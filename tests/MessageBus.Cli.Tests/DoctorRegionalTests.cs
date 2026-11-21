using System.CommandLine;
using System.Net;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Server;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Http;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class DoctorRegionalTests
{
    private const string Origin = "https://eu.stamps.example.test";
    private const string Isolated = """{"product":"messagebus.ai","stamp":{"stampId":"eu","geography":"eu","azureRegion":"westeurope","url":"https://eu.stamps.example.test","aliases":[]},"signIn":{"authority":{"version":1,"stampId":"eu","issuer":"https://eu.stamps.example.test","audience":"https://eu.stamps.example.test"},"machineSignature":{"version":2,"required":true}}}""";
    private const string Legacy = """{"product":"messagebus.ai","signIn":{"microsoft":true},"stamp":{"stampId":"eu","geography":"eu","azureRegion":"westeurope","url":"https://eu.stamps.example.test","aliases":[]}}""";

    [Theory]
    [InlineData(true, Origin)]
    [InlineData(false, Origin)]
    [InlineData(false, "https://fixture-tunnel.example.test")]
    public async Task Actual_doctor_reads_stay_at_selected_base_with_fresh_discovery(bool isolated, string origin)
    {
        using var machine = Machine(origin);
        machine.Server.On(HttpMethod.Get, "/gateway/api/server", request =>
        {
            Assert.Null(request.Header("Authorization"));
            Assert.Null(request.Header(MbusHeaders.Workspace));
            return Json(isolated ? Isolated : Legacy);
        });
        var view = await DoctorServerView.AskAsync(Context(machine), TestContext.Current.CancellationToken);
        Assert.Null(view.Problem);
        Assert.NotNull(view.Machine);
        Assert.Equal(isolated, view.PreparedBinding is not null);
        Assert.Equal(isolated, File.Exists(Path.Combine(machine.MbusFolder, "server-bindings.json")));
        var authenticated = machine.Server.Requests.Where(r => r.Header("Authorization") is not null).ToArray();
        Assert.Equal(3, authenticated.Length);
        Assert.All(authenticated, r =>
        {
            Assert.Equal(origin, r.Uri.GetLeftPart(UriPartial.Authority));
            Assert.StartsWith("/gateway/api/", r.Uri.AbsolutePath);
            Assert.Equal("MbusKey fixture-key", r.Header("Authorization"));
            Assert.Equal("ws_fixture", r.Header(MbusHeaders.Workspace));
        });
        Assert.Equal(4, machine.Server.Requests.Count(r => r.Uri.AbsolutePath == "/gateway/api/server"));
    }

    [Theory]
    [InlineData("stripped")]
    [InlineData("changed")]
    [InlineData("revoked")]
    [InlineData("redirect")]
    [InlineData("missing-ledger")]
    public async Task Diagnostic_observation_never_authorizes_a_later_doctor_credential(string failure)
    {
        using var machine = Machine(Origin);
        var discovery = 0;
        machine.Server.On(HttpMethod.Get, "/gateway/api/server", _ =>
        {
            if (++discovery == 1) return Json(Isolated);
            if (failure == "missing-ledger")
            {
                var path = Path.Combine(machine.MbusFolder, "server-bindings.json");
                if (File.Exists(path)) File.Delete(path);
                return Json(Isolated);
            }
            return failure switch
            {
                "stripped" => Json(Legacy),
                "changed" => Json(Isolated.Replace("eu.stamps", "other.stamps", StringComparison.Ordinal)),
                "revoked" => new(HttpStatusCode.ServiceUnavailable),
                _ => new(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://foreign.example.test/stolen") } },
            };
        });
        var view = await DoctorServerView.AskAsync(Context(machine), TestContext.Current.CancellationToken);
        Assert.NotNull(view.Problem);
        Assert.NotNull(view.PreparedBinding);
        Assert.True(discovery > 1); // Refusal occurred at the subsequent actual read, after the diagnostic observation.
        Assert.DoesNotContain(machine.Server.Requests, r => r.Header("Authorization") is not null);
        Assert.All(machine.Server.Requests, r => Assert.Equal(Origin, r.Uri.GetLeftPart(UriPartial.Authority)));
    }

    [Fact]
    public async Task Explicit_alias_keeps_selected_transport_separate_from_canonical_authority()
    {
        const string alias = "https://alias.example.test";
        using var machine = Machine(alias);
        machine.Env[EnvVars.Home] = machine.MbusFolder;
        machine.Env[ServerBindingExpectation.EnvironmentVariable] = """{"version":1,"transportOrigin":"https://alias.example.test","stampId":"eu","issuer":"https://eu.stamps.example.test","audience":"https://eu.stamps.example.test","authorityVersion":1,"minimumMachineSignatureVersion":2,"machineSignatureRequired":true}""";
        machine.Server.On(HttpMethod.Get, "/gateway/api/server", _ => Json(Isolated.Replace("\"aliases\":[]", "\"aliases\":[\"https://alias.example.test\"]", StringComparison.Ordinal)));
        var view = await DoctorServerView.AskAsync(Context(machine), TestContext.Current.CancellationToken);
        Assert.Null(view.Problem);
        Assert.Equal(Origin, view.PreparedBinding?.Issuer);
        Assert.Equal(alias + "/gateway/", view.PreparedBinding?.SelectedEndpoint);
        Assert.All(machine.Server.Requests, r => Assert.Equal(alias, r.Uri.GetLeftPart(UriPartial.Authority)));
    }

    [Fact]
    public async Task Concurrent_failed_validation_revokes_all_prepared_doctor_reads_before_credentials()
    {
        using var machine = Machine(Origin);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observations = 0;
        machine.DiscoveryHttpHandler = () => new DiscoveryHandler(async request =>
        {
            Assert.Null(request.Headers.Authorization);
            Assert.False(request.Headers.Contains(MbusHeaders.Workspace));
            var number = Interlocked.Increment(ref observations);
            if (number == 1) return Json(Isolated); // Doctor's diagnostic observation.
            if (number > 4) return new(HttpStatusCode.ServiceUnavailable); // Concurrent validator.
            if (number == 4) entered.TrySetResult();
            await release.Task.WaitAsync(TestContext.Current.CancellationToken);
            return Json(Isolated);
        });
        var context = Context(machine);
        var asking = DoctorServerView.AskAsync(context, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            using var invalidator = new ServerSendGate(machine.CreateHost(), new ConfigStore(machine.MbusFolder, "file"), new Uri(Origin + "/gateway"));
            await Assert.ThrowsAsync<ServerBindingException>(() => invalidator.PrepareSendAsync(
                new Uri(Origin + "/gateway/api/check"), TestContext.Current.CancellationToken));
        }
        finally { release.TrySetResult(); }
        var view = await asking;
        Assert.NotNull(view.Problem);
        Assert.NotNull(view.PreparedBinding);
        Assert.DoesNotContain(machine.Server.Requests, r => r.Header("Authorization") is not null);
    }

    [Fact]
    public async Task Actual_doctor_command_returns_not_ready_on_server_binding_refusal()
    {
        using var machine = Machine(Origin);
        machine.InstallFake("git", new("git version 2.50.1"));
        machine.InstallFake("codex", new("codex-cli 0.169.0"), new Dictionary<string, FakeAnswer>
        { ["login"] = new("Logged in using ChatGPT", ToStderr: true) });
        machine.Server.On(HttpMethod.Get, "/gateway/api/server", _ => new(HttpStatusCode.ServiceUnavailable));
        var result = await machine.RunAsync("doctor");
        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain(machine.Server.Requests, r => r.Header("Authorization") is not null);
    }

    [Fact]
    public async Task Persisted_origin_pin_remains_diagnosable_during_an_outage_without_a_machine_profile()
    {
        using var machine = Machine(Origin);
        var store = new ConfigStore(machine.MbusFolder, "file");
        await store.WithProfileSecurityLockAsync(tx =>
        {
            tx.PrepareBinding(ServerEndpoint.Parse(Origin + "/gateway"), ServerDiscoveryParser.Parse(Isolated));
            var saved = tx.Load();
            tx.Save(saved with { MachineId = null, WorkspaceId = null, ServerBinding = null });
            return Task.FromResult(true);
        }, TestContext.Current.CancellationToken);
        machine.Server.On(HttpMethod.Get, "/gateway/api/server", _ => new(HttpStatusCode.ServiceUnavailable));
        var view = await DoctorServerView.AskAsync(Context(machine), TestContext.Current.CancellationToken);
        Assert.NotNull(view.Problem);
        Assert.Equal(Origin, view.PersistedBinding?.Issuer);
        Assert.Null(view.Discovery);
        Assert.DoesNotContain(machine.Server.Requests, r => r.Header("Authorization") is not null);
    }

    [Fact]
    public async Task Explicit_server_override_does_not_probe_with_saved_foreign_identity()
    {
        using var machine = Machine(Origin);
        machine.Env[EnvVars.Server] = "https://foreign.example.test/gateway";
        machine.Server.On(HttpMethod.Get, "/gateway/api/server", _ => Json(Legacy));
        var view = await DoctorServerView.AskAsync(Context(machine), TestContext.Current.CancellationToken);
        Assert.NotNull(view.Problem);
        Assert.DoesNotContain(machine.Server.Requests, r => r.Header("Authorization") is not null);
    }

    private static TestMachine Machine(string origin)
    {
        var machine = new TestMachine();
        machine.Env[EnvVars.ApiKey] = "fixture-key";
        machine.Env[EnvVars.Server] = origin + "/gateway";
        machine.Env[ConfigStore.KeyStoreVariable] = "file";
        new ConfigStore(machine.MbusFolder, "file").Save(new CliConfig
        { Server = origin + "/gateway", MachineId = "mch_fixture", WorkspaceId = "ws_fixture" });
        machine.Server.On(HttpMethod.Get, "/gateway/api/w/ws_fixture/machines/mch_fixture", _ => Json("""{"machineId":"mch_fixture","workspaceId":"ws_fixture","ownerSubjectId":"fixture","kind":"personal","name":"fixture","os":"linux","architecture":"x64","acceptsWorkFrom":{"mode":"own-projects"},"keyFingerprint":"fixture","enrolledAt":"2026-11-01T00:00:00Z"}"""));
        machine.Server.On(HttpMethod.Get, "/gateway/api/runner/policy", _ => Json("""{"ownerSubjectId":"fixture","acceptsWorkFrom":{"mode":"own-projects"}}"""));
        machine.Server.On(HttpMethod.Get, "/gateway/api/machines/me/agents", _ => Json("[]"));
        return machine;
    }

    private static CommandContext Context(TestMachine machine)
    {
        var options = new CommonOptions();
        var root = new RootCommand();
        root.Options.Add(options.Server);
        return CommandContext.Load(machine.CreateHost(), root.Parse([]), options);
    }
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class DiscoveryHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
