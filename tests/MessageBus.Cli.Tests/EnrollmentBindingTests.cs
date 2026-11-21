using System.Net;
using System.Net.Sockets;
using System.Text;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Security;
using MessageBus.Cli.Server;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Security;
using MessageBus.Mcp;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class EnrollmentBindingTests
{
    private const string Ca = "https://ca.stamps.example.test";
    private const string Eu = "https://eu.stamps.example.test";
    private const string NewMachineId = "mch_00000000000000000000000002";
    private const string ApiKey = "fixture-identical-bootstrap-key";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("before-enrollment-key-save")]
    [InlineData("after-enrollment-key-save")]
    [InlineData("before-config-save")]
    [InlineData("before-enrollment-config-publish")]
    [InlineData("after-enrollment-config-publish")]
    [InlineData("cancel-after-key-save")]
    public async Task Failed_publication_preserves_old_config_key_and_trust(string boundary)
    {
        using var machine = new TestMachine();
        var store = new ConfigStore(machine.MbusFolder, "file");
        using var oldKey = MachineKey.Create();
        using var newKey = MachineKey.Create();
        store.Save(new CliConfig { Server = Ca, MachineId = "mch_old", WorkspaceId = "w_same", KeyFingerprint = oldKey.Fingerprint });
        store.SaveKeyPem(oldKey.ExportPrivateKeyPem());
        KeyTrust.Save(store.Directory, "w_same", new TrustedWorkspace(), Ca);
        var configBytes = File.ReadAllBytes(store.ConfigPath);
        var trustPath = Path.Combine(store.Directory, KeyTrust.FileName);
        var trustBytes = File.ReadAllBytes(trustPath);
        var keyBytes = File.ReadAllBytes(store.KeyPath);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        store.BindingWriteBoundary = reached =>
        {
            if (boundary == "cancel-after-key-save" && reached == "after-enrollment-key-save") cancel.Cancel();
            else if (reached == boundary) throw new IOException("Owned fixture publication failure");
        };
        await Assert.ThrowsAnyAsync<Exception>(() => store.WithProfileSecurityLockAsync(tx =>
        {
            var current = tx.Load();
            tx.CommitEnrollment(current, current with { Server = Eu, MachineId = NewMachineId, KeyFingerprint = newKey.Fingerprint },
                newKey.ExportPrivateKeyPem(), cancel.Token);
            return Task.FromResult(true);
        }, Ct));
        store.BindingWriteBoundary = null;
        Assert.Equal(configBytes, File.ReadAllBytes(store.ConfigPath));
        Assert.Equal(keyBytes, File.ReadAllBytes(store.KeyPath));
        Assert.Equal(trustBytes, File.ReadAllBytes(trustPath));
        var restarted = new ConfigStore(store.Directory, "file");
        Assert.Equal("mch_old", restarted.Load().MachineId);
        using var restored = MachineKey.FromPem(restarted.LoadKeyPem()!);
        Assert.Equal(oldKey.Fingerprint, restored.Fingerprint);
        Assert.DoesNotContain(Directory.EnumerateFiles(store.Directory), path => Path.GetFileName(path).StartsWith("machine-key.", StringComparison.Ordinal)
            && path.EndsWith(".pem", StringComparison.Ordinal) && path != store.KeyPath);
    }

    [Fact]
    public async Task Unsupported_vault_refuses_without_writing_or_deleting_its_existing_key()
    {
        using var machine = new TestMachine();
        using var key = MachineKey.Create();
        var vault = new FakeVault(key.ExportPrivateKeyPem());
        var store = new ConfigStore(machine.MbusFolder, osVault: vault);
        store.Save(new CliConfig { Server = Ca, MachineId = "mch_old", KeyFingerprint = key.Fingerprint });
        await Assert.ThrowsAsync<ServerBindingException>(() => store.WithProfileSecurityLockAsync(tx =>
        { tx.DemandStagedEnrollment(); return Task.FromResult(true); }, Ct));
        Assert.Equal(key.ExportPrivateKeyPem(), vault.Load());
        Assert.Equal("mch_old", store.Load().MachineId);
    }

    [Theory]
    [InlineData(Eu + "/gateway", Eu, "eu", false)]
    [InlineData("https://alias.stamps.example.test/gateway", Ca, "ca", true)]
    [InlineData(Ca + "/different-base", Ca, "ca", false)]
    public async Task Explicit_token_login_uses_fresh_scope_and_publishes_actual_binding(string selected, string canonical, string stamp, bool alias)
    {
        using var machine = new TestMachine();
        machine.Env[ConfigStore.KeyStoreVariable] = "file";
        var store = new ConfigStore(machine.MbusFolder, "file");
        using var oldKey = MachineKey.Create();
        store.Save(new CliConfig { Server = Ca, MachineId = "mch_old", WorkspaceId = "w_same", KeyFingerprint = oldKey.Fingerprint,
            EnrolledWithApiKey = RunnerKeyEnrollment.Fingerprint(ApiKey) });
        store.SaveKeyPem(oldKey.ExportPrivateKeyPem());
        await PinAsync(store, Ca, Isolated(Ca, "ca"));
        var oldKeyPath = store.KeyPath;
        var oldKeyBytes = File.ReadAllBytes(oldKeyPath);
        var prefix = new Uri(selected).AbsolutePath.TrimEnd('/');
        var origin = new Uri(selected).GetLeftPart(UriPartial.Authority);
        if (alias)
        {
            machine.Env["MBUS_HOME"] = store.Directory;
            machine.Env[ServerBindingExpectation.EnvironmentVariable] = Expected(origin, canonical, stamp);
        }
        machine.Server.On(HttpMethod.Get, prefix + "/api/server", _ => Text(Isolated(canonical, stamp, alias ? origin : null)));
        machine.Server.On(HttpMethod.Post, prefix + ApiRoutes.MachineEnroll, request =>
        {
            Assert.Equal("MbusEnroll fixture-enroll", request.Header("Authorization"));
            Assert.Null(request.Header(MbusHeaders.Workspace));
            var body = MessageBusJson.Deserialize<MachineEnrollRequest>(request.BodyText)!;
            Assert.Equal("", body.Inventory.MachineId);
            Assert.Null(body.WorkspaceId);
            Assert.NotEqual(oldKey.PublicKeyBase64, body.PublicKey);
            return FakeServer.Json(EnrollResponse(body));
        });
        machine.Server.On(HttpMethod.Put, prefix + ApiRoutes.MachineInventory, request =>
        {
            Assert.Equal("MbusMachine " + NewMachineId, request.Header("Authorization"));
            Assert.Equal("2", request.Header(MbusHeaders.SignatureVersion));
            return new(HttpStatusCode.NoContent);
        });
        var result = await machine.RunAsync("login", "--server", selected, "--token", "fixture-enroll");
        Assert.True(result.ExitCode == 0, result.Error);
        var saved = new ConfigStore(store.Directory, "file").Load();
        Assert.Equal(ServerEndpoint.Parse(selected).Value, saved.ServerBinding!.SelectedEndpoint);
        Assert.Equal(canonical, saved.ServerBinding.Issuer);
        Assert.Equal("w_same", saved.WorkspaceId); // equal workspace IDs grant no source identity inheritance
        Assert.Equal(oldKeyBytes, File.ReadAllBytes(oldKeyPath));
        Assert.Null(saved.EnrolledWithApiKey);
        Assert.All(machine.Server.Requests, request => Assert.Equal(origin, request.Uri.GetLeftPart(UriPartial.Authority)));
        Assert.DoesNotContain(machine.Server.Requests, request => request.Header("Authorization") == "MbusMachine mch_old");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task First_isolated_discovery_refuses_later_stripping_before_token_or_caller_send(bool runner, bool provisioned)
    {
        using var machine = new TestMachine();
        machine.Env[ConfigStore.KeyStoreVariable] = "file";
        if (provisioned)
        {
            machine.Env["MBUS_HOME"] = machine.MbusFolder;
            machine.Env[ServerBindingExpectation.EnvironmentVariable] = Expected(Eu, Eu, "eu");
        }
        if (runner) machine.Env[EnvVars.ApiKey] = ApiKey;
        var observation = 0;
        machine.Server.On(HttpMethod.Get, "/api/server", _ => Text(++observation == 1 ? Isolated(Eu, "eu") : Legacy));
        var result = runner ? await machine.RunAsync("login", "--server", Eu)
            : await machine.RunAsync("login", "--server", Eu, "--token", "fixture-enroll");
        Assert.NotEqual(0, result.ExitCode);
        var restarted = new ConfigStore(machine.MbusFolder, "file");
        var floor = await restarted.WithProfileSecurityLockAsync(tx => Task.FromResult(tx.LoadBindingFloor()), Ct);
        Assert.Equal(Eu, Assert.Single(floor.Bindings).TransportOrigin);
        Assert.False(restarted.HasKey);
        machine.Env.Remove(ServerBindingExpectation.EnvironmentVariable);
        var retry = runner ? await machine.RunAsync("login", "--server", Eu)
            : await machine.RunAsync("login", "--server", Eu, "--token", "fixture-enroll");
        Assert.NotEqual(0, retry.ExitCode);
        Assert.All(machine.Server.Requests, request =>
        {
            Assert.Equal("/api/server", request.Uri.AbsolutePath);
            Assert.Null(request.Header("Authorization"));
            Assert.Empty(request.Body);
        });
        Assert.False(new ConfigStore(machine.MbusFolder, "file").HasKey);
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("null")]
    [InlineData("unsupported")]
    public async Task Invalid_first_security_bundle_refuses_before_a_later_legacy_observation(string invalid)
    {
        using var machine = new TestMachine();
        machine.Env[ConfigStore.KeyStoreVariable] = "file";
        var first = invalid switch
        {
            "partial" => """{"product":"messagebus.ai","signIn":{"machineSignature":{"version":2,"required":true}}}""",
            "null" => """{"product":"messagebus.ai","signIn":{"authority":null,"machineSignature":null}}""",
            _ => Isolated(Eu, "eu").Replace("\"version\":2", "\"version\":99", StringComparison.Ordinal),
        };
        var observations = 0;
        machine.Server.On(HttpMethod.Get, "/api/server", _ => Text(++observations == 1 ? first : Legacy));
        var result = await machine.RunAsync("login", "--server", Eu, "--token", "fixture-enroll");
        Assert.NotEqual(0, result.ExitCode);
        var request = Assert.Single(machine.Server.Requests);
        Assert.Equal("/api/server", request.Uri.AbsolutePath);
        Assert.Null(request.Header("Authorization"));
        Assert.Empty(request.Body);
        Assert.False(new ConfigStore(machine.MbusFolder, "file").HasKey);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Runner_logout_clears_provenance_and_restarts_for_new_login_without_erasing_floor(bool isolated, bool pending)
    {
        using var machine = new TestMachine();
        machine.Env[ConfigStore.KeyStoreVariable] = "file";
        machine.Env[EnvVars.ApiKey] = ApiKey;
        machine.Env[EnvVars.Server] = Eu;
        var stripped = false;
        machine.Server.On(HttpMethod.Get, "/api/server", _ => Text(isolated && !stripped ? Isolated(Eu, "eu") : Legacy));
        machine.Server.On(HttpMethod.Get, ApiRoutes.Caller, _ => Text("""{"scopes":["runner"]}"""));
        var loseResponse = pending;
        machine.Server.On(HttpMethod.Post, ApiRoutes.MachineEnroll, request =>
        {
            if (loseResponse) { loseResponse = false; throw new HttpRequestException("Owned fixture response loss"); }
            return FakeServer.Json(EnrollResponse(MessageBusJson.Deserialize<MachineEnrollRequest>(request.BodyText)!));
        });
        machine.Server.On(HttpMethod.Put, ApiRoutes.MachineInventory, _ => new(HttpStatusCode.NoContent));
        var first = await machine.RunAsync("login");
        if (pending) Assert.NotEqual(0, first.ExitCode);
        else Assert.True(first.ExitCode == 0, first.Error);
        var store = new ConfigStore(machine.MbusFolder, "file");
        var enrolled = store.Load();
        Assert.NotNull(enrolled.RunnerEnrollmentBinding);
        if (pending) Assert.NotNull(enrolled.PendingRunnerEnrollment);
        else Assert.NotNull(enrolled.EnrolledWithApiKey);
        var keyPath = store.KeyPath;
        var ledger = isolated ? File.ReadAllBytes(store.ServerBindingsPath) : null;

        var logout = await machine.RunAsync("logout");
        Assert.True(logout.ExitCode == 0, logout.Error);
        var restarted = new ConfigStore(machine.MbusFolder, "file");
        var reset = restarted.Load();
        Assert.Null(reset.MachineId);
        Assert.Null(reset.WorkspaceId);
        Assert.Null(reset.WorkspaceName);
        Assert.Null(reset.KeyFingerprint);
        Assert.Null(reset.EnrolledAt);
        Assert.Null(reset.EnrolledWithApiKey);
        Assert.Null(reset.PendingRunnerEnrollment);
        Assert.Null(reset.RunnerEnrollmentBinding);
        Assert.Null(reset.PendingRunnerIdentity);
        Assert.Equal(enrolled.ServerBinding, reset.ServerBinding);
        Assert.Equal(enrolled.MachineKeyGeneration, reset.MachineKeyGeneration);
        Assert.Equal(enrolled.KeyStore, reset.KeyStore);
        Assert.False(File.Exists(keyPath));
        Assert.False(restarted.HasKey);
        Assert.Null(restarted.LoadKeyPem());
        if (isolated)
        {
            Assert.Equal(ledger, File.ReadAllBytes(restarted.ServerBindingsPath));
            stripped = true;
            var before = machine.Server.Requests.Count;
            Assert.NotEqual(0, (await machine.RunAsync("login")).ExitCode);
            Assert.All(machine.Server.Requests.Skip(before), request => Assert.Null(request.Header("Authorization")));
            stripped = false;
        }
        var next = await machine.RunAsync("login");
        Assert.True(next.ExitCode == 0, next.Error);
        var completed = new ConfigStore(machine.MbusFolder, "file").Load();
        Assert.Equal(NewMachineId, completed.MachineId);
        Assert.NotEqual(enrolled.MachineKeyGeneration, completed.MachineKeyGeneration);
        Assert.NotEqual(enrolled.KeyFingerprint, completed.KeyFingerprint);
        Assert.NotNull(completed.RunnerEnrollmentBinding);
        Assert.Equal(enrolled.ServerBinding, completed.ServerBinding);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_deletes_independently_staged_runner_key_and_active_key(bool isolated)
    {
        using var machine = new TestMachine();
        machine.Env[ConfigStore.KeyStoreVariable] = "file";
        machine.Env[EnvVars.Server] = Eu;
        machine.Env[EnvVars.ApiKey] = ApiKey;
        var store = new ConfigStore(machine.MbusFolder, "file");
        using var old = MachineKey.Create();
        store.Save(new CliConfig { Server = Eu, MachineId = "mch_old", WorkspaceId = "w_same", KeyFingerprint = old.Fingerprint });
        store.SaveKeyPem(old.ExportPrivateKeyPem());
        var activeKeyPath = store.KeyPath;
        machine.Server.On(HttpMethod.Get, "/api/server", _ => Text(isolated ? Isolated(Eu, "eu") : Legacy));
        machine.Server.On(HttpMethod.Get, ApiRoutes.Caller, _ => Text("""{"scopes":["runner"]}"""));
        machine.Server.On(HttpMethod.Post, ApiRoutes.MachineEnroll, _ => throw new HttpRequestException("Owned fixture response loss"));
        Assert.NotEqual(0, (await machine.RunAsync("login")).ExitCode);
        var checkpoint = new ConfigStore(machine.MbusFolder, "file").Load();
        Assert.NotNull(checkpoint.PendingRunnerIdentity);
        var pendingKeyPath = Path.Combine(store.Directory, $"machine-key.{checkpoint.PendingRunnerIdentity.KeyGeneration}.pem");
        Assert.True(File.Exists(pendingKeyPath));
        var logout = await machine.RunAsync("logout");
        Assert.True(logout.ExitCode == 0, logout.Error);
        var restarted = new ConfigStore(machine.MbusFolder, "file");
        Assert.Null(restarted.Load().PendingRunnerIdentity);
        Assert.Equal(checkpoint.MachineKeyGeneration, restarted.Load().MachineKeyGeneration);
        Assert.False(restarted.HasKey);
        Assert.Null(restarted.LoadKeyPem());
        Assert.False(File.Exists(pendingKeyPath));
        Assert.False(File.Exists(activeKeyPath));
        Assert.Equal(checkpoint.ServerBinding, restarted.Load().ServerBinding);
        if (isolated) Assert.True(File.Exists(restarted.ServerBindingsPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_preserves_latest_settings_and_refuses_a_new_identity_published_during_discovery(bool replaceIdentity)
    {
        using var machine = new TestMachine();
        machine.Env[ConfigStore.KeyStoreVariable] = "file";
        var store = new ConfigStore(machine.MbusFolder, "file");
        using var old = MachineKey.Create();
        using var replacement = MachineKey.Create();
        store.Save(new CliConfig { Server = Eu, MachineId = "mch_old", WorkspaceId = "w_same", KeyFingerprint = old.Fingerprint });
        store.SaveKeyPem(old.ExportPrivateKeyPem());
        machine.Server.On(HttpMethod.Get, "/api/server", _ => Text(Legacy));
        byte[]? published = null;
        machine.DiscoveryHttpHandler = () => new DiscoveryHook(machine.Server, async () =>
        {
            if (published is not null) return;
            var competitor = new ConfigStore(machine.MbusFolder, "file");
            await competitor.WithProfileSecurityLockAsync(tx =>
            {
                var current = tx.Load();
                if (replaceIdentity) tx.CommitEnrollment(current, current with
                {
                    MachineId = NewMachineId, KeyFingerprint = replacement.Fingerprint,
                    MachineName = "new fixture name", Location = "new fixture location",
                }, replacement.ExportPrivateKeyPem(), Ct);
                else tx.Save(current with { MachineName = "new fixture name", Location = "new fixture location" });
                return Task.FromResult(true);
            }, Ct);
            published = File.ReadAllBytes(competitor.ConfigPath);
        });
        var result = await machine.RunAsync("logout");
        Assert.NotNull(published);
        var restarted = new ConfigStore(machine.MbusFolder, "file");
        Assert.Equal("new fixture name", restarted.Load().MachineName);
        Assert.Equal("new fixture location", restarted.Load().Location);
        if (replaceIdentity)
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Equal(published, File.ReadAllBytes(store.ConfigPath));
            Assert.Equal(NewMachineId, restarted.Load().MachineId);
            Assert.Equal(replacement.ExportPrivateKeyPem(), restarted.LoadKeyPem());
        }
        else
        {
            Assert.True(result.ExitCode == 0, result.Error);
            Assert.Null(restarted.Load().MachineId);
            Assert.Null(restarted.LoadKeyPem());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Remote_enrollment_or_new_key_proof_failure_preserves_previous_identity_and_trust(bool failProof)
    {
        using var machine = new TestMachine();
        machine.Env[ConfigStore.KeyStoreVariable] = "file";
        using var key = MachineKey.Create();
        var store = new ConfigStore(machine.MbusFolder, "file");
        store.Save(new CliConfig { Server = Ca, MachineId = "mch_old", WorkspaceId = "w_same", KeyFingerprint = key.Fingerprint });
        store.SaveKeyPem(key.ExportPrivateKeyPem());
        KeyTrust.Save(store.Directory, "w_same", new TrustedWorkspace(), Ca);
        var configBytes = File.ReadAllBytes(store.ConfigPath);
        var keyBytes = File.ReadAllBytes(store.KeyPath);
        var trustBytes = File.ReadAllBytes(Path.Combine(store.Directory, KeyTrust.FileName));
        machine.Server.On(HttpMethod.Get, "/api/server", _ => Text(Isolated(Eu, "eu")));
        machine.Server.On(HttpMethod.Post, ApiRoutes.MachineEnroll, request => failProof
            ? FakeServer.Json(EnrollResponse(MessageBusJson.Deserialize<MachineEnrollRequest>(request.BodyText)!))
            : new(HttpStatusCode.ServiceUnavailable));
        machine.Server.On(HttpMethod.Put, ApiRoutes.MachineInventory, _ => new(HttpStatusCode.Unauthorized));
        var result = await machine.RunAsync("login", "--server", Eu, "--token", "fixture-enroll");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(configBytes, File.ReadAllBytes(store.ConfigPath));
        Assert.Equal(keyBytes, File.ReadAllBytes(store.KeyPath));
        Assert.Equal(trustBytes, File.ReadAllBytes(Path.Combine(store.Directory, KeyTrust.FileName)));
        Assert.DoesNotContain(machine.Server.Requests, r => r.Header("Authorization") == "MbusMachine mch_old");
        Assert.True(File.Exists(store.ServerBindingsPath)); // failed enrollment retains its newly learned floor
    }

    [Fact]
    public async Task Runner_lost_response_checkpoints_key_and_scope_and_resumes_after_restart()
    {
        using var machine = new TestMachine();
        machine.Env[ConfigStore.KeyStoreVariable] = "file";
        machine.Env[EnvVars.ApiKey] = ApiKey;
        machine.Env[EnvVars.Server] = Eu + "/gateway";
        machine.Server.On(HttpMethod.Get, "/gateway/api/server", _ => Text(Isolated(Eu, "eu")));
        machine.Server.On(HttpMethod.Get, "/gateway" + ApiRoutes.Caller, request =>
        {
            Assert.Null(request.Header(MbusHeaders.Workspace));
            Assert.Equal("MbusKey " + ApiKey, request.Header("Authorization"));
            return Text("""{"scopes":["runner"]}""");
        });
        string? publicKey = null;
        var loseResponse = true;
        machine.Server.On(HttpMethod.Post, "/gateway" + ApiRoutes.MachineEnroll, request =>
        {
            var body = MessageBusJson.Deserialize<MachineEnrollRequest>(request.BodyText)!;
            Assert.Equal("", body.Inventory.MachineId);
            Assert.Null(request.Header(MbusHeaders.Workspace));
            if (publicKey is null) publicKey = body.PublicKey;
            else Assert.Equal(publicKey, body.PublicKey);
            if (loseResponse) { loseResponse = false; throw new HttpRequestException("Owned fixture response loss"); }
            return FakeServer.Json(EnrollResponse(body));
        });
        machine.Server.On(HttpMethod.Put, "/gateway" + ApiRoutes.MachineInventory, _ => new(HttpStatusCode.NoContent));
        Assert.NotEqual(0, (await machine.RunAsync("login")).ExitCode);
        var restarted = new ConfigStore(machine.MbusFolder, "file");
        var pending = restarted.Load();
        Assert.Null(pending.MachineId);
        Assert.Equal(RunnerKeyEnrollment.Fingerprint(ApiKey), pending.PendingRunnerEnrollment);
        Assert.Equal(Eu + "/gateway/", pending.RunnerEnrollmentBinding!.SelectedEndpoint);
        using (var persisted = MachineKey.FromPem(restarted.LoadKeyPem()!)) Assert.Equal(publicKey, persisted.PublicKeyBase64);
        var retry = await machine.RunAsync("login");
        Assert.True(retry.ExitCode == 0, retry.Error);
        var complete = restarted.Load();
        Assert.Null(complete.PendingRunnerEnrollment);
        Assert.Equal(pending.MachineKeyGeneration, complete.MachineKeyGeneration);
        Assert.Equal(pending.RunnerEnrollmentBinding, complete.RunnerEnrollmentBinding);
        // Further startup validates discovery without probing with the revoked bootstrap credential.
        var beforeReuse = machine.Server.Requests.Count;
        Assert.Equal(0, (await machine.RunAsync("login")).ExitCode);
        Assert.DoesNotContain(machine.Server.Requests.Skip(beforeReuse), request => request.Header("Authorization") is not null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_refuses_replaced_checkpoint_key_or_missing_ledger_despite_a_stripped_top_level_snapshot(bool stripLedger)
    {
        using var machine = new TestMachine();
        machine.Env[ConfigStore.KeyStoreVariable] = "file";
        machine.Env[EnvVars.ApiKey] = ApiKey;
        machine.Env[EnvVars.Server] = Eu;
        machine.Server.On(HttpMethod.Get, "/api/server", _ => Text(Isolated(Eu, "eu")));
        machine.Server.On(HttpMethod.Get, ApiRoutes.Caller, _ => Text("""{"scopes":["runner"]}"""));
        machine.Server.On(HttpMethod.Post, ApiRoutes.MachineEnroll, _ => throw new HttpRequestException("Owned fixture response loss"));
        Assert.NotEqual(0, (await machine.RunAsync("login")).ExitCode);
        var store = new ConfigStore(machine.MbusFolder, "file");
        var before = File.ReadAllBytes(store.ConfigPath);
        if (stripLedger)
        {
            var config = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.ConfigPath))!.AsObject();
            config.Remove("serverBinding");
            File.WriteAllText(store.ConfigPath, config.ToJsonString());
            File.Delete(store.ServerBindingsPath);
            Assert.Throws<ServerBindingException>(() => new ConfigStore(store.Directory, "file"));
        }
        else
        {
            using var substituted = MachineKey.Create();
            File.WriteAllText(store.KeyPath, substituted.ExportPrivateKeyPem());
            var afterLoss = machine.Server.Requests.Count;
            var restart = await machine.RunAsync("login");
            Assert.NotEqual(0, restart.ExitCode);
            Assert.Equal(before, File.ReadAllBytes(store.ConfigPath));
            Assert.All(machine.Server.Requests.Skip(afterLoss), request => Assert.Null(request.Header("Authorization")));
        }
    }

    [Theory]
    [InlineData(Ca, false)]
    [InlineData(Ca, true)]
    [InlineData(Eu, false)]
    [InlineData(Eu, true)]
    public async Task Occupied_runner_replacement_preserves_active_identity_on_loss_or_proof_failure_and_resumes(string selected, bool failProof)
    {
        using var machine = new TestMachine();
        machine.Env[ConfigStore.KeyStoreVariable] = "file";
        machine.Env[EnvVars.ApiKey] = ApiKey;
        machine.Env[EnvVars.Server] = selected;
        using var oldKey = MachineKey.Create();
        var store = new ConfigStore(machine.MbusFolder, "file");
        store.Save(new CliConfig { Server = Ca, MachineId = "mch_old", WorkspaceId = "w_source", KeyFingerprint = oldKey.Fingerprint,
            EnrolledWithApiKey = RunnerKeyEnrollment.Fingerprint("fixture-old-bootstrap") });
        store.SaveKeyPem(oldKey.ExportPrivateKeyPem());
        var active = store.Load();
        var activeKeyPath = store.KeyPath;
        machine.Server.On(HttpMethod.Get, "/api/server", _ => Text(Isolated(selected, selected == Ca ? "ca" : "eu")));
        machine.Server.On(HttpMethod.Get, ApiRoutes.Caller, request =>
        {
            Assert.Null(request.Header(MbusHeaders.Workspace));
            Assert.Equal("MbusKey " + ApiKey, request.Header("Authorization"));
            return Text("""{"scopes":["runner"]}""");
        });
        var failing = true;
        string? enrolledPublicKey = null;
        machine.Server.On(HttpMethod.Post, ApiRoutes.MachineEnroll, request =>
        {
            Assert.Null(request.Header(MbusHeaders.Workspace));
            var body = MessageBusJson.Deserialize<MachineEnrollRequest>(request.BodyText)!;
            Assert.Null(body.WorkspaceId);
            Assert.Equal("", body.Inventory.MachineId);
            Assert.NotEqual(oldKey.PublicKeyBase64, body.PublicKey);
            if (enrolledPublicKey is null) enrolledPublicKey = body.PublicKey;
            else Assert.Equal(enrolledPublicKey, body.PublicKey);
            if (failing && !failProof) throw new HttpRequestException("fixture lost response");
            return FakeServer.Json(EnrollResponse(body));
        });
        var proofs = 0;
        machine.Server.On(HttpMethod.Put, ApiRoutes.MachineInventory, request =>
        {
            proofs++;
            Assert.Equal("MbusMachine " + NewMachineId, request.Header("Authorization"));
            Assert.Equal("w_same", request.Header(MbusHeaders.Workspace));
            return new(failing ? HttpStatusCode.Unauthorized : HttpStatusCode.NoContent);
        });
        Assert.NotEqual(0, (await machine.RunAsync("login")).ExitCode);
        var restarted = new ConfigStore(machine.MbusFolder, "file");
        var checkpoint = restarted.Load();
        Assert.Equal(active.Server, checkpoint.Server);
        Assert.Equal(active.MachineId, checkpoint.MachineId);
        Assert.Equal(active.WorkspaceId, checkpoint.WorkspaceId);
        Assert.Equal(active.EnrolledWithApiKey, checkpoint.EnrolledWithApiKey);
        Assert.Equal(active.MachineKeyGeneration, checkpoint.MachineKeyGeneration);
        Assert.Equal(oldKey.ExportPrivateKeyPem(), restarted.LoadKeyPem());
        Assert.NotNull(checkpoint.PendingRunnerIdentity);
        Assert.Equal(ServerEndpoint.Parse(selected).Value, checkpoint.PendingRunnerIdentity.Binding.SelectedEndpoint);
        Assert.Equal(failProof ? 1 : 0, proofs);
        failing = false;
        var result = await machine.RunAsync("login");
        Assert.True(result.ExitCode == 0, result.Error);
        var completed = restarted.Load();
        Assert.Null(completed.PendingRunnerIdentity);
        Assert.Equal(checkpoint.PendingRunnerIdentity.KeyGeneration, completed.MachineKeyGeneration);
        Assert.Equal(NewMachineId, completed.MachineId);
        Assert.Equal(selected, completed.Server);
        Assert.Equal(oldKey.ExportPrivateKeyPem(), File.ReadAllText(activeKeyPath));
        Assert.DoesNotContain(machine.Server.Requests, r => r.Header("Authorization") == "MbusMachine mch_old");
        Assert.All(machine.Server.Requests, r => Assert.Equal(selected, r.Uri.GetLeftPart(UriPartial.Authority)));
    }

    [Theory]
    [InlineData(Eu)]
    [InlineData(Ca + "/different-base")]
    [InlineData("https://alias.stamps.example.test")]
    public async Task Equal_bootstrap_and_machine_fingerprints_do_not_reuse_checkpoint_at_another_endpoint(string selected)
    {
        using var machine = new TestMachine();
        machine.Env[ConfigStore.KeyStoreVariable] = "file";
        machine.Env[EnvVars.ApiKey] = ApiKey;
        using var key = MachineKey.Create();
        var store = new ConfigStore(machine.MbusFolder, "file");
        store.SaveKeyPem(key.ExportPrivateKeyPem());
        store.Save(new CliConfig { Server = Ca, PendingRunnerEnrollment = RunnerKeyEnrollment.Fingerprint(ApiKey), KeyFingerprint = key.Fingerprint });
        var before = File.ReadAllBytes(store.ConfigPath);
        machine.Server.On(HttpMethod.Get, new Uri(selected).AbsolutePath.TrimEnd('/') + "/api/server", _ => Text(Isolated(Eu, "eu")));
        var run = await machine.RunAsync("login", "--server", selected);
        Assert.NotEqual(0, run.ExitCode);
        Assert.Equal(before, File.ReadAllBytes(store.ConfigPath));
        using var preserved = MachineKey.FromPem(store.LoadKeyPem()!);
        Assert.Equal(key.Fingerprint, preserved.Fingerprint);
        Assert.All(machine.Server.Requests, request => Assert.Null(request.Header("Authorization")));
    }

    [Fact]
    public async Task Equal_key_and_workspace_on_independent_authorities_does_not_select_the_source_runner_signer()
    {
        using var machine = new TestMachine();
        machine.Env[ConfigStore.KeyStoreVariable] = "file";
        machine.Env[EnvVars.ApiKey] = ApiKey;
        using var key = MachineKey.Create();
        var source = new ConfigStore(machine.MbusFolder, "file");
        var target = new ConfigStore(Path.Combine(machine.Home, "target-fixture"), "file");
        foreach (var (store, origin, stamp) in new[] { (source, Ca, "ca"), (target, Eu, "eu") })
        {
            store.SaveKeyPem(key.ExportPrivateKeyPem());
            store.Save(new CliConfig { Server = origin, MachineId = "mch_same", WorkspaceId = "w_same", KeyFingerprint = key.Fingerprint,
                EnrolledWithApiKey = RunnerKeyEnrollment.Fingerprint(ApiKey) });
            var binding = await PinAsync(store, origin, Isolated(origin, stamp));
            store.Save(store.Load() with { RunnerEnrollmentBinding = new RunnerEnrollmentBinding
            {
                SelectedEndpoint = ServerEndpoint.Parse(origin).Value, ServerBinding = binding,
                ApiKeyFingerprint = RunnerKeyEnrollment.Fingerprint(ApiKey), MachineKeyFingerprint = key.Fingerprint,
            } });
        }
        var before = File.ReadAllBytes(source.ConfigPath);
        machine.Server.On(HttpMethod.Get, "/api/server", _ => Text(Isolated(Eu, "eu")));
        var selected = CliSettings.Resolve(machine.CreateHost(), source.Load(), Eu);
        Assert.Equal(ApiKey, selected.ApiKey);
        var result = await machine.RunAsync("login", "--server", Eu);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(before, File.ReadAllBytes(source.ConfigPath));
        Assert.All(machine.Server.Requests, request => Assert.Null(request.Header("Authorization")));
        Assert.Equal("mch_same", target.Load().MachineId);
    }

    public static IEnumerable<object[]> RedirectCases => from status in new[] { 301, 302, 303, 307, 308 }
        from sameOrigin in new[] { false, true } select new object[] { status, sameOrigin };

    [Theory]
    [MemberData(nameof(RedirectCases))]
    public async Task Actual_token_login_post_redirect_never_replays_to_target(int status, bool sameOrigin)
    {
        using var machine = new TestMachine();
        machine.Env[ConfigStore.KeyStoreVariable] = "file";
        using var source = new TcpListener(IPAddress.Loopback, 0);
        using var target = new TcpListener(IPAddress.Loopback, 0);
        source.Start(); target.Start();
        var server = $"http://127.0.0.1:{((IPEndPoint)source.LocalEndpoint).Port}";
        var destination = sameOrigin ? server + "/stolen" : $"http://127.0.0.1:{((IPEndPoint)target.LocalEndpoint).Port}/stolen";
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        stop.CancelAfter(TimeSpan.FromSeconds(10));
        var paths = new List<string>();
        var serving = ServeAsync();
        try
        {
            var basis = machine.CreateHost();
            var host = CopyHost(basis, () => new SocketsHttpHandler());
            var exit = await CliApp.RunAsync(["login", "--server", server, "--token", "fixture-enroll"], host, stop.Token);
            Assert.NotEqual(0, exit);
            Assert.Contains(paths, line => line.StartsWith("POST /api/machines/enroll ", StringComparison.Ordinal));
            Assert.DoesNotContain(paths, line => line.Contains("/stolen", StringComparison.Ordinal));
            Assert.False(target.Pending());
            Assert.False(new ConfigStore(machine.MbusFolder, "file").HasKey);
        }
        finally
        {
            stop.Cancel(); source.Stop();
            try { await serving; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }

        async Task ServeAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient connection;
                try { connection = await source.AcceptTcpClientAsync(stop.Token); }
                catch (ObjectDisposedException) when (stop.IsCancellationRequested) { return; }
                catch (SocketException) when (stop.IsCancellationRequested) { return; }
                using var accepted = connection;
                using var stream = accepted.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var first = await reader.ReadLineAsync(stop.Token) ?? "";
                var headers = new List<string>();
                for (var line = await reader.ReadLineAsync(stop.Token); !string.IsNullOrEmpty(line); line = await reader.ReadLineAsync(stop.Token)) headers.Add(line);
                paths.Add(first);
                var discovery = first.StartsWith("GET /api/server ", StringComparison.Ordinal);
                if (discovery) Assert.DoesNotContain(headers, h => h.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase));
                else Assert.Contains("Authorization: MbusEnroll fixture-enroll", headers);
                var reply = discovery ? $"HTTP/1.1 200 OK\r\nContent-Length: {Encoding.UTF8.GetByteCount(Legacy)}\r\nConnection: close\r\n\r\n{Legacy}"
                    : $"HTTP/1.1 {status} Redirect\r\nLocation: {destination}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.UTF8.GetBytes(reply), stop.Token);
            }
        }
    }

    private static CliHost CopyHost(CliHost basis, Func<HttpMessageHandler> transport) => new()
    {
        Out = basis.Out, Error = basis.Error, In = basis.In, OpenStandardInput = basis.OpenStandardInput,
        OpenStandardOutput = basis.OpenStandardOutput, GetEnvironmentVariable = basis.GetEnvironmentVariable,
        HomeDirectory = basis.HomeDirectory, MachineName = basis.MachineName, MbusCommand = basis.MbusCommand,
        Browser = basis.Browser, Skills = basis.Skills, McpServer = basis.McpServer, Time = basis.Time,
        CreateHttpHandler = transport, CreateDiscoveryHttpHandler = transport,
    };

    private static Task<ServerBindingSnapshot?> PinAsync(ConfigStore store, string endpoint, string discovery) =>
        store.WithProfileSecurityLockAsync(tx => Task.FromResult(tx.PrepareBinding(ServerEndpoint.Parse(endpoint), ServerDiscoveryParser.Parse(discovery))), Ct);

    private sealed class DiscoveryHook(HttpMessageHandler server, Func<Task> before) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await before();
            using var transport = new HttpMessageInvoker(server, disposeHandler: false);
            return await transport.SendAsync(request, ct);
        }
    }

    private const string Legacy = """{"product":"messagebus.ai","version":"0.0.1","signIn":{"microsoft":true}}""";
    private static string Isolated(string canonical, string stamp, string? alias = null) =>
        """{"product":"messagebus.ai","stamp":{"stampId":"$STAMP","geography":"$STAMP","azureRegion":"fixture","url":"$CANONICAL","aliases":[$ALIASES]},"signIn":{"authority":{"version":1,"stampId":"$STAMP","issuer":"$CANONICAL","audience":"$CANONICAL"},"machineSignature":{"version":2,"required":true}}}"""
            .Replace("$STAMP", stamp, StringComparison.Ordinal).Replace("$CANONICAL", canonical, StringComparison.Ordinal)
            .Replace("$ALIASES", alias is null ? "" : "\"" + alias + "\"", StringComparison.Ordinal);
    private static string Expected(string transport, string canonical, string stamp) =>
        $$"""{"version":1,"transportOrigin":"{{transport}}","stampId":"{{stamp}}","issuer":"{{canonical}}","audience":"{{canonical}}","authorityVersion":1,"minimumMachineSignatureVersion":2,"machineSignatureRequired":true}""";
    private static HttpResponseMessage Text(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    private static MachineEnrollResponse EnrollResponse(MachineEnrollRequest request) => new()
    {
        Machine = new MachineInfo { MachineId = NewMachineId, WorkspaceId = "w_same", OwnerSubjectId = "fixture-owner", Kind = MachineKind.Shared,
            Name = request.Name, Os = request.Inventory.Os, Architecture = request.Inventory.Architecture, EnrolledAt = DateTimeOffset.UnixEpoch,
            AcceptsWorkFrom = new AcceptsWorkFrom { Mode = AcceptsWorkFromMode.Workspace }, KeyFingerprint = "fixture" },
        WorkspaceName = "Fixture workspace", MinCliVersion = "0.0.1",
    };
    private sealed class FakeVault(string pem) : IMachineKeyVault
    {
        public string Description => "owned fixture vault";
        public string? Load() => pem;
        public void Save(string value) => throw new InvalidOperationException("The existing fixture key must not be written.");
        public void Delete() => throw new InvalidOperationException("The existing fixture key must not be deleted.");
    }
}
