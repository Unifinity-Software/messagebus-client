using MessageBus.Cli.Agents;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Readiness;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Runner;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;
using MessageBus.Mcp;
using MessageBus.Tests.Shared;
using Xunit;

namespace MessageBus.Cli.Tests;

/// <summary>
/// The machine scan (<c>mbus connect --rescan</c>, which the runner and <c>mbus login</c> share) and
/// <c>mbus doctor</c>, with fake git, agent and toolchain commands that answer like the real ones
/// (the original issue, the original issue, the original issue). Nothing real is run: PATH is only the test's own folder.
/// </summary>
public sealed class DoctorTests
{
    [Fact]
    public async Task A_changed_workspace_key_blocks_the_runner_and_doctor_until_it_is_trusted()
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MachineKeyReason);
        using var machine = new TestMachine();
        machine.Env["MBUS_API_KEY"] = "mbk_test";
        machine.Env["MBUS_KEY_STORE"] = "file";
        var store = new ConfigStore(machine.MbusFolder, "file");
        store.Save(new CliConfig
        {
            Server = FakeServer.Url, WorkspaceId = "ws_test", MachineId = "mch_test", KeyStore = "file",
        });
        machine.InstallFake("git", new("git version 2.50.1"));
        machine.InstallFake("codex", new("codex-cli 0.169.0"), new Dictionary<string, FakeAnswer>
        {
            ["login"] = new("Logged in using ChatGPT", ToStderr: true),
        });
        using var pair = KeyPair.GenerateP256();
        var key = new PublicKeyInfo
        {
            KeyId = "ek_current", Scope = KeyScope.Workspace, Version = 2,
            Algorithm = "P-256", PublicKey = pair.PublicKey.ToBase64(), Fingerprint = pair.PublicKey.Fingerprint,
            CreatedAt = machine.Time.GetUtcNow(),
        };
        var pins = new TrustedWorkspace();
        KeyTrust.Accept(pins, key with { Version = 1, KeyId = "ek_old", Fingerprint = new string('a', 64) }, null);
        KeyTrust.Save(machine.MbusFolder, "ws_test", pins, FakeServer.Url);
        machine.Server.On(HttpMethod.Get, "/api/w/ws_test/keys", _ => FakeServer.Json<IReadOnlyList<PublicKeyInfo>>([key]));
        machine.Server.On(HttpMethod.Put, "/api/machines/inventory", _ => FakeServer.Json<IReadOnlyList<AgentInfo>>([]));

        var host = machine.CreateHost();
        var options = new CommonOptions();
        var root = new System.CommandLine.RootCommand();
        root.Options.Add(options.Server);
        var context = CommandContext.Load(host, root.Parse([]), options);
        var runnerHost = new CliRunnerHostContext(context);
        var inventory = await runnerHost.BuildInventoryAsync(1, TestContext.Current.CancellationToken);
        var codex = inventory.Agents.Single(a => a.Kind == AgentKind.Codex);
        Assert.Contains(MessageBus.Cli.Keys.KeysCommand.Grouped(key.Fingerprint), codex.NotReady);
        Assert.Contains("mbus keys trust workspace", codex.NotReady);
        Assert.DoesNotContain("agent:codex", inventory.Capabilities);
        var readiness = new RunnerReadiness(TextWriter.Null);
        readiness.Scanned(inventory, _ => null);
        await using var harness = new Runner.RunnerHarness(extensions: e => e with { Readiness = readiness });
        var declined = await harness.DeliverAsync(harness.Task("dlv_key", "codex"));
        Assert.Equal(DeclineReason.MachineNotReady, declined.Reason);

        var doctor = await machine.RunAsync("doctor");
        Assert.Equal(1, doctor.ExitCode);
        Assert.Contains(MessageBus.Cli.Keys.KeysCommand.Grouped(key.Fingerprint), doctor.Output);
        Assert.Contains("mbus keys trust workspace", doctor.Output);
        var rescan = await RescanAsync(machine);
        Assert.NotNull(rescan.Agents.Single(a => a.Kind == AgentKind.Codex).NotReady);
        Assert.DoesNotContain("agent:codex", rescan.Capabilities);
        // Merely checking must never accept the replacement key.
        Assert.Single(KeyTrust.Load(machine.MbusFolder, "ws_test", FakeServer.Url).Keys["-"]);
        KeyTrust.Accept(pins, key, null);
        KeyTrust.Save(machine.MbusFolder, "ws_test", pins, FakeServer.Url);
        var fixedInventory = await runnerHost.BuildInventoryAsync(1, TestContext.Current.CancellationToken);
        Assert.Null(fixedInventory.Agents.Single(a => a.Kind == AgentKind.Codex).NotReady);
        Assert.Contains("agent:codex", fixedInventory.Capabilities);
        var fixedDoctor = await machine.RunAsync("doctor");
        Assert.Equal(0, fixedDoctor.ExitCode);
        Assert.DoesNotContain("hasn't accepted", fixedDoctor.Output);
    }

    [Theory]
    [InlineData("new-admin", true)]
    [InlineData("trusted-admin", true)]
    [InlineData("changed-admin", false)]
    [InlineData("changed-version", false)]
    [InlineData("revoked", false)]
    [InlineData("unreadable", false)]
    public async Task Workspace_key_grants_keep_agents_ready_only_when_a_run_would_accept_them(string scenario, bool ready)
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MachineKeyReason);
        using var machine = new TestMachine();
        machine.Env["MBUS_API_KEY"] = "mbk_test";
        machine.Env["MBUS_KEY_STORE"] = "file";
        var store = new ConfigStore(machine.MbusFolder, "file");
        store.Save(new CliConfig { Server = FakeServer.Url, WorkspaceId = "ws_test", MachineId = "mch_test", KeyStore = "file" });
        machine.InstallFake("git", new("git version 2.50.1"));
        machine.InstallFake("codex", new("codex-cli 0.169.0"), new Dictionary<string, FakeAnswer>
        {
            ["login"] = new("Logged in using ChatGPT", ToStderr: true),
        });
        using var device = KeyPair.GenerateP256();
        using var admin = KeyPair.GenerateP256();
        using var replacement = KeyPair.GenerateP256();
        store.SaveKeyPem(device.ExportPem());
        PublicKeyInfo Info(string id, KeyPair pair, KeyScope scope, string? owner) => new()
        {
            KeyId = id, Scope = scope, Owner = owner, Version = 1, Algorithm = "P-256",
            PublicKey = pair.PublicKey.ToBase64(), Fingerprint = pair.PublicKey.Fingerprint, CreatedAt = machine.Time.GetUtcNow(),
        };
        var own = Info("ek_device", device, KeyScope.Machine, "mch_test");
        var signer = Info("ek_admin", admin, KeyScope.Machine, "mch_admin");
        var key = Info("ek_current", replacement, KeyScope.Workspace, null) with
        {
            Version = 2, SigningKeyId = signer.KeyId,
            Signature = KeySignatures.Sign("ws_test", KeyScope.Workspace, null, replacement.PublicKey, admin),
        };
        var pins = new TrustedWorkspace();
        KeyTrust.Accept(pins, key with { Version = scenario == "changed-version" ? 2 : 1, KeyId = "ek_old", Fingerprint = new string('a', 64) }, null);
        if (scenario is "trusted-admin" or "changed-admin")
            KeyTrust.AcceptSigner(pins, scenario == "changed-admin" ? signer with { Fingerprint = new string('b', 64) } : signer);
        KeyTrust.Save(machine.MbusFolder, "ws_test", pins, FakeServer.Url);
        var trustPath = Path.Combine(machine.MbusFolder, KeyTrust.FileName);
        var remembered = File.ReadAllText(trustPath);
        var grant = GrantCrypto.Create("ws_test", key, replacement, own, signer, admin, "admin", machine.Time.GetUtcNow());
        if (scenario == "revoked") grant = grant with { RevokedAt = machine.Time.GetUtcNow() };
        if (scenario == "unreadable") grant = grant with { SealedPrivateKey = "broken" };
        machine.Server.On(HttpMethod.Get, "/api/w/ws_test/keys", _ => FakeServer.Json<IReadOnlyList<PublicKeyInfo>>([own, signer, key]));
        machine.Server.On(HttpMethod.Get, "/api/w/ws_test/keys/grants", request =>
        {
            Assert.Equal("?machineId=mch_test", request.Uri.Query);
            return FakeServer.Json<IReadOnlyList<KeyGrant>>([grant]);
        });
        var options = new CommonOptions();
        var root = new System.CommandLine.RootCommand();
        root.Options.Add(options.Server);
        var context = CommandContext.Load(machine.CreateHost(), root.Parse([]), options);

        var inventory = await new CliRunnerHostContext(context).BuildInventoryAsync(1, TestContext.Current.CancellationToken);

        Assert.Equal(ready, inventory.Agents.Single(a => a.Kind == AgentKind.Codex).NotReady is null);
        Assert.Equal(ready, inventory.Capabilities.Contains("agent:codex"));
        Assert.Equal(remembered, File.ReadAllText(trustPath));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("json")]
    public async Task An_incomplete_key_check_keeps_agents_ready_and_doctor_only_warns(string problem)
    {
        using var machine = new TestMachine();
        machine.Env["MBUS_API_KEY"] = "mbk_test";
        machine.Env["MBUS_KEY_STORE"] = "file";
        var store = new ConfigStore(machine.MbusFolder, "file");
        store.Save(new CliConfig { Server = FakeServer.Url, WorkspaceId = "ws_test", MachineId = "mch_test", KeyStore = "file" });
        machine.InstallFake("git", new("git version 2.50.1"));
        machine.InstallFake("codex", new("codex-cli 0.169.0"), new Dictionary<string, FakeAnswer>
        {
            ["login"] = new("Logged in using ChatGPT", ToStderr: true),
        });
        machine.Server.On(HttpMethod.Get, "/api/w/ws_test/keys", _ =>
        {
            return problem switch
            {
                "http" => throw new HttpRequestException("private exception detail"),
                _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("bad json") },
            };
        });
        machine.Server.On(HttpMethod.Put, "/api/machines/inventory", _ => FakeServer.Json<IReadOnlyList<AgentInfo>>([]));
        var diagnostics = new StringWriter();
        var host = machine.CreateHost(error: diagnostics);
        var options = new CommonOptions();
        var root = new System.CommandLine.RootCommand();
        root.Options.Add(options.Server);
        var context = CommandContext.Load(host, root.Parse([]), options);
        var inventory = await new CliRunnerHostContext(context).BuildInventoryAsync(1, TestContext.Current.CancellationToken);
        Assert.Null(inventory.Agents.Single(a => a.Kind == AgentKind.Codex).NotReady);
        Assert.Contains("agent:codex", inventory.Capabilities);
        Assert.Contains("couldn't check", diagnostics.ToString());
        Assert.DoesNotContain("private exception detail", diagnostics.ToString());

        var doctor = await machine.RunAsync("doctor");
        Assert.Equal(0, doctor.ExitCode);
        // The status word may be coloured, so match the line by its text.
        Assert.Matches(@"note\S*\s+Workspace key: ", doctor.Output);
        Assert.Contains("couldn't check", doctor.Output);
        Assert.DoesNotContain("Run mbus doctor", doctor.Output);
        Assert.DoesNotContain("couldn't check", doctor.Error);
        Assert.DoesNotContain("not ready  Workspace key:", doctor.Output);
        var rescan = await RescanAsync(machine);
        Assert.Contains("agent:codex", rescan.Capabilities);
    }

    [Fact]
    public async Task A_cancelled_key_check_propagates_cancellation()
    {
        using var machine = new TestMachine();
        var host = machine.CreateHost();
        var options = new CommonOptions();
        var root = new System.CommandLine.RootCommand();
        root.Options.Add(options.Server);
        var context = CommandContext.Load(host, root.Parse([]), options);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WorkspaceKeyReadiness.CheckAsync(context, cancelled.Token));
    }

    public static bool IsWindows => OperatingSystem.IsWindows();

    [Theory(SkipUnless = nameof(IsWindows), Skip = "Codex sandbox advice is Windows-specific.")]
    [InlineData(null, "a role's Codex on Windows setting")]
    [InlineData("always", "sandbox is required by this machine")]
    [InlineData("unelevated", "restricted-token sandbox is required")]
    public async Task Doctor_explains_the_machine_codex_sandbox_policy(string? mode, string expected)
    {
        using var machine = new TestMachine();
        machine.InstallFake("git", new("git version 2.50.1"));
        machine.InstallFake("codex", new("codex-cli 0.169.0"), new Dictionary<string, FakeAnswer> { ["login"] = new("Logged in using ChatGPT", ToStderr: true) });
        if (mode is not null) machine.Env["MBUS_RUNNER_CODEX_SANDBOX"] = mode;
        var doctor = await machine.RunAsync("doctor");
        Assert.Contains(expected, doctor.Output);
        Assert.Contains(".NET tests cannot run on Windows", doctor.Output);
    }

    private static readonly FakeAnswer ClaudeVersion = new("2.1.3 (Claude Code)");

    [Fact]
    public async Task Git_that_is_only_the_macOS_placeholder_is_not_reported_and_no_agent_takes_work_until_it_is_fixed()
    {
        using var machine = new TestMachine();
        // /usr/bin/git before Apple's developer tools are installed.
        machine.InstallFake("git", new("xcode-select: note: No developer tools were found, requesting install.", 1, ToStderr: true));
        machine.InstallFake("claude", ClaudeVersion, new Dictionary<string, FakeAnswer> { ["auth"] = new("""{ "loggedIn": true, "authMethod": "claude.ai" }""") });

        var inventory = await RescanAsync(machine);
        var claude = inventory.Agents.Single(a => a.Kind == AgentKind.ClaudeCode);

        Assert.DoesNotContain("tool:git", inventory.Capabilities);
        Assert.DoesNotContain("agent:claude-code", inventory.Capabilities);
        Assert.True(claude.SignedIn);
        Assert.Contains("xcode-select --install", claude.NotReady);

        var doctor = await machine.RunAsync("doctor");
        Assert.Equal(1, doctor.ExitCode);
        Assert.Contains("xcode-select --install", doctor.Output);
    }

    [Fact]
    public async Task Sign_in_comes_from_each_agents_status_command_and_toolchains_count_only_when_they_run()
    {
        using var machine = new TestMachine();
        machine.InstallFake("git", new("git version 2.50.1"));
        // Claude Code's settings say it signed in once (what the old check read), but from this session it can't use it.
        machine.WriteHomeFile(".claude.json", """{ "oauthAccount": { "emailAddress": "a@contoso.com" } }""");
        machine.InstallFake("claude", ClaudeVersion, new Dictionary<string, FakeAnswer> { ["auth"] = new("""{ "loggedIn": false, "authMethod": "none" }""", 1) });
        machine.InstallFake("codex", new("codex-cli 0.50.0"), new Dictionary<string, FakeAnswer> { ["login"] = new("Logged in using ChatGPT", ToStderr: true) });
        // A per-user .NET install that isn't on PATH, and node/npm that are.
        machine.InstallFake("dotnet", new("9.0.100 [x]\n10.0.402 [x]"), folder: Path.Combine(machine.Home, ".dotnet"));
        machine.InstallFake("node", new("v24.1.0"));
        machine.InstallFake("npm", new("11.0.0"));
        // A placeholder that only says Python isn't installed (Windows' Store alias does this).
        machine.InstallFake(OperatingSystem.IsWindows() ? "python" : "python3", new("Python was not found; run without arguments to install from the Microsoft Store.", 9009, ToStderr: true));

        var inventory = await RescanAsync(machine);
        var claude = inventory.Agents.Single(a => a.Kind == AgentKind.ClaudeCode);
        var codex = inventory.Agents.Single(a => a.Kind == AgentKind.Codex);

        Assert.False(claude.SignedIn);
        Assert.Contains("claude setup-token", claude.NotReady);
        Assert.True(codex.SignedIn);
        Assert.Null(codex.NotReady);
        Assert.Superset(new HashSet<string> { "tool:git", "tool:dotnet", "tool:dotnet-sdk-9.0", "tool:dotnet-sdk-10.0", "tool:node", "tool:npm", "agent:codex" },
            inventory.Capabilities.ToHashSet());
        Assert.DoesNotContain("agent:claude-code", inventory.Capabilities);
        Assert.DoesNotContain("tool:python", inventory.Capabilities);

        var doctor = await machine.RunAsync("doctor");
        Assert.Equal(1, doctor.ExitCode); // Claude Code can't sign in, and this machine isn't signed in to messagebus.ai
        Assert.Contains("claude setup-token", doctor.Output);
        Assert.Contains("mbus login", doctor.Output);
        Assert.Contains("after 10 idle minute(s)", doctor.Output);
        Assert.Contains("mbus runner --keep-awake always", doctor.Output);
    }

    [Fact]
    public async Task A_Codex_environment_key_is_configuration_and_the_doctor_names_the_model_check()
    {
        using var machine = new TestMachine();
        machine.Env["OPENAI_API_KEY"] = "fixture-configured";
        machine.InstallFake("git", new("git version 2.50.1"));
        machine.InstallFake("codex", new("codex-cli 0.169.0"), new Dictionary<string, FakeAnswer> { ["login"] = new("Not logged in", 1) });

        var inventory = await RescanAsync(machine);
        Assert.Contains("agent:codex", inventory.Capabilities);
        var doctor = await machine.RunAsync("doctor");
        Assert.Contains("credentials configured. Model access is unverified.", doctor.Output);
        Assert.Contains("doctor --smoke --agent codex", doctor.Output);
        Assert.DoesNotContain("fixture-configured", doctor.Output);
    }

    [Fact]
    public async Task Doctor_says_when_another_mbus_is_first_on_PATH_and_names_this_one_in_its_advice()
    {
        // the original issue: an old dotnet tool install on PATH, and the installer's mbus (not on PATH) running.
        using var machine = new TestMachine();
        machine.InstallFake("git", new("git version 2.50.1"));
        machine.InstallFake("mbus", new("0.1.1-preview.1"));
        Directory.CreateDirectory(Path.Combine(machine.Bin, ".store", "messagebus.ai.cli", "0.1.1-preview.1"));
        var running = Path.Combine(machine.Home, "Programs (test)", "messagebus", "mbus");
        machine.MbusCommand = new(running, ["mcp"]);

        var doctor = await machine.RunAsync("doctor");

        var onPath = Path.Combine(machine.Bin, OperatingSystem.IsWindows() ? "mbus.cmd" : "mbus");
        Assert.Contains($"The mbus on PATH is another one ({onPath}, 0.1.1-preview.1)", doctor.Output);
        // Not the global tools folder (a --tool-path install), so the fix names its folder.
        Assert.Contains($"dotnet tool uninstall --tool-path {machine.Bin} MessageBus.Ai.Cli", doctor.Output);
        var command = OperatingSystem.IsWindows() ? $"& \"{running}\"" : $"'{running}'";
        Assert.Contains($"Fix: {command} login", doctor.Output); // not signed in: the fix starts this mbus
    }

    [Fact]
    public void Claude_Code_without_a_usable_sign_in_is_explained_for_a_runner_service_differently_from_an_SSH_session()
    {
        // the original issue: a launchd service isn't an SSH session; its fix is a token stored by mbus runner install.
        var ssh = new SignInEnvironment(Env(("SSH_CONNECTION", "10.0.0.2 52000 10.0.0.5 22")), new Dictionary<string, string>());
        var service = new SignInEnvironment(Env((MessageBus.Cli.Runner.Service.RunnerServiceDefinitions.ServiceVariable, "1")), new Dictionary<string, string>());
        var serviceWithToken = service with { Stored = new Dictionary<string, string> { [SignInChecks.ClaudeTokenVariable] = "sk-ant-oat01-expired" } };

        Assert.Contains("SSH session", SignInChecks.ClaudeNotSignedIn(ssh, macOs: true));
        var noToken = SignInChecks.ClaudeNotSignedIn(service, macOs: true);
        Assert.DoesNotContain("SSH", noToken);
        Assert.Contains("mbus runner install", noToken);
        Assert.NotEqual(noToken, SignInChecks.ClaudeNotSignedIn(serviceWithToken, macOs: true)); // a stored token that no longer works
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] variables) =>
        name => variables.FirstOrDefault(v => v.Name == name).Value;

    private static async Task<MachineInventory> RescanAsync(TestMachine machine)
    {
        var run = await machine.RunAsync("connect", "--rescan", "--json");
        Assert.Equal(0, run.ExitCode);
        return MessageBusJson.Deserialize<MachineInventory>(run.Output)!;
    }
}
