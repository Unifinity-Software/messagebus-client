using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MessageBus.Attachments.Client;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;
using MessageBus.Mcp;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class StepInputsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Encrypted_design_and_code_arrive_before_the_agent_starts()
    {
        using var inputs = new InputServer();
        var sandbox = new RecordingSandbox();
        await using var runner = new RunnerHarness(bundles: inputs.Source, sandbox: _ => sandbox);
        var repository = Directory.CreateDirectory(Path.Combine(runner.Scratch, "sender")).FullName;
        Assert.SkipWhen(CommandLocator.Find("git", Environment.GetEnvironmentVariable, []) is null, "git is not installed.");
        var unavailable = await WindowsRunCapabilities.GitShellUnavailableAsync();
        Assert.SkipWhen(unavailable is not null, unavailable ?? "");
        await Git.RunAsync(repository, ["init", "--quiet", "--initial-branch=feature/example"], Ct);
        await File.WriteAllTextAsync(Path.Combine(repository, "README.md"), "The earlier step's code", Ct);
        await Git.RunAsync(repository, ["add", "README.md"], Ct);
        await Git.RunAsync(repository, ["-c", "user.name=Test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Example code"], Ct);
        var bundle = await GitBundles.CreateAsync(repository, Path.Combine(runner.Scratch, "code.bundle"), ct: Ct);
        var code = await inputs.AttachmentAsync("att_code", "code.bundle", await File.ReadAllBytesAsync(bundle.Path, Ct), AttachmentFormat.GitBundle, bundle.Branch);
        var document = await inputs.AttachmentAsync("att_design", "design.md", "Design notes"u8.ToArray());
        var exhibits = new[] { new Ref { Kind = RefKinds.Exhibit, Exhibit = new ExhibitRef { Name = "Design", MediaType = "text/markdown", Size = 12, Caption = "Approved design" } } };
        var delivery = runner.Task("dlv_inputs") with { StepContext = Context(new("design", "result"), new("code", "branch")) };
        inputs.Reply(delivery, "design", "Design summary", "Design details", [document], exhibits, stored: true);
        inputs.Reply(delivery, "code", "Code summary", "Code details", [code]);

        Assert.True((await runner.DeliverAsync(delivery)).Accepted);
        var settle = await runner.Service.SettledAsync(delivery.DeliveryId);
        await runner.Engine.StopAsync();

        Assert.Equal(SettleStatus.Succeeded, settle.Result.Status);
        Assert.True(sandbox.ProcessStarted);
        Assert.Equal("feature/example", sandbox.InputBranches!["code"]);
        var work = runner.WorkTreeOf(delivery.DeliveryId);
        Assert.Equal("The earlier step's code", await File.ReadAllTextAsync(Path.Combine(work, "README.md"), Ct));
        Assert.Equal("Design summary", await ReadAsync("design", "summary.txt"));
        Assert.Equal("Design summary\n\nDesign details", await ReadAsync("design", "result.md"));
        Assert.Equal("Design notes", await ReadAsync("design", "design.md"));
        Assert.Equal("Code summary", await ReadAsync("code", "summary.txt"));
        Assert.Equal("feature/example", await ReadAsync("code", "branch.txt"));
        Assert.Equal("feature/example", (await Git.RunAsync(work, ["branch", "--show-current"], Ct)).Trim());
        using var metadata = JsonDocument.Parse(await ReadAsync("design", "exhibits.json"));
        Assert.Equal("Approved design", metadata.RootElement[0].GetProperty("caption").GetString());
        Assert.False(File.Exists(Path.Combine(work, ".mbus", "inputs", "code", "exhibits.json")));
        Assert.Equal("", (await Git.RunAsync(work, ["ls-files", "--others", "--exclude-standard", ".mbus"], Ct)).Trim());
        var prompt = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(work)!, "prompt.md"), Ct);
        Assert.Contains(".mbus/inputs/design/", prompt);
        Assert.Contains(".mbus/inputs/code/", prompt);

        Task<string> ReadAsync(string step, string file) => File.ReadAllTextAsync(Path.Combine(work, ".mbus", "inputs", step, file), Ct);
    }

    [Theory]
    [InlineData("body-key")]
    [InlineData("attachment-key")]
    [InlineData("missing-result")]
    [InlineData("corrupt-body")]
    [InlineData("unsafe-document")]
    [InlineData("unsupported-service")]
    public async Task An_unreadable_input_settles_failed_without_starting_the_fake_agent(string fault)
    {
        using var inputs = new InputServer();
        var sandbox = new RecordingSandbox();
        await using var runner = new RunnerHarness(bundles: inputs.Source, sandbox: _ => sandbox);
        var delivery = runner.Task("dlv_unreadable") with { StepContext = Context(new Input("design", "result")) };
        var document = await inputs.AttachmentAsync("att_design", fault == "unsafe-document" ? "../outside.md" : "design.md", "Design"u8.ToArray(), missingKey: fault == "attachment-key");
        if (fault != "missing-result")
            inputs.Reply(delivery, "design", "Design summary", "Details", [document], missingKey: fault == "body-key", corrupt: fault == "corrupt-body");
        else inputs.Server.On(HttpMethod.Get, "/api/w/ws_test/threads/thr_design/messages", _ => new(HttpStatusCode.NotFound));
        if (fault == "unsupported-service") inputs.Server.On(HttpMethod.Get, "/api/server", _ => FakeServer.Json(FakeServer.ConfiguredServerInfo));

        Assert.True((await runner.DeliverAsync(delivery)).Accepted);
        var settle = await runner.Service.SettledAsync(delivery.DeliveryId);
        await runner.Engine.StopAsync();

        Assert.Equal(SettleStatus.Failed, settle.Result.Status);
        Assert.Equal("unreadable", settle.Result.FailureCategory);
        Assert.Contains("design", settle.Result.Error);
        Assert.Null(settle.Result.ExitCode);
        Assert.False(sandbox.ProcessStarted);
        Assert.False(File.Exists(Path.Combine(runner.WorkTreeOf(delivery.DeliveryId), "fake-agent.txt")));
    }

    [Fact]
    public async Task Work_item_and_unknown_inputs_do_not_fetch_threads()
    {
        using var inputs = new InputServer();
        using var log = new StringWriter();
        await using var runner = new RunnerHarness(bundles: inputs.Source, log: log);
        var delivery = runner.Task("dlv_ignored") with { StepContext = Context(new("start", "work-item"), new("future", "future-kind")) };

        Assert.True((await runner.DeliverAsync(delivery)).Accepted);
        Assert.Equal(SettleStatus.Succeeded, (await runner.Service.SettledAsync(delivery.DeliveryId)).Result.Status);
        await runner.Engine.StopAsync();

        Assert.DoesNotContain(inputs.Server.Requests, r => r.Uri.AbsolutePath.Contains("/threads/"));
        Assert.Contains("Ignoring step input 'future-kind'", log.ToString());
    }

    private sealed record Input(string Step, string What);

    private static StepContext Context(params Input[] inputs) => new()
    {
        JobId = "job_example", StepId = "review", StepName = "Review", Round = 1, AllowedOutcomes = ["approved"],
        Inputs = [.. inputs.Select(i => new StepInput { FromStep = i.Step, ThreadId = "thr_" + i.Step, What = i.What })],
    };

    private sealed class RecordingSandbox : IRunSandbox
    {
        public bool ProcessStarted { get; private set; }
        public IReadOnlyDictionary<string, string>? InputBranches { get; private set; }
        public ProcessLaunch Wrap(ProcessLaunch launch, RunWorkspace workspace, PermissionProfile profile) => launch;
        public void Started(RunWorkspace workspace, int processId)
        {
            ProcessStarted = true;
            InputBranches = workspace.PreparedInputs?.InputBranches;
            Assert.True(workspace.PreparedInputs?.Files.Count > 0);
        }
    }

    private sealed class InputServer : IDisposable
    {
        private readonly KeyPair _key = KeyPair.GenerateP256();
        private readonly KeyPair _missingKey = KeyPair.GenerateP256();
        private readonly HttpClient _api;
        private readonly MachineBundles _bundles;
        public FakeServer Server { get; } = new();
        public IBundleSource Source { get; }

        public InputServer()
        {
            Server.On(HttpMethod.Get, "/api/server", _ => FakeServer.Json(FakeServer.ConfiguredServerInfo with { Capabilities = [WorkflowCapabilities.Workflows] }));
            Server.On(HttpMethod.Get, "/api/caller", _ => new(HttpStatusCode.OK) { Content = new StringContent("{\"subjectId\":\"fixture\",\"permissions\":[]}") });
            Server.On(HttpMethod.Get, "/api/w/ws_test/keys", _ => FakeServer.Json<IReadOnlyList<PublicKeyInfo>>([Key()]));
            Server.On(HttpMethod.Get, "/api/w/ws_test/keys/grants", _ => FakeServer.Json<IReadOnlyList<KeyGrant>>([]));
            _api = new HttpClient(Server, disposeHandler: false) { BaseAddress = new Uri(FakeServer.Url) };
            // No grants are written in this fixture; the existing machine-key loader holds the recipient key.
            _bundles = new MachineBundles(_api, Server.Links, "ws_test", "mch_test", Path.GetTempPath(), TextWriter.Null, machineKeyPem: _key.ExportPem);
            Source = new MachineBundleSource(_bundles);
        }

        private PublicKeyInfo Key(bool missing = false)
        {
            var pair = missing ? _missingKey : _key;
            return new PublicKeyInfo
            {
                KeyId = missing ? "ek_missing" : "ek_machine", Scope = KeyScope.Machine, Owner = "mch_test", Version = 1,
                Algorithm = pair.PublicKey.Algorithm, PublicKey = pair.PublicKey.ToBase64(), Fingerprint = pair.PublicKey.Fingerprint, CreatedAt = DateTimeOffset.UnixEpoch,
            };
        }

        public void Reply(TaskDelivery delivery, string step, string summary, string details, IReadOnlyList<AttachmentRef> attachments,
            IReadOnlyList<Ref>? refs = null, bool stored = false, bool missingKey = false, bool corrupt = false)
        {
            var sealedBody = BodyCrypto.Seal(new MessageBody { Summary = summary, Details = details }, Key(missingKey)).ToSealedBody();
            var reply = delivery.Envelope with
            {
                Id = "msg_" + step, ThreadId = "thr_" + step, Type = "task.implement.reply",
                Body = sealedBody.Body with { Data = stored ? null : sealedBody.Body.Data, Sha256 = corrupt ? new string('0', 64) : sealedBody.Body.Sha256 },
                Encryption = sealedBody.Encryption, Attachments = attachments, Refs = refs ?? [],
            };
            Server.On(HttpMethod.Get, $"/api/w/ws_test/threads/thr_{step}/messages", _ => new(HttpStatusCode.OK)
            {
                Content = new StringContent("[" + MessageBusJson.Serialize(delivery.Envelope with { ThreadId = "thr_" + step }) + "," + MessageBusJson.Serialize(reply) + "]", Encoding.UTF8, "application/json"),
            });
            if (stored) Server.On(HttpMethod.Get, $"/api/w/ws_test/threads/thr_{step}/messages/msg_{step}/body", _ => new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Convert.FromBase64String(sealedBody.Body.Data!)),
            });
        }

        public async Task<AttachmentRef> AttachmentAsync(string id, string name, byte[] bytes, AttachmentFormat format = AttachmentFormat.File, string? branch = null, bool missingKey = false)
        {
            using var plain = new MemoryStream(bytes);
            using var encrypted = new MemoryStream();
            var sealedFile = await MessageCrypto.SealAsync(plain, encrypted, Key(missingKey), Ct);
            var ciphertext = encrypted.ToArray();
            var attachment = new AttachmentRef
            {
                Id = id, Name = name, Format = format, Branch = branch, SizeBytes = ciphertext.LongLength,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(ciphertext)), Encryption = sealedFile.Encryption,
            };
            Server.On(HttpMethod.Post, $"/api/w/ws_test/attachments/{id}/download-link", _ => FakeServer.Json(new DownloadLink
            {
                Id = id, Url = FakeServer.Url + "/blobs/" + id, ExpiresAt = DateTimeOffset.MaxValue,
            }));
            Server.On(HttpMethod.Get, "/blobs/" + id, _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(ciphertext) });
            return attachment;
        }

        public void Dispose()
        {
            _bundles.Dispose();
            _api.Dispose();
            Server.Dispose();
            _key.Dispose();
            _missingKey.Dispose();
        }
    }
}
