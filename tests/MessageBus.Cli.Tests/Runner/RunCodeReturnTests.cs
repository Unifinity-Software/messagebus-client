using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Attachments.Client;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Attachments;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;
using MessageBus.Mcp;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class RunCodeReturnTests
{
    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Theory]
    [InlineData(false, false, false, false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, false, false, true)]
    [InlineData(false, false, false, true, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, true, true, true)]
    public async Task Actual_return_uses_local_keys_and_fresh_negotiated_delivery_attempt(bool roleReply, bool rotationNeeded, bool checkedReturn, bool supportsAttempt = false, bool staleAttempt = false, bool fullBundle = false)
    {
        Assert.SkipWhen(checkedReturn && !OperatingSystem.IsLinux(), "Checked return uses the Linux-only verification toolchain.");
        Assert.SkipWhen(CommandLocator.Find("git", Environment.GetEnvironmentVariable, []) is null, "git isn't installed.");
        await using var h = new RunnerHarness();
        using var run = RunWorkspace.Create(h.WorkRoot, "dlv_code", 1);
        Directory.CreateDirectory(run.WorkTree);
        await Git.RunAsync(run.WorkTree, ["init", "--quiet", "--initial-branch=main"]);
        File.WriteAllText(Path.Combine(run.WorkTree, "change.txt"), "change");
        await Git.RunAsync(run.WorkTree, ["add", "."]);
        await Git.RunAsync(run.WorkTree, ["-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Change"]);
        var task = h.Task("dlv_code");
        task = task with { Attempt = 3, Envelope = task.Envelope with
        {
            ReplyTo = roleReply ? new RoleTarget { Role = "reviewer" } : null,
            Sender = new SenderIdentity { Kind = SenderKind.Person, SubjectId = "person_test" },
        } };
        string? baseCommit = null;
        if (checkedReturn || fullBundle)
        {
            baseCommit = await Git.CommitIdAsync(run.WorkTree, "HEAD");
            File.AppendAllText(Path.Combine(run.WorkTree, "change.txt"), " checked");
            await Git.RunAsync(run.WorkTree, ["add", "."]);
            await Git.RunAsync(run.WorkTree, ["-c", "user.name=Test", "-c", "user.email=test@example.com",
                "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Checked change"]);
            if (checkedReturn)
                run.VerifiedReturnRevision = await Git.CommitIdAsync(run.WorkTree, "HEAD");
            if (fullBundle)
                run.FullBundleFriction = new FrictionNote { Kind = "missing-context", Note = "The repository could not be reached." };
        }
        await AssertReturnAsync(h, run, task, "main", baseCommit, roleReply, rotationNeeded, supportsAttempt, staleAttempt);
    }

    internal static async Task AssertReturnAsync(RunnerHarness h, RunWorkspace run,
        MessageBus.Contracts.Runner.TaskDelivery task, string branch, string? baseCommit,
        bool roleReply = false, bool rotationNeeded = false, bool supportsAttempt = true, bool staleAttempt = false)
    {
        using var pair = KeyPair.GenerateP256();
        var key = new PublicKeyInfo
        {
            KeyId = roleReply ? "ek_reviewer" : "ek_workspace", Scope = roleReply ? KeyScope.Role : KeyScope.Workspace, Owner = roleReply ? "reviewer" : null, Version = 1,
            Algorithm = pair.PublicKey.Algorithm, PublicKey = pair.PublicKey.ToBase64(),
            Fingerprint = pair.PublicKey.Fingerprint, CreatedAt = h.Time.GetUtcNow(),
        };
        using var server = new FakeServer();
        using var http = new HttpClient(server) { BaseAddress = new Uri(FakeServer.Url) };
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Test");
        var advertisedAttempt = !supportsAttempt;
        server.On(HttpMethod.Get, "/api/server", r =>
        {
            Assert.Equal("Test", r.Header("Authorization"));
            Assert.Contains("no-cache", r.Header("Cache-Control"));
            return Json(advertisedAttempt
                ? """{"product":"messagebus.ai","capabilities":["settleAttemptV1"]}"""
                : """{"product":"messagebus.ai"}""");
        });
        server.On(HttpMethod.Get, "/api/w/ws_test/threads/thr_dlv_code/messages", _ => Json("[]"));
        server.On(HttpMethod.Get, "/api/w/ws_test/keys", _ => FakeServer.Json<IReadOnlyList<PublicKeyInfo>>([key]));
        server.On(HttpMethod.Post, "/api/w/ws_test/attachments/upload-link", _ => FakeServer.Json(new UploadLink
        {
            AttachmentId = "att_return", Url = FakeServer.Url + "/upload", MaxBytes = long.MaxValue, ExpiresAt = h.Time.GetUtcNow().AddHours(1),
        }));
        server.On(HttpMethod.Get, "/api/caller", _ => Json("""{"subjectId":"person_test","permissions":[]}"""));
        server.On(HttpMethod.Get, "/api/w/ws_test/keys/rotation", _ => FakeServer.Json<IReadOnlyList<KeyRotationState>>([new() { Scope = key.Scope, Owner = key.Owner, Key = key, RotationNeeded = rotationNeeded }]));
        using var keys = new MachineKeys(http, "ws_test", null, run.Root, TextWriter.Null, h.Time);
        byte[]? uploaded = null;
        server.On(HttpMethod.Put, "/upload", r =>
        {
            Assert.Null(r.Header("Authorization"));
            Assert.DoesNotContain(r.Headers.Keys, name => name.StartsWith("X-Mbus-", StringComparison.OrdinalIgnoreCase));
            uploaded = r.Body;
            return new HttpResponseMessage(HttpStatusCode.Created);
        });
        SettleInput? sent = null;
        var settleCalls = 0;
        var acceptedCalls = 0;
        server.On(HttpMethod.Post, "/mcp", r =>
        {
            Assert.Equal("Test", r.Header("Authorization"));
            var request = JsonNode.Parse(r.BodyText)!;
            if (request["id"] is null) return new HttpResponseMessage(HttpStatusCode.Accepted);
            JsonNode result;
            if ((string?)request["method"] == "initialize")
            {
                result = JsonNode.Parse("""{"protocolVersion":"2025-11-25","serverInfo":{"name":"test","version":"1"},"capabilities":{"tools":{}}}""")!;
            }
            else if ((string?)request["method"] == "tools/call")
            {
                Assert.Equal("settle", (string?)request["params"]?["name"]);
                settleCalls++;
                var arguments = request["params"]!["arguments"]!;
                Assert.Equal(supportsAttempt, arguments.AsObject().ContainsKey("attempt"));
                sent = MessageBusJson.Deserialize<SettleInput>(arguments.ToJsonString());
                Assert.Equal(supportsAttempt ? task.Attempt : (int?)null, sent!.Attempt);
                if (staleAttempt && sent.Attempt != task.Attempt + 1)
                {
                    return Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(),
                        ["result"] = JsonNode.Parse("""{"isError":true,"structuredContent":{"code":"conflict","message":"stale delivery attempt","retryable":false},"content":[{"type":"text","text":"stale delivery attempt"}]}""") }.ToJsonString());
                }
                acceptedCalls++;
                result = JsonNode.Parse("""{"content":[{"type":"text","text":"{\"status\":\"working\",\"replyMessageId\":\"msg_return\"}"}]}""")!;
            }
            else
            {
                return Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" } }.ToJsonString());
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        });

        if (rotationNeeded)
        {
            await Assert.ThrowsAsync<McpToolException>(() => RunCodeReturn.ReturnAsync(http, server.Links, "ws_test", run, task, branch, baseCommit, TestContext.Current.CancellationToken, keys.RecipientAsync));
            Assert.DoesNotContain(server.Requests, r => r.Method == HttpMethod.Put);
            Assert.True(Directory.Exists(run.WorkTree));
            return;
        }

        // A previous discovery must never pin this operation to stale target capabilities.
        Assert.Equal(!supportsAttempt, await TargetCapabilities.SupportsAsync(http, "settleAttemptV1", TestContext.Current.CancellationToken));
        advertisedAttempt = supportsAttempt;

        if (staleAttempt)
        {
            var verified = run.VerifiedReturnRevision;
            var error = await Assert.ThrowsAsync<McpToolException>(() => RunCodeReturn.ReturnAsync(http, server.Links, "ws_test", run, task, branch, baseCommit,
                TestContext.Current.CancellationToken, keys.RecipientAsync));
            Assert.Equal(McpErrorCodes.Conflict, error.Error.Code);
            Assert.NotNull(uploaded);
            Assert.Equal(1, settleCalls);
            Assert.Equal(0, acceptedCalls);
            Assert.Equal(verified, run.VerifiedReturnRevision);
            Assert.True(Directory.Exists(run.WorkTree));
            return;
        }

        Assert.True(await RunCodeReturn.ReturnAsync(http, server.Links, "ws_test", run, task, branch, baseCommit, TestContext.Current.CancellationToken, keys.RecipientAsync));
        Assert.Equal(2, server.Requests.Count(r => r.Uri.AbsolutePath == "/api/server"));
        Assert.NotNull(uploaded);
        Assert.NotNull(sent);
        Assert.True(sent.ReplyOnly);
        Assert.Equal(task.DeliveryId, sent.DeliveryId);
        Assert.Equal(branch, Assert.Single(sent.Attachments).Branch);
        Assert.Equal(key.KeyId, sent.SealedReply?.Encryption.KeyId);
        Assert.Equal(key.KeyId, Assert.Single(sent.Attachments).Encryption?.KeyId);
        Assert.True(Directory.Exists(run.WorkTree));

        if (run.FullBundleFriction is { } note)
        {
            using var ring = new KeyRing().Add(key.KeyId, KeyPair.FromPkcs8(pair.ExportPkcs8()));
            using var cipher = new MemoryStream(uploaded);
            using var plaintext = new MemoryStream();
            var attachment = Assert.Single(sent.Attachments);
            await MessageCrypto.OpenAsync(cipher, plaintext, attachment.Encryption!, ring);
            var bundle = Path.Combine(run.Root, "full-return.bundle");
            File.WriteAllBytes(bundle, plaintext.ToArray());
            Assert.NotNull(baseCommit);
            Assert.Null(attachment.BaseCommit);
            Assert.Empty(GitBundles.ReadHeader(bundle).Prerequisites);
            Assert.Equal(note, Assert.Single(BodyCrypto.Open(sent.SealedReply!, ring).Friction));
            Assert.Equal(note.Kind, Assert.Single(sent.Result.FrictionKinds));
        }

        if (run.VerifiedReturnRevision is { } expected)
        {
            Assert.Equal(expected, Assert.Single(sent.Result.Refs).Sha);
            using var ring = new KeyRing().Add(key.KeyId, KeyPair.FromPkcs8(pair.ExportPkcs8()));
            using var cipher = new MemoryStream(uploaded);
            using var plaintext = new MemoryStream();
            await MessageCrypto.OpenAsync(cipher, plaintext, Assert.Single(sent.Attachments).Encryption!, ring);
            var bundle = Path.Combine(run.Root, "returned.bundle");
            File.WriteAllBytes(bundle, plaintext.ToArray());
            var heads = await Git.RunAsync(run.WorkTree, ["bundle", "list-heads", bundle]);
            Assert.Equal(expected + " refs/heads/" + branch, heads.Trim());
            // A moved branch must fail before a second upload or reply.
            await Git.RunAsync(run.WorkTree, ["update-ref", "refs/heads/" + branch, baseCommit!]);
            Assert.False(await RunCodeReturn.ReturnAsync(http, server.Links, "ws_test", run, task, branch, baseCommit,
                TestContext.Current.CancellationToken, keys.RecipientAsync));
            Assert.Single(server.Requests, r => r.Method == HttpMethod.Put);
            return;
        }

        // Match the fake agent's settle: a bundle attachment, without a commit ref.
        var accepted = task.Envelope with
        {
            Id = "msg_return", Type = task.Envelope.Type + ".reply", ParentId = task.Envelope.Id,
            Sender = new SenderIdentity { Kind = SenderKind.Agent, AgentId = task.AgentId },
            Refs = [], Attachments = sent.Attachments,
        };
        server.On(HttpMethod.Get, "/api/w/ws_test/threads/thr_dlv_code/messages", _ => Json("[" + MessageBusJson.Serialize(accepted) + "]"));
        Assert.True(await RunCodeReturn.ReturnAsync(http, server.Links, "ws_test", run, task, branch, baseCommit, TestContext.Current.CancellationToken, keys.RecipientAsync));
        Assert.Single(server.Requests, r => r.Method == HttpMethod.Put);
    }
}
