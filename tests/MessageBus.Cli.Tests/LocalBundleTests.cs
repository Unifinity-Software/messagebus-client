using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Attachments.Client;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Sandbox;
using MessageBus.Cli.Tests.Runner;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Attachments;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;
using MessageBus.Mcp;
using ModelContextProtocol.Protocol;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class LocalBundleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly AgentTarget Target = new() { AgentId = "agt_test" };

    [Theory]
    [InlineData(typeof(SendInput))]
    [InlineData(typeof(SettleInput))]
    public void Expected_head_is_advertised_only_by_the_local_attachment_schema(Type input)
    {
        var local = McpToolCatalog.Schema(input, local: true);
        var expected = local.GetProperty("properties").GetProperty("attach").GetProperty("items")
            .GetProperty("properties").GetProperty("expectedHead");
        Assert.Equal("^([0-9a-fA-F]{40}|[0-9a-fA-F]{64})$", expected.GetProperty("pattern").GetString());

        var hosted = McpToolCatalog.Schema(input, local: false);
        Assert.DoesNotContain("expectedHead", hosted.GetRawText());
        Assert.All(McpToolCatalog.Tools(local: false), tool => Assert.DoesNotContain("expectedHead", tool.Description));
    }

    [Fact]
    public async Task Batch_uses_final_attachment_order_and_companions_can_be_fetched_into_a_cold_repository()
    {
        using var machine = new TestMachine();
        using var pair = KeyPair.GenerateP256();
        using var http = ConfigureServer(machine, pair);
        await using var tools = OpenTools(machine, http);
        var repo = await InitAsync(machine);
        var previous = await CommitAsync(repo, new string('a', 5000));
        await Git.RunAsync(repo, ["switch", "--quiet", "-c", "sibling"], Ct);
        var sibling = await CommitAsync(repo, "Sibling");
        await Git.RunAsync(repo, ["switch", "--quiet", "main"], Ct);
        var spec = new AttachSpec { Format = AttachmentFormat.GitBundle, Path = repo };
        var first = await CallAsync(tools, McpToolNames.Send, Message(spec));
        Assert.False(first.IsError ?? false, first.StructuredContent.ToString());
        var head = await CommitAsync(repo, "Next");
        spec = spec with { ExpectedHead = head.ToUpperInvariant() };
        var preuploaded = new AttachmentRef
        {
            Id = "att_existing", Name = "existing.bundle", Format = AttachmentFormat.GitBundle,
            SizeBytes = 1, Sha256 = new string('0', 64),
        };
        var leadingFile = Path.Combine(machine.Home, "note.txt");
        await File.WriteAllTextAsync(leadingFile, "Note", Ct);

        var call = await CallAsync(tools, McpToolNames.SendMany, new SendManyInput
        {
            Messages =
            [
                Message(spec) with { Attachments = [preuploaded] },
                Message(new AttachSpec { Format = AttachmentFormat.File, Path = leadingFile }, spec, spec with { Base = sibling }),
                Message(spec with { Base = "" }) with { Attachments = [preuploaded] },
            ],
        });

        Assert.False(call.IsError ?? false, call.StructuredContent.ToString());
        var result = McpToolCatalog.ReadResult<SendManyResult>(call);
        Assert.Contains(previous, result.Results[1].Notice);
        Assert.DoesNotContain(sibling, result.Results[1].Notice);
        var calls = machine.Server.Requests.Where(r => r.Uri.AbsolutePath == "/mcp").Select(r => JsonNode.Parse(r.BodyText));
        var batch = Assert.Single(calls, r => (string?)r?["params"]?["name"] == McpToolNames.SendMany);
        var wire = MessageBusJson.Deserialize<SendManyInput>(batch!["params"]!["arguments"]!.ToJsonString())!;
        Assert.DoesNotContain("expectedHead", batch.ToJsonString());
        Assert.All(wire.Messages, message =>
        {
            Assert.Empty(message.Attach);
            Assert.Null(message.Body);
            Assert.NotNull(message.SealedBody);
        });
        Assert.Equal(preuploaded, wire.Messages[0].Attachments[0]);
        var companion = wire.Messages[0].Attachments[1];
        Assert.Null(companion.BaseCommit);
        Assert.Equal(AttachmentFormat.File, wire.Messages[1].Attachments[0].Format);
        Assert.Equal(previous, wire.Messages[1].Attachments[1].BaseCommit);
        Assert.Equal(sibling, wire.Messages[1].Attachments[2].BaseCommit);
        Assert.Null(wire.Messages[2].Attachments[1].BaseCommit);

        var ciphertext = machine.Server.Single(HttpMethod.Put, $"/blobs/{companion.Id}").Body;
        using var ring = new KeyRing();
        ring.Add(companion.Encryption!.KeyId, KeyPair.FromPem(pair.ExportPem()));
        var bundlePath = Path.Combine(machine.Home, "companion.bundle");
        await using (var output = File.Create(bundlePath))
            await MessageCrypto.OpenAsync(new MemoryStream(ciphertext), output, companion.Encryption, ring, Ct);
        Assert.Empty(GitBundles.ReadHeader(bundlePath).Prerequisites);
        Assert.Equal(head, await GitBundles.FetchAsync(bundlePath, Path.Combine(machine.Home, "cold"), "main", ct: Ct));
    }

    [Fact]
    public async Task Branch_movement_during_packaging_refuses_the_batch_before_uploading_the_bundle_or_sending()
    {
        using var machine = new TestMachine();
        using var pair = KeyPair.GenerateP256();
        using var http = ConfigureServer(machine, pair);
        var repo = await InitAsync(machine);
        var expected = await CommitAsync(repo, "Reviewed");
        var actual = await CommitAsync(repo, "Advanced");
        await Git.RunAsync(repo, ["update-ref", "refs/heads/main", expected], Ct);
        await using var tools = OpenTools(machine, http, start =>
        {
            if (start.ArgumentList.Take(2).SequenceEqual(["bundle", "create"]))
                Git.RunAsync(repo, ["update-ref", "refs/heads/main", actual], Ct).GetAwaiter().GetResult();
        });
        var file = Path.Combine(machine.Home, "note.txt");
        await File.WriteAllTextAsync(file, "Note", Ct);

        var call = await CallAsync(tools, McpToolNames.SendMany, new SendManyInput
        {
            Messages =
            [
                Message(new AttachSpec { Format = AttachmentFormat.File, Path = file }),
                Message(new AttachSpec { Format = AttachmentFormat.GitBundle, Path = repo, ExpectedHead = expected }),
            ],
        });

        Assert.True(call.IsError);
        var error = call.StructuredContent!.Value.Deserialize<McpError>(MessageBusJson.Options)!;
        Assert.Equal("messages[1].attach[0].expectedHead", error.Field);
        Assert.Contains(expected, error.Message);
        Assert.Contains(actual, error.Message);
        Assert.Single(machine.Server.Requests, r => r.Uri.AbsolutePath == "/api/w/ws_test/attachments/upload-link");
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath == "/mcp");
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, true)]
    public async Task Workflow_bundle_uses_the_connection_status_and_checkout_result(
        bool hasCredential, bool publicRepository, bool ownerGit, bool credentialServiceFailed)
    {
        await WindowsRunCapabilities.RequireGitShellAsync();
        var git = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []);
        Assert.SkipWhen(git is null, "git isn't installed.");
        using var machine = new TestMachine();
        using var pair = KeyPair.GenerateP256();
        using var http = ConfigureServer(machine, pair);
        await using var harness = new RunnerHarness();
        var task = harness.Task("dlv_code", environment: new RunEnvironment { Checkout = CheckoutMode.Bundle });
        var delivery = task with
        {
            Envelope = task.Envelope with { Refs = [new Ref { Kind = "git.repo", Url = "https://git.example.test/team/private.git" }] },
            StepContext = new StepContext
            {
                JobId = "job_test", StepId = "code", StepName = "Code", Round = 1, AllowedOutcomes = ["done"], Inputs = [],
            },
        };
        var profile = new PermissionProfile { Name = "worker", CanPush = true };
        machine.Server.On(HttpMethod.Post, ServiceRoutes.RepoCredential, request =>
        {
            var issuance = MessageBusJson.Deserialize<RunnerRepoCredentialRequest>(request.BodyText)!;
            Assert.Equal(!ownerGit, issuance.CanPush);
            if (credentialServiceFailed)
                return new(HttpStatusCode.ServiceUnavailable);
            return hasCredential
                ? FakeServer.Json(new RunnerRepoCredential
                {
                    UserName = "fixture", Secret = "fixture-password", ExpiresAt = DateTimeOffset.MaxValue,
                })
                : new(HttpStatusCode.NoContent);
        });
        var isolation = RunIsolation.Create(new RunIsolationOptions
        {
            WorkRoot = harness.WorkRoot, HomeDirectory = harness.Home, Requested = IsolationLevel.Basic,
            Credentials = new ServiceRepoCredentials(http, TextWriter.Null),
            UseOwnerGit = _ => ownerGit,
        }, TextWriter.Null);
        var remote = delivery.Envelope.Refs[0].Url!;
        var localRemote = Path.Combine(harness.Scratch, "remote");
        string? remoteBase = null;
        if (publicRepository || ownerGit)
        {
            Directory.CreateDirectory(localRemote);
            await Git.RunAsync(localRemote, ["init", "--quiet", "--initial-branch=main"], Ct);
            remoteBase = await CommitAsync(localRemote, "Base");
        }

        using var producer = RunWorkspace.Create(harness.WorkRoot, delivery.DeliveryId, delivery.Attempt);
        await isolation.PrepareAsync(producer, delivery, profile, Ct);
        try
        {
            machine.Server.Single(HttpMethod.Post, ServiceRoutes.RepoCredential);
            Assert.Equal(hasCredential && !ownerGit, File.Exists(Path.Combine(producer.Root, RunGitConfig.CredentialFileName)));
            if (!hasCredential || ownerGit)
            {
                var checkout = await producer.CheckOutAsync(delivery, _ => git, null, Ct, launch =>
                    TestRepositoryRemote.Map(isolation.Wrap(launch, producer, profile), remote, localRemote));
                Assert.Null(checkout);
                Assert.Equal(remoteBase is null, producer.RepositoryUnavailable);
                await Git.RunAsync(producer.WorkTree, ["symbolic-ref", "HEAD", "refs/heads/main"], Ct);
            }
            else
            {
                Directory.CreateDirectory(producer.WorkTree);
                await Git.RunAsync(producer.WorkTree, ["init", "--quiet", "--initial-branch=main"], Ct);
            }
            // Simulate the agent obtaining the history and making a branch in its worktree.
            var baseCommit = remoteBase ?? await CommitAsync(producer.WorkTree, "Base");
            await Git.RunAsync(producer.WorkTree, ["switch", "--quiet", "-c", "feature/code"], Ct);
            var head = await CommitAsync(producer.WorkTree, "Workflow edit");
            await using var tools = new LocalTools(ToolOptions(machine, http) with
            {
                Delivery = delivery, SettleFile = producer.SettleFile, Scope = RunToolScope.For(delivery, producer.Root),
                WorkingDirectory = producer.WorkTree, FullBundleFriction = producer.FullBundleFriction,
            });
            var input = new SettleInput
            {
                ThreadId = delivery.Envelope.ThreadId, DeliveryId = delivery.DeliveryId,
                Reply = new MessageBody { Summary = "Code ready" },
                Result = new SettleResult { Status = SettleStatus.Succeeded },
                Attach = [new AttachSpec { Format = AttachmentFormat.GitBundle, Base = baseCommit, ExpectedHead = head }],
            };
            using var json = JsonDocument.Parse(MessageBusJson.Serialize(input));
            var arguments = json.RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
            arguments["outcome"] = JsonSerializer.SerializeToElement("done");
            var call = await tools.CallAsync("codex", McpToolNames.Settle, arguments, Ct);
            Assert.False(call.IsError ?? false, call.StructuredContent.ToString());

            var request = Assert.Single(machine.Server.Requests.Select(r => r.Uri.AbsolutePath == "/mcp" ? JsonNode.Parse(r.BodyText) : null),
                node => (string?)node?["params"]?["name"] == McpToolNames.Settle);
            var sent = MessageBusJson.Deserialize<SettleInput>(request!["params"]!["arguments"]!.ToJsonString())!;
            var attachment = Assert.Single(sent.Attachments);
            var expectThin = hasCredential || publicRepository || (ownerGit && credentialServiceFailed);
            Assert.Equal(expectThin ? baseCommit : null, attachment.BaseCommit);
            using var keys = new KeyRing();
            keys.Add(attachment.Encryption!.KeyId, KeyPair.FromPem(pair.ExportPem()));
            var body = BodyCrypto.Open(sent.SealedReply!, keys);
            var recorded = MessageBusJson.Deserialize<SettleInput>(await File.ReadAllTextAsync(producer.SettleFile, Ct))!;
            var bundle = Path.Combine(machine.Home, "returned.bundle");
            await using (var file = File.Create(bundle))
                await MessageCrypto.OpenAsync(new MemoryStream(machine.Server.Single(HttpMethod.Put, $"/blobs/{attachment.Id}").Body),
                    file, attachment.Encryption, keys, Ct);
            var prerequisites = GitBundles.ReadHeader(bundle).Prerequisites;
            if (expectThin)
            {
                Assert.Contains(baseCommit, prerequisites);
                Assert.Empty(body.Friction);
                Assert.Empty(recorded.Result.FrictionKinds);
                return;
            }
            Assert.Empty(prerequisites);
            Assert.Equal(credentialServiceFailed
                ? "Repository checkout failed: code was passed as a full bundle"
                : "No repository connection for git.example.test/team/private: code was passed as a full bundle",
                Assert.Single(body.Friction).Note);
            Assert.Equal("missing-context", Assert.Single(recorded.Result.FrictionKinds));

            var source = new WorkflowBundleSource(new ReceivedMessage
            {
                Envelope = delivery.Envelope with { Attachments = [attachment] }, Body = body,
            }, bundle);
            var consumerRemote = ownerGit ? Path.Combine(harness.Scratch, "unreachable") : localRemote;
            await using var consumer = new RunnerHarness(bundles: source, sandbox: root =>
                new RepositorySandbox(
                    RunIsolation.Create(new RunIsolationOptions { WorkRoot = root, HomeDirectory = machine.Home, Requested = IsolationLevel.Basic }, TextWriter.Null),
                    git!, remote, consumerRemote));
            var review = consumer.Task("dlv_review", environment: new RunEnvironment { Checkout = CheckoutMode.Clone });
            review = review with
            {
                Envelope = review.Envelope with { Refs = delivery.Envelope.Refs },
                StepContext = delivery.StepContext with
                {
                    StepId = "review", Inputs = [new StepInput { FromStep = "code", What = "branch", ThreadId = delivery.Envelope.ThreadId }],
                },
            };
            var accepted = await consumer.DeliverAsync(review);
            Assert.True(accepted.Accepted, $"{accepted.Reason}: {accepted.Detail}");
            var result = await consumer.Service.SettledAsync(review.DeliveryId);
            await consumer.Engine.StopAsync();
            Assert.True(result.Result.Status == SettleStatus.Succeeded, result.Result.Error);
            var work = consumer.WorkTreeOf(review.DeliveryId);
            Assert.Equal(head, await Git.CommitIdAsync(work, "HEAD", Ct));
            Assert.Equal("feature/code", (await Git.RunAsync(work, ["branch", "--show-current"], Ct)).Trim());
            if (publicRepository)
            {
                Assert.Equal(remote, (await Git.RunAsync(work, ["config", "--get", "remote.origin.url"], Ct)).Trim());
                Assert.Equal(baseCommit, await Git.CommitIdAsync(work, "refs/heads/main", Ct));
            }
        }
        finally { isolation.Ended(producer); }
    }

    private sealed class RepositorySandbox(IRunSandbox inner, string git, string remote, string local) : IRunSandbox
    {
        public Task PrepareAsync(RunWorkspace workspace, TaskDelivery delivery, PermissionProfile profile, CancellationToken cancellationToken) =>
            inner.PrepareAsync(workspace, delivery, profile, cancellationToken);

        public ProcessLaunch Wrap(ProcessLaunch launch, RunWorkspace workspace, PermissionProfile profile)
        {
            var wrapped = inner.Wrap(launch, workspace, profile);
            return launch.FileName == git ? TestRepositoryRemote.Map(wrapped, remote, local) : wrapped;
        }

        public Task EndedAsync(RunWorkspace workspace) => inner.EndedAsync(workspace);
    }

    private sealed class WorkflowBundleSource(ReceivedMessage result, string bundle) : IBundleSource
    {
        public Task<ReceivedMessage?> ReadStepResultAsync(TaskDelivery delivery, StepInput input, CancellationToken cancellationToken) =>
            Task.FromResult<ReceivedMessage?>(result);

        public Task<string?> FetchStepAttachmentAsync(AttachmentRef attachment, string path, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(bundle);

        public Task<string?> FetchAsync(TaskDelivery delivery, AttachmentRef attachment, string runDirectory, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pinned_runner_settle_discards_a_kept_reply_when_packaging_fails(bool otherAttachmentFails)
    {
        using var machine = new TestMachine();
        using var pair = KeyPair.GenerateP256();
        using var http = ConfigureServer(machine, pair);
        var repo = await InitAsync(machine);
        var tip = await CommitAsync(repo, "Tip");
        var delivery = new TaskDelivery
        {
            DeliveryId = "dlv_test", Attempt = 1, LeaseExpiresAt = machine.Time.GetUtcNow().AddHours(1),
            AgentId = "agt_test", RunEnvironment = new(), HeartbeatSeconds = 30,
            Envelope = new MessageEnvelope
            {
                Id = "msg_task", Type = "task.implement", Source = "agent://ws_test/agt_test", Time = machine.Time.GetUtcNow(),
                WorkspaceId = "ws_test", Target = Target, ThreadId = "thr_test",
                Sender = new SenderIdentity { Kind = SenderKind.Agent, SubjectId = "person_test", AgentId = Target.AgentId },
                Body = new BodyRef { ContentType = MessageLimits.BodyContentType, SizeBytes = 0, Sha256 = "" },
            },
        };
        var settleFile = Path.Combine(machine.Home, "settle.json");
        var options = ToolOptions(machine, http) with
        {
            Delivery = delivery, SettleFile = settleFile, Scope = RunToolScope.For(delivery, machine.Home),
        };
        await using var tools = new LocalTools(options);
        var missing = new AttachSpec { Format = AttachmentFormat.File, Path = Path.Combine(repo, "missing.txt") };
        var input = new SettleInput
        {
            ThreadId = delivery.Envelope.ThreadId, DeliveryId = delivery.DeliveryId,
            Reply = new MessageBody { Summary = "Result" },
            Result = new SettleResult { Status = SettleStatus.Succeeded },
            Attach = [missing],
        };
        var unpinned = await CallAsync(tools, McpToolNames.Settle, input);
        Assert.True(unpinned.IsError);
        Assert.True(File.Exists(KeptAnswer.KeptFile(settleFile)), unpinned.StructuredContent.ToString());

        var pinned = new AttachSpec
        {
            Format = AttachmentFormat.GitBundle, Path = repo, ExpectedHead = tip, Base = otherAttachmentFails ? "" : tip,
        };
        var failed = await CallAsync(tools, McpToolNames.Settle, input with
        {
            Attach = otherAttachmentFails ? [missing, pinned] : [pinned],
        });
        Assert.True(failed.IsError);
        var error = failed.StructuredContent!.Value.Deserialize<McpError>(MessageBusJson.Options)!;
        Assert.Contains(otherAttachmentFails ? "file" : "Nothing to send", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(settleFile));
        Assert.False(await LocalServer.SendKeptAnswerAsync(options, Ct));
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath == "/mcp");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fetch_reports_missing_commits_and_preserves_malformed_bundle_errors(bool malformed)
    {
        using var machine = new TestMachine();
        using var pair = KeyPair.GenerateP256();
        using var http = ConfigureServer(machine, pair);
        await using var tools = OpenTools(machine, http);
        var repo = await InitAsync(machine);
        var prerequisite = await CommitAsync(repo, "Base");
        await CommitAsync(repo, "Tip");
        var path = Path.Combine(machine.Home, "incremental.bundle");
        await GitBundles.CreateAsync(repo, path, "main", prerequisite, Ct);
        var bytes = malformed ? Encoding.UTF8.GetBytes("not a bundle") : await File.ReadAllBytesAsync(path, Ct);
        var attachment = new AttachmentRef
        {
            Id = "att_fetch", Name = "incremental.bundle", Format = AttachmentFormat.GitBundle, Branch = "main",
            BaseCommit = prerequisite, SizeBytes = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        };
        var envelope = new MessageEnvelope
        {
            Id = "msg_fetch", Type = "task.implement", Source = "person://ws_test/person_test", Time = machine.Time.GetUtcNow(),
            WorkspaceId = "ws_test", Target = Target, ThreadId = "thr_fetch",
            Sender = new SenderIdentity { Kind = SenderKind.Person, SubjectId = "person_test" },
            Body = new BodyRef { ContentType = MessageLimits.BodyContentType, SizeBytes = 0, Sha256 = "" }, Attachments = [attachment],
        };
        machine.Server.On(HttpMethod.Get, "/api/w/ws_test/threads/thr_fetch/messages",
            _ => Json("[" + MessageBusJson.Serialize(envelope) + "]"));
        machine.Server.On(HttpMethod.Post, "/api/w/ws_test/attachments/att_fetch/download-link",
            _ => FakeServer.Json(new DownloadLink { Id = attachment.Id, Url = FakeServer.Url + "/fetch", ExpiresAt = machine.Time.GetUtcNow().AddHours(1) }));
        machine.Server.On(HttpMethod.Get, "/fetch", _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });

        var call = await CallAsync(tools, McpToolNames.FetchAttachment, new FetchAttachmentInput
        {
            ThreadId = envelope.ThreadId, AttachmentId = attachment.Id, Repository = Path.Combine(machine.Home, "cold"),
        });

        Assert.True(call.IsError);
        var error = call.StructuredContent!.Value.Deserialize<McpError>(MessageBusJson.Options)!;
        if (malformed)
        {
            Assert.Contains("bundle", error.Message);
            Assert.DoesNotContain("resend", error.Message);
        }
        else
        {
            Assert.Equal("repository", error.Field);
            Assert.Contains(prerequisite, error.Message);
            Assert.Contains("cold", error.Message);
            Assert.Contains("base: \"\"", error.Message);
        }
    }

    [Fact]
    public async Task Fetch_recovers_a_newer_bundle_base_in_an_older_shallow_clone()
    {
        using var machine = new TestMachine();
        var repo = await InitAsync(machine);
        await CommitAsync(repo, "Old base");
        var remote = Path.Combine(machine.Home, "origin.git");
        await Git.RunAsync(repo, ["clone", "--quiet", "--bare", repo, remote], Ct);
        var receiver = Path.Combine(machine.Home, "receiver");
        await Git.RunAsync(repo, ["clone", "--quiet", "--depth=1", new Uri(remote).AbsoluteUri, receiver], Ct);
        Assert.Equal("true", (await Git.RunAsync(receiver, ["rev-parse", "--is-shallow-repository"], Ct)).Trim());

        var @base = await CommitAsync(repo, "New base");
        await Git.RunAsync(remote, ["fetch", "--quiet", "--update-head-ok", repo, "main:main"], Ct);
        var tip = await CommitAsync(repo, "Worker result");
        var bundle = await GitBundles.CreateAsync(repo, Path.Combine(machine.Home, "incremental.bundle"), "main", @base, Ct);

        Assert.Equal(tip, await GitBundles.FetchAsync(bundle.Path, receiver, "main", ct: Ct));
        Assert.Equal(@base, await Git.CommitIdAsync(receiver, "refs/remotes/mbus/main^", Ct));
    }

    [Fact]
    public async Task Explicit_local_only_base_is_kept_when_a_remote_is_configured()
    {
        using var machine = new TestMachine();
        var repo = await InitAsync(machine);
        var published = await CommitAsync(repo, "Published base");
        var remote = Path.Combine(machine.Home, "origin.git");
        await Git.RunAsync(repo, ["clone", "--quiet", "--bare", repo, remote], Ct);
        await Git.RunAsync(repo, ["remote", "add", "origin", remote], Ct);
        await Git.RunAsync(repo, ["fetch", "--quiet", "origin"], Ct);
        Assert.Equal(published, await Git.CommitIdAsync(repo, "refs/remotes/origin/main", Ct));
        var localBase = await CommitAsync(repo, "Local base");
        await CommitAsync(repo, "Worker result");

        var bundle = await GitBundles.CreateAsync(repo, Path.Combine(machine.Home, "explicit.bundle"), "main", localBase, Ct);

        Assert.Equal(localBase, bundle.BaseCommit);
        Assert.Equal(localBase, Assert.Single(bundle.Prerequisites));
    }

    [Fact]
    public async Task Local_only_base_uses_the_nearest_published_ancestor_or_whole_history()
    {
        using var machine = new TestMachine();
        var repo = await InitAsync(machine);
        var older = await CommitAsync(repo, "Older published commit");
        var published = await CommitAsync(repo, "Published base");
        await Git.RunAsync(repo, ["branch", "older", older], Ct);
        var remote = Path.Combine(machine.Home, "origin.git");
        await Git.RunAsync(repo, ["clone", "--quiet", "--bare", repo, remote], Ct);
        await Git.RunAsync(repo, ["remote", "add", "origin", remote], Ct);
        await Git.RunAsync(repo, ["fetch", "--quiet", "origin"], Ct);
        var localBase = await CommitAsync(repo, "Local base");
        var tip = await CommitAsync(repo, "Worker result");
        await Git.RunAsync(repo, ["update-ref", "refs/remotes/mbus/returned", localBase], Ct);
        var bundle = await GitBundles.CreateAsync(repo, Path.Combine(machine.Home, "incremental.bundle"), "main", localBase, usePublishedBaseFallback: true, Ct);

        Assert.Equal(published, bundle.BaseCommit);
        Assert.Equal(published, Assert.Single(bundle.Prerequisites));
        var receiver = Path.Combine(machine.Home, "receiver");
        await Git.RunAsync(repo, ["clone", "--quiet", remote, receiver], Ct);
        Assert.Equal(tip, await GitBundles.FetchAsync(bundle.Path, receiver, "main", ct: Ct));

        await Git.RunAsync(repo, ["checkout", "--quiet", "--orphan", "unrelated"], Ct);
        var unrelatedBase = await CommitAsync(repo, "Unrelated root");
        var unrelatedTip = await CommitAsync(repo, "Unrelated result");
        var whole = await GitBundles.CreateAsync(repo, Path.Combine(machine.Home, "whole.bundle"), "unrelated", unrelatedBase, usePublishedBaseFallback: true, Ct);

        Assert.Null(whole.BaseCommit);
        Assert.Empty(whole.Prerequisites);
        Assert.Equal(unrelatedTip, await GitBundles.FetchAsync(whole.Path, Path.Combine(machine.Home, "empty"), "unrelated", ct: Ct));
    }

    [Fact]
    public async Task Retry_refuses_multiple_code_attachments_and_explains_how_to_resend()
    {
        using var machine = new TestMachine();
        using var pair = KeyPair.GenerateP256();
        var body = MessageBusJson.Serialize(new MessageBody { Summary = "Check both repositories" });
        var root = new MessageEnvelope
        {
            Id = "msg_retry", Type = "task.implement", Source = "person://ws_test/person_test",
            Time = machine.Time.GetUtcNow(), WorkspaceId = "ws_test", Target = Target, ThreadId = "thr_retry",
            Sender = new SenderIdentity { Kind = SenderKind.Person, SubjectId = "person_test" },
            Body = new BodyRef
            {
                ContentType = MessageLimits.BodyContentType, SizeBytes = Encoding.UTF8.GetByteCount(body), Sha256 = "",
                Data = Convert.ToBase64String(Encoding.UTF8.GetBytes(body)),
            },
            Attachments =
            [
                new AttachmentRef
                {
                    Id = "att_bundle", Name = "main.bundle", Format = AttachmentFormat.GitBundle,
                    Branch = "main", SizeBytes = 1, Sha256 = new string('0', 64),
                },
                new AttachmentRef
                {
                    Id = "att_patch", Name = "companion.patch", Format = AttachmentFormat.Patch,
                    Branch = "companion", BaseCommit = new string('a', 40), SizeBytes = 1, Sha256 = new string('0', 64),
                },
            ],
        };
        var status = new StatusResult
        {
            Threads =
            [
                new ThreadSummary
                {
                    ThreadId = root.ThreadId, RootMessageId = root.Id, WorkspaceId = root.WorkspaceId,
                    Type = root.Type, Target = root.Target, Sender = root.Sender, Status = ThreadStatus.Settled,
                    CreatedAt = root.Time, UpdatedAt = root.Time,
                },
            ],
        };
        using var http = ConfigureServer(machine, pair, status);
        machine.Server.On(HttpMethod.Get, "/api/w/ws_test/threads/thr_retry/messages",
            _ => Json("[" + MessageBusJson.Serialize(root) + "]"));
        await using var tools = OpenTools(machine, http);

        var call = await CallAsync(tools, McpToolNames.Retry, new RetryInput { ThreadId = root.ThreadId, Repository = machine.Home });

        Assert.True(call.IsError);
        var error = call.StructuredContent!.Value.Deserialize<McpError>(MessageBusJson.Options)!;
        Assert.Equal(McpErrorCodes.InvalidInput, error.Code);
        Assert.Equal("repository", error.Field);
        Assert.Contains("multiple code attachments", error.Message);
        Assert.Contains("fresh send", error.Message);
        Assert.Contains("path and branch", error.Message);
        Assert.Contains("expectedHead", error.Message);
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath.EndsWith("/upload-link", StringComparison.Ordinal));
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath == "/mcp"
            && (string?)JsonNode.Parse(r.BodyText)?["params"]?["name"] == McpToolNames.Send);
    }

    private static SendInput Message(params AttachSpec[] attachments) =>
        new() { To = Target, Type = "task.implement", Body = new MessageBody { Summary = "Check the bundle" }, Attach = attachments };

    private static Task<CallToolResult> CallAsync<T>(LocalTools tools, string name, T input)
    {
        using var doc = JsonDocument.Parse(MessageBusJson.Serialize(input));
        return tools.CallAsync("codex", name, doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()), Ct);
    }

    private static LocalTools OpenTools(TestMachine machine, HttpClient http, Action<ProcessStartInfo>? prepareGit = null) =>
        new(ToolOptions(machine, http) with { PrepareGit = prepareGit });

    private static LocalServerOptions ToolOptions(TestMachine machine, HttpClient http) => new()
    {
        Http = http, Links = machine.Server.Links, WorkspaceId = "ws_test", ConfigDirectory = machine.Home, Time = machine.Time,
        Input = Stream.Null, Output = Stream.Null, TempDirectory = machine.Home,
    };

    private static HttpClient ConfigureServer(TestMachine machine, KeyPair pair, StatusResult? status = null)
    {
        var key = new PublicKeyInfo
        {
            KeyId = "ek_workspace", Scope = KeyScope.Workspace, Version = 1, Algorithm = pair.PublicKey.Algorithm,
            PublicKey = pair.PublicKey.ToBase64(), Fingerprint = pair.PublicKey.Fingerprint, CreatedAt = machine.Time.GetUtcNow(),
        };
        var server = machine.Server;
        server.On(HttpMethod.Get, "/api/caller", _ => Json("""{"subjectId":"person_test","permissions":[]}"""));
        server.On(HttpMethod.Get, "/api/w/ws_test/keys", _ => FakeServer.Json<IReadOnlyList<PublicKeyInfo>>([key]));
        server.On(HttpMethod.Get, "/api/w/ws_test/keys/rotation",
            _ => FakeServer.Json<IReadOnlyList<KeyRotationState>>([new() { Scope = key.Scope, Key = key }]));
        for (var i = 1; i <= 6; i++)
            server.On(HttpMethod.Put, $"/blobs/att_{i}", _ => new HttpResponseMessage(HttpStatusCode.Created));
        var uploads = 0;
        server.On(HttpMethod.Post, "/api/w/ws_test/attachments/upload-link", _ =>
        {
            var id = $"att_{Interlocked.Increment(ref uploads)}";
            return FakeServer.Json(new UploadLink
            {
                AttachmentId = id, Url = FakeServer.Url + "/blobs/" + id, MaxBytes = long.MaxValue,
                ExpiresAt = machine.Time.GetUtcNow().AddHours(1),
            });
        });
        server.On(HttpMethod.Post, "/mcp", request => RespondToMcp(request, status));
        return new HttpClient(server, disposeHandler: false) { BaseAddress = new Uri(FakeServer.Url) };
    }

    private static HttpResponseMessage RespondToMcp(RecordedRequest request, StatusResult? status)
    {
        var rpc = JsonNode.Parse(request.BodyText)!;
        if (rpc["id"] is null) return new HttpResponseMessage(HttpStatusCode.Accepted);
        var method = (string?)rpc["method"];
        if (method is not ("initialize" or "tools/call"))
            return Json(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = rpc["id"]!.DeepClone(),
                ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" },
            }.ToJsonString());
        var args = rpc["params"]!;
        var result = method == "initialize"
            ? new JsonObject
            {
                ["protocolVersion"] = args["protocolVersion"]!.DeepClone(), ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "test", ["version"] = "1" },
            }
            : new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = (string?)args["name"] switch
                {
                    McpToolNames.Settle => """{"status":"working"}""",
                    McpToolNames.Send => """{"messageId":"msg_seed","threadId":"thr_seed","status":"queued"}""",
                    McpToolNames.Status when status is not null => MessageBusJson.Serialize(status),
                    McpToolNames.SendMany => MessageBusJson.Serialize(new SendManyResult
                    {
                        Results = Enumerable.Range(0, args["arguments"]!["messages"]!.AsArray().Count).Select(i => new SendResult
                        {
                            MessageId = $"msg_{i}", ThreadId = $"thr_{i}", Status = ThreadStatus.Queued,
                        }).ToArray(),
                    }),
                    McpToolNames.Watch => MessageBusJson.Serialize(new WatchResult
                    {
                        Events = [new ThreadStatusChanged { EventId = "evt_done", ThreadId = "thr_1", At = DateTimeOffset.UnixEpoch, Status = ThreadStatus.Settled }],
                    }),
                    _ => throw new InvalidOperationException("Unexpected MCP call: " + args["name"]),
                } }),
            };
        return Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = rpc["id"]!.DeepClone(), ["result"] = result }.ToJsonString());
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static async Task<string> InitAsync(TestMachine machine)
    {
        Assert.SkipWhen(CommandLocator.Find("git", Environment.GetEnvironmentVariable, []) is null, "git isn't installed.");
        var repo = Directory.CreateDirectory(Path.Combine(machine.Home, "sender")).FullName;
        await Git.RunAsync(repo, ["init", "--quiet", "--initial-branch=main"], Ct);
        return repo;
    }

    private static async Task<string> CommitAsync(string repo, string message)
    {
        await Git.RunAsync(repo, ["-c", "user.name=Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false",
            "commit", "--allow-empty", "--quiet", "-m", message], Ct);
        return await Git.CommitIdAsync(repo, "HEAD", Ct);
    }
}
