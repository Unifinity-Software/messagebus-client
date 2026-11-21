using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Cli.Update;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Releases;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;
using MessageBus.Mcp;
using MessageBus.Tests.Shared;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class LoginTests
{
    [Theory]
    [InlineData(false, """{"error":"no-workspace","message":"Open the dashboard, join or create a workspace, and retry."}""", null,
        "Open the dashboard, join or create a workspace, and retry.")]
    [InlineData(true, """{"code":"no-workspace","message":"Open the dashboard, join or create a workspace, and retry."}""", null,
        "Open the dashboard, join or create a workspace, and retry.")]
    [InlineData(false, "", "no-workspace",
        "Your account doesn't belong to a workspace yet. Open the dashboard, join or create a workspace, then run mbus login again.")]
    [InlineData(true, """{"error":"no-workspace","title":"Forbidden","message":" "}""", null,
        "Your account doesn't belong to a workspace yet. Open the dashboard, join or create a workspace, then run mbus login again.")]
    [InlineData(false, """{"message":"Contact an administrator."}""", "forbidden",
        "Couldn't enroll this machine: you don't have permission in this workspace. Contact an administrator. (forbidden)")]
    [InlineData(true, """{"message":"Contact an administrator."}""", "forbidden",
        "Couldn't list your workspaces: you don't have permission in this workspace. Contact an administrator. (forbidden)")]
    public async Task Login_shows_the_appropriate_guidance_for_a_forbidden_response(
        bool duringWorkspaceLookup, string body, string? code, string expected)
    {
        using var machine = new TestMachine();
        HttpResponseMessage Forbidden()
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (code is not null) response.Headers.Add(MbusHeaders.Error, code);
            return response;
        }

        machine.Server
            .On(HttpMethod.Post, "/api/cli/auth/start", _ => Text(
                """{"deviceCode":"mbcd_fixture","userCode":"BCDF-GHJK","expiresIn":600,"interval":5}"""))
            .On(HttpMethod.Post, "/api/cli/auth/token", _ => Text(
                """{"state":"approved","accessToken":"mbca_fixture","refreshToken":"mbcr_fixture","expiresIn":3600,"account":{"name":"Ada"}}"""))
            .On(HttpMethod.Post, ApiRoutes.MachineEnroll, _ => duringWorkspaceLookup
                ? new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("""{"error":"workspace-required"}""", Encoding.UTF8, "application/json"),
                }
                : Forbidden())
            .On(HttpMethod.Get, ApiRoutes.MachineWorkspaces, _ => Forbidden());

        var run = await machine.RunAsync("login", "--server", FakeServer.Url);

        Assert.Equal(1, run.ExitCode);
        Assert.Equal(expected, run.Error.Trim());
        Assert.Equal(duringWorkspaceLookup, machine.Server.Requests.Any(r => r.Uri.AbsolutePath == ApiRoutes.MachineWorkspaces));
    }

    [Theory]
    [InlineData("")]
    [InlineData(""", "capabilities":["approvalAffectedV1","settleAttemptV1"]""")]
    [InlineData(""", "capabilities":["approvalAffectedV1","settleAttemptV1","roleProposalsV1"], "roleProposals":{"version":1,"baselineVersion":"role-proposal-readonly-v1","normalizerVersion":"role-definition-v1"}""")]
    public async Task Login_discovers_servers_with_optional_capability_tokens_and_saves_the_connection(string discoveryFields)
    {
        using var machine = new TestMachine();
        machine.Env["MBUS_API_KEY"] = "mbk_test";
        // Raw wire data keeps this regression independent of the CLI's discovery DTO.
        machine.Server
            .On(HttpMethod.Get, "/api/server", _ => Text(
                """{"product":"messagebus.ai","version":"0.1.0","signIn":{"microsoft":true,"cliApprovalUrl":"http://127.0.0.1:5200/cli-login"}"""
                + discoveryFields + "}"))
            .On(HttpMethod.Get, ApiRoutes.Caller, _ => Text("""{"kind":"api-key","scopes":["send"],"permissions":[]}"""))
            .On(HttpMethod.Put, ApiRoutes.MachineInventory, _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var run = await machine.RunAsync("login", "--server", FakeServer.Url);

        Assert.True(run.ExitCode == 0, run.Error);
        Assert.Equal(FakeServer.Url, new ConfigStore(machine.MbusFolder).Load().Server);
        var discoveries = machine.Server.Requests.Where(request => request.Method == HttpMethod.Get && request.Uri.AbsolutePath == "/api/server").ToArray();
        Assert.NotEmpty(discoveries);
        Assert.All(discoveries, request =>
        {
            Assert.Null(request.Header("Authorization"));
            Assert.Null(request.Header(MbusHeaders.Workspace));
            Assert.Null(request.Header(MbusHeaders.Signature));
            Assert.Empty(request.Body);
        });
        Assert.Equal("MbusKey mbk_test", machine.Server.Single(HttpMethod.Put, ApiRoutes.MachineInventory).Header("Authorization"));
    }

    [Theory]
    [InlineData("https://old.example", false)]
    [InlineData(FakeServer.Url + "/", true)]
    public async Task Login_keeps_the_update_record_only_for_the_same_server(string previousServer, bool keepRecord)
    {
        using var machine = new TestMachine();
        machine.Env[ConfigStore.KeyStoreVariable] = "file";
        var store = new ConfigStore(machine.MbusFolder, "file");
        using var previousKey = MessageBus.Cli.Security.MachineKey.Create();
        store.Save(new CliConfig { Server = previousServer, MachineId = "mch_previous", WorkspaceId = "w101",
            KeyFingerprint = previousKey.Fingerprint });
        store.SaveKeyPem(previousKey.ExportPrivateKeyPem());
        var keyBytes = File.ReadAllBytes(store.KeyPath);
        RunnerUpdateRecord.In(machine.MbusFolder).Save(new RunnerUpdateReport
        {
            Outcome = RunnerUpdateOutcome.Updated, FromVersion = "0.1.0", ToVersion = "0.2.0", At = machine.Time.GetUtcNow(),
        }, TextWriter.Null);
        machine.Env["MBUS_API_KEY"] = "mbk_test";
        machine.Server
            .On(HttpMethod.Get, ApiRoutes.Caller, _ => Text("""{"kind":"api-key","scopes":["send"],"permissions":[]}"""))
            .On(HttpMethod.Put, ApiRoutes.MachineInventory, _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var run = await machine.RunAsync("login", "--server", FakeServer.Url);

        Assert.True(run.ExitCode == 0, run.Error);
        Assert.Equal(FakeServer.Url, new ConfigStore(machine.MbusFolder).Load().Server);
        Assert.Equal(keepRecord, File.Exists(Path.Combine(machine.MbusFolder, RunnerUpdateRecord.FileName)));
        var saved = store.Load();
        Assert.Null(saved.MachineId);
        Assert.Null(saved.WorkspaceId);
        Assert.Equal(keyBytes, File.ReadAllBytes(store.KeyPath));
        Assert.DoesNotContain(machine.Server.Requests, request => request.Uri.AbsolutePath == ApiRoutes.MachineEnroll);
        Assert.All(machine.Server.Requests, request =>
        {
            Assert.Null(request.Header(MbusHeaders.Workspace));
            Assert.Null(request.Header(MbusHeaders.Signature));
            Assert.NotEqual("MbusMachine mch_previous", request.Header("Authorization"));
        });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public Task Damaged_trust_stops_login_before_enrolling_the_computer(bool runner, bool duringDiscovery) =>
        CheckDamagedTrustAsync(runner, duringDiscovery, "not JSON", legacy: false);

    public static TheoryData<bool, bool, string> DamagedTrustRecords => new(
        from runner in new[] { false, true }
        from legacy in new[] { false, true }
        from json in new[]
        {
            "{}",
            """{"servers":null}""",
            """{"servers":{"https://old.example":null}}""",
            """{"workspaces":{"w101":null}}""",
            """{"workspaces":{"w101":{"keys":null,"signers":{}}}}""",
            """{"workspaces":{"w101":{"keys":{},"signers":null}}}""",
            """{"workspaces":{"w101":{"keys":{"-":null},"signers":{}}}}""",
            """{"workspaces":{"w101":{"keys":{"-":[null]},"signers":{}}}}""",
            """{"workspaces":{"w101":{"keys":{"-":[{"version":1,"keyId":null,"fingerprint":"pin"}]},"signers":{}}}}""",
            """{"workspaces":{"w101":{"keys":{},"signers":{"mch_admin":null}}}}""",
            """{"workspaces":{"w101":{"keys":{},"signers":{"mch_admin":{"keyId":"ek_pin","fingerprint":null}}}}}""",
            """{"workspaces":{"w101":{"keys":{},"Keys":{},"signers":{}}}}""",
            """{"workspaces":{"w101":{"keys":{},"signers":{"mch_admin":{"keyId":"first","keyId":"second","fingerprint":"pin"}}}}}""",
            """{"servers":{},"Servers":{}}""",
            """{"workspaces":{"w101":{"keys":{},"signers":{}},"W101":{"keys":{},"signers":{}}}}""",
        }
        select (runner, legacy, json));

    [Theory]
    [MemberData(nameof(DamagedTrustRecords))]
    public Task Structurally_damaged_trust_refuses_real_login_without_writes(bool runner, bool legacy, string json) =>
        CheckDamagedTrustAsync(runner, false, json, legacy);

    private static async Task CheckDamagedTrustAsync(bool runner, bool duringDiscovery, string json, bool legacy)
    {
        using var machine = new TestMachine();
        machine.Env[ConfigStore.KeyStoreVariable] = "file";
        if (runner) machine.Env["MBUS_API_KEY"] = "fixture-runner-key";
        var store = new ConfigStore(machine.MbusFolder, "file");
        using var key = MessageBus.Cli.Security.MachineKey.Create();
        store.Save(new CliConfig { Server = "https://old.example", MachineId = "mch_previous", WorkspaceId = "w101",
            KeyFingerprint = key.Fingerprint });
        store.SaveKeyPem(key.ExportPrivateKeyPem());
        var configBytes = File.ReadAllBytes(store.ConfigPath);
        var keyBytes = File.ReadAllBytes(store.KeyPath);
        var path = Path.Combine(machine.MbusFolder, legacy ? "trusted-keys.json" : KeyTrust.FileName);
        if (duringDiscovery) KeyTrust.Save(machine.MbusFolder, "w101", new TrustedWorkspace(), "https://old.example");
        else File.WriteAllText(path, json);
        var trustBytes = Encoding.UTF8.GetBytes(json);
        var discoveries = 0;
        machine.Server
            .On(HttpMethod.Get, "/api/server", _ =>
            {
                if (duringDiscovery && ++discoveries == 2) File.WriteAllBytes(path, trustBytes);
                return FakeServer.Json(FakeServer.ConfiguredServerInfo);
            })
            .On(HttpMethod.Get, ApiRoutes.Caller, _ => Text("""{"scopes":["runner"]}"""))
            .On(HttpMethod.Post, ApiRoutes.MachineEnroll, request => FakeServer.Json(EnrollResponse(MessageBusJson.Deserialize<MachineEnrollRequest>(request.BodyText)!)))
            .On(HttpMethod.Put, ApiRoutes.MachineInventory, _ => new(HttpStatusCode.NoContent));

        var run = runner ? await machine.RunAsync("login", "--server", FakeServer.Url)
            : await machine.RunAsync("login", "--server", FakeServer.Url, "--token", "enroll-test");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains(path, run.Error);
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath == ApiRoutes.MachineEnroll);
        Assert.All(machine.Server.Requests, request => Assert.Null(request.Header("Authorization")));
        Assert.Equal(configBytes, File.ReadAllBytes(store.ConfigPath));
        Assert.Equal(keyBytes, File.ReadAllBytes(store.KeyPath));
        Assert.Equal(trustBytes, File.ReadAllBytes(path));
        Assert.DoesNotContain(Directory.EnumerateFiles(store.Directory), file => Path.GetFileName(file).StartsWith("machine-key.", StringComparison.Ordinal)
            && file.EndsWith(".pem", StringComparison.Ordinal) && file != store.KeyPath);
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("inventory")]
    public async Task Trust_damage_during_the_actual_credential_send_discovery_refuses_that_send(string boundary)
    {
        using var machine = new TestMachine();
        machine.Env[ConfigStore.KeyStoreVariable] = "file";
        machine.Env["MBUS_API_KEY"] = "fixture-key";
        var store = new ConfigStore(machine.MbusFolder, "file");
        using var key = MessageBus.Cli.Security.MachineKey.Create();
        store.Save(new CliConfig { Server = "https://old.example", MachineId = "mch_previous", WorkspaceId = "w101",
            KeyFingerprint = key.Fingerprint });
        store.SaveKeyPem(key.ExportPrivateKeyPem());
        KeyTrust.Save(machine.MbusFolder, "w101", new TrustedWorkspace(), "https://old.example");
        var configBytes = File.ReadAllBytes(store.ConfigPath);
        var keyBytes = File.ReadAllBytes(store.KeyPath);
        var trustPath = Path.Combine(machine.MbusFolder, KeyTrust.FileName);
        var damaged = Encoding.UTF8.GetBytes("""{"servers":{"https://old.example":{"w101":{"keys":null,"signers":{}}}}}""");
        var discoveries = 0;
        var callerObserved = false;
        var damagePublished = false;
        machine.Server.On(HttpMethod.Get, "/api/server", _ =>
        {
            discoveries++;
            // Observation 1 is Login's descriptive request; 2 is runner preparation. The next
            // observation belongs to the actual caller send. Inventory is identified by the
            // already-observed caller request, so this cannot accidentally damage the initial gate.
            if (boundary == "caller" && discoveries == 3 || boundary == "inventory" && callerObserved)
            {
                File.WriteAllBytes(trustPath, damaged);
                damagePublished = true;
            }
            return FakeServer.Json(FakeServer.ConfiguredServerInfo);
        });
        machine.Server.On(HttpMethod.Get, ApiRoutes.Caller, request =>
        {
            callerObserved = true;
            Assert.False(damagePublished);
            Assert.Equal("MbusKey fixture-key", request.Header("Authorization"));
            return Text("""{"kind":"api-key","scopes":["send"],"permissions":[]}""");
        });
        machine.Server.On(HttpMethod.Put, ApiRoutes.MachineInventory, _ => new(HttpStatusCode.NoContent));
        var run = await machine.RunAsync("login", "--server", FakeServer.Url);
        Assert.True(damagePublished, "The fixture must reach the discovery for the named actual send.");
        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains(trustPath, run.Error);
        Assert.Equal(boundary == "inventory", callerObserved);
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath == ApiRoutes.MachineInventory);
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath == ApiRoutes.MachineEnroll);
        if (boundary == "caller") Assert.All(machine.Server.Requests, r => Assert.Null(r.Header("Authorization")));
        Assert.Equal(configBytes, File.ReadAllBytes(store.ConfigPath));
        Assert.Equal(keyBytes, File.ReadAllBytes(store.KeyPath));
        Assert.Equal(damaged, File.ReadAllBytes(trustPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Saved_session_enrollment_checks_trust_at_the_actual_post_including_renewed_sign_in(bool renew)
    {
        await CheckSavedSessionEnrollmentAsync(renew, damageTrust: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_server_saved_session_enrolls_and_retains_the_current_account(bool renew)
    {
        await CheckSavedSessionEnrollmentAsync(renew, damageTrust: false);
    }

    private static async Task CheckSavedSessionEnrollmentAsync(bool renew, bool damageTrust)
    {
        using var machine = new TestMachine();
        machine.Env[ConfigStore.KeyStoreVariable] = "file";
        var store = new ConfigStore(machine.MbusFolder, "file");
        using var key = MessageBus.Cli.Security.MachineKey.Create();
        store.Save(new CliConfig { Server = FakeServer.Url, MachineId = "mch_previous", WorkspaceId = "w101",
            KeyFingerprint = key.Fingerprint, KeyStore = "file" });
        store.SaveKeyPem(key.ExportPrivateKeyPem());
        store.SaveSession(new CliSessionFile
        {
            Server = FakeServer.Url, AccessToken = "fixture-saved-access", RefreshToken = "fixture-saved-refresh",
            AccessExpiresAt = machine.Time.GetUtcNow().AddHours(renew ? -1 : 1), AccountName = "Previous account",
        });
        KeyTrust.Save(machine.MbusFolder, "w101", new TrustedWorkspace(), FakeServer.Url);
        var configBytes = File.ReadAllBytes(store.ConfigPath);
        var keyBytes = File.ReadAllBytes(store.KeyPath);
        var trustPath = Path.Combine(machine.MbusFolder, KeyTrust.FileName);
        var trustBytes = File.ReadAllBytes(trustPath);
        var damaged = Encoding.UTF8.GetBytes("""{"servers":{"http://127.0.0.1:5200":{"w101":{"keys":null,"signers":{}}}}}""");
        var discoveries = 0;
        var approved = false;
        var afterApproval = 0;
        var boundaryReached = false;
        var requestsBeforeDamage = 0;
        machine.Server.On(HttpMethod.Get, "/api/server", _ =>
        {
            discoveries++;
            if (approved) afterApproval++;
            // Fresh: descriptive discovery, captured binding, token validation, then actual POST discovery.
            // Renewed: after collecting approval, SaveApproved validates once, token validation once,
            // then the actual POST validates. Corruption must happen after the body's earlier trust check.
            if ((!renew && discoveries == 4) || (renew && afterApproval == 3))
            {
                boundaryReached = true;
                requestsBeforeDamage = machine.Server.Requests.Count;
                if (damageTrust) File.WriteAllBytes(trustPath, damaged);
            }
            // Raw old-server discovery: no new binding descriptors or capabilities required.
            return Text("""{"product":"messagebus.ai","version":"0.1.0","signIn":{"microsoft":true,"cliApprovalUrl":"http://127.0.0.1:5200/cli-login"}}""");
        });
        machine.Server
            .On(HttpMethod.Post, "/api/cli/auth/refresh", request =>
            {
                Assert.True(renew);
                Assert.False(boundaryReached);
                Assert.Contains("fixture-saved-refresh", request.BodyText);
                return new(HttpStatusCode.Unauthorized);
            })
            .On(HttpMethod.Post, "/api/cli/auth/start", _ => Text("""{"deviceCode":"fixture-device","userCode":"ABCD-EFGH","expiresIn":600,"interval":5}"""))
            .On(HttpMethod.Post, "/api/cli/auth/token", _ =>
            {
                approved = true;
                return Text("""{"state":"approved","accessToken":"fixture-renewed-access","refreshToken":"fixture-renewed-refresh","expiresIn":3600,"account":{"name":"Current account"}}""");
            })
            .On(HttpMethod.Post, ApiRoutes.MachineEnroll, request => FakeServer.Json(
                EnrollResponse(MessageBusJson.Deserialize<MachineEnrollRequest>(request.BodyText)!)))
            .On(HttpMethod.Put, ApiRoutes.MachineInventory, _ => new(HttpStatusCode.NoContent));

        var run = await machine.RunAsync("login", "--server", FakeServer.Url, "--no-browser");

        Assert.True(boundaryReached, "The actual enrollment POST's discovery must be observed.");
        Assert.Equal(renew, approved);
        Assert.Equal(renew ? "Current account" : "Previous account", store.LoadSession()!.AccountName);
        if (damageTrust)
        {
            Assert.NotEqual(0, run.ExitCode);
            Assert.Contains(trustPath, run.Error);
            Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath == ApiRoutes.MachineEnroll);
            Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath == ApiRoutes.MachineInventory);
            Assert.All(machine.Server.Requests.Skip(requestsBeforeDamage), r =>
            {
                Assert.Null(r.Header("Authorization"));
                Assert.Null(r.Header(MbusHeaders.Workspace));
                Assert.Empty(r.Body);
            });
            if (!renew) Assert.All(machine.Server.Requests, r => Assert.Null(r.Header("Authorization")));
            Assert.Equal(configBytes, File.ReadAllBytes(store.ConfigPath));
            Assert.Equal(keyBytes, File.ReadAllBytes(store.KeyPath));
            Assert.Equal(damaged, File.ReadAllBytes(trustPath));
            Assert.DoesNotContain(Directory.EnumerateFiles(store.Directory), file =>
                Path.GetFileName(file).StartsWith("machine-key.", StringComparison.Ordinal) && file != store.KeyPath);
        }
        else
        {
            Assert.True(run.ExitCode == 0, run.Error);
            var enroll = machine.Server.Single(HttpMethod.Post, ApiRoutes.MachineEnroll);
            Assert.Equal(renew ? "MbusCli fixture-renewed-access" : "MbusCli fixture-saved-access", enroll.Header("Authorization"));
            var body = MessageBusJson.Deserialize<MachineEnrollRequest>(enroll.BodyText)!;
            Assert.True(SignatureIsValid(machine.Server.Single(HttpMethod.Put, ApiRoutes.MachineInventory), body.PublicKey));
            Assert.Equal("mch_test", store.Load().MachineId);
            Assert.Null(store.LoadSession()!.ServerBinding);
            Assert.Equal(trustBytes, File.ReadAllBytes(trustPath));
        }
        Assert.Empty(machine.Browser.Opened);
    }

    [Fact]
    public Task Trust_damage_at_the_actual_enrollment_retry_after_a_successful_refresh_refuses_the_send() =>
        CheckEnrollmentRefreshRetryAsync(damageTrust: true);

    [Fact]
    public Task Legacy_server_enrollment_retries_with_the_refreshed_token_and_keeps_the_current_account() =>
        CheckEnrollmentRefreshRetryAsync(damageTrust: false);

    private static async Task CheckEnrollmentRefreshRetryAsync(bool damageTrust)
    {
        using var machine = new TestMachine();
        machine.Env[ConfigStore.KeyStoreVariable] = "file";
        var store = new ConfigStore(machine.MbusFolder, "file");
        using var previousKey = MessageBus.Cli.Security.MachineKey.Create();
        store.Save(new CliConfig { Server = FakeServer.Url, MachineId = "mch_previous", WorkspaceId = "w101",
            KeyFingerprint = previousKey.Fingerprint, KeyStore = "file" });
        store.SaveKeyPem(previousKey.ExportPrivateKeyPem());
        var savedSession = new CliSessionFile
        {
            Server = FakeServer.Url, AccessToken = "fixture-saved-access", RefreshToken = "fixture-saved-refresh",
            AccessExpiresAt = machine.Time.GetUtcNow().AddHours(1), SessionId = "fixture-session",
            AccountName = "Current account", AccountEmail = "member@example.test", SignedInAt = machine.Time.GetUtcNow(),
        };
        store.SaveSession(savedSession);
        KeyTrust.Save(machine.MbusFolder, "w101", new TrustedWorkspace(), FakeServer.Url);
        var configBytes = File.ReadAllBytes(store.ConfigPath);
        var keyBytes = File.ReadAllBytes(store.KeyPath);
        var trustPath = Path.Combine(machine.MbusFolder, KeyTrust.FileName);
        var trustBytes = File.ReadAllBytes(trustPath);
        var damaged = Encoding.UTF8.GetBytes("""{"servers":{"http://127.0.0.1:5200":{"w101":{"keys":null,"signers":{}}}}}""");
        RecordedRequest? firstEnroll = null;
        MachineEnrollRequest? enrolledBody = null;
        var refreshed = false;
        var retryBoundaryReached = false;
        var retryDiscoveryIndex = -1;
        machine.Server.On(HttpMethod.Get, "/api/server", _ =>
        {
            // The first discovery after a completed refresh is the retry's actual send gate.
            // The enrollment body and its earlier trust check already ran before the first POST.
            if (refreshed && !retryBoundaryReached)
            {
                Assert.NotNull(firstEnroll);
                Assert.Equal("fixture-refreshed-access", store.LoadSession()!.AccessToken);
                retryBoundaryReached = true;
                retryDiscoveryIndex = machine.Server.Requests.Count - 1;
                if (damageTrust) File.WriteAllBytes(trustPath, damaged);
            }
            // Exact old-server wire shape: no binding descriptors or capability tokens.
            return Text("""{"product":"messagebus.ai","version":"0.1.0","signIn":{"microsoft":true,"cliApprovalUrl":"http://127.0.0.1:5200/cli-login"}}""");
        });
        machine.Server
            .On(HttpMethod.Post, ApiRoutes.MachineEnroll, request =>
            {
                if (firstEnroll is null)
                {
                    Assert.False(refreshed);
                    Assert.False(retryBoundaryReached);
                    Assert.Equal("MbusCli fixture-saved-access", request.Header("Authorization"));
                    Assert.Null(request.Header(MbusHeaders.Workspace));
                    Assert.Null(request.Header(MbusHeaders.Signature));
                    enrolledBody = MessageBusJson.Deserialize<MachineEnrollRequest>(request.BodyText)!;
                    Assert.Equal("w101", enrolledBody.WorkspaceId);
                    Assert.Equal("test-mac", enrolledBody.Name);
                    Assert.Equal("Fixture desk", enrolledBody.Location);
                    Assert.Equal("mch_previous", enrolledBody.Inventory.MachineId);
                    Assert.NotEmpty(enrolledBody.PublicKey);
                    Assert.NotEqual(previousKey.PublicKeyBase64, enrolledBody.PublicKey);
                    firstEnroll = request;
                    return new(HttpStatusCode.Unauthorized);
                }
                Assert.False(damageTrust);
                Assert.True(retryBoundaryReached);
                Assert.Equal("MbusCli fixture-refreshed-access", request.Header("Authorization"));
                Assert.Equal(firstEnroll.Body, request.Body);
                return FakeServer.Json(EnrollResponse(enrolledBody!));
            })
            .On(HttpMethod.Post, "/api/cli/auth/refresh", request =>
            {
                Assert.NotNull(firstEnroll);
                Assert.False(refreshed);
                Assert.False(retryBoundaryReached);
                Assert.Null(request.Header("Authorization"));
                using var refreshBody = JsonDocument.Parse(request.BodyText);
                var refreshToken = Assert.Single(refreshBody.RootElement.EnumerateObject());
                Assert.Equal("refreshToken", refreshToken.Name);
                Assert.Equal("fixture-saved-refresh", refreshToken.Value.GetString());
                refreshed = true;
                return Text("""{"accessToken":"fixture-refreshed-access","refreshToken":"fixture-refreshed-refresh","expiresIn":3600}""");
            })
            .On(HttpMethod.Put, ApiRoutes.MachineInventory, _ => new(HttpStatusCode.NoContent));

        var run = await machine.RunAsync("login", "--server", FakeServer.Url, "--workspace", "w101",
            "--location", "Fixture desk", "--no-browser");

        Assert.NotNull(firstEnroll);
        Assert.True(refreshed, "The first enrollment's 401 must trigger a successful refresh.");
        Assert.True(retryBoundaryReached, "The actual retry discovery must run after refresh publication.");
        machine.Server.Single(HttpMethod.Post, "/api/cli/auth/refresh");
        Assert.Equal(savedSession with
        {
            AccessToken = "fixture-refreshed-access", RefreshToken = "fixture-refreshed-refresh",
            AccessExpiresAt = machine.Time.GetUtcNow().AddHours(1),
        }, store.LoadSession());
        Assert.Empty(machine.Browser.Opened);
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath is "/api/cli/auth/start" or "/api/cli/auth/token");
        if (damageTrust)
        {
            Assert.NotEqual(0, run.ExitCode);
            Assert.Contains(trustPath, run.Error);
            Assert.Same(firstEnroll, machine.Server.Single(HttpMethod.Post, ApiRoutes.MachineEnroll));
            Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath == ApiRoutes.MachineInventory);
            Assert.All(machine.Server.Requests.Skip(retryDiscoveryIndex), request =>
            {
                Assert.Null(request.Header("Authorization"));
                Assert.Null(request.Header(MbusHeaders.Workspace));
                Assert.Null(request.Header(MbusHeaders.Signature));
                Assert.Empty(request.Body);
            });
            Assert.Equal(configBytes, File.ReadAllBytes(store.ConfigPath));
            Assert.Equal(keyBytes, File.ReadAllBytes(store.KeyPath));
            Assert.Equal(damaged, File.ReadAllBytes(trustPath));
            Assert.DoesNotContain(Directory.EnumerateFiles(store.Directory), file =>
                Path.GetFileName(file).StartsWith("machine-key.", StringComparison.Ordinal) && file != store.KeyPath);
        }
        else
        {
            Assert.True(run.ExitCode == 0, run.Error);
            Assert.Equal(2, machine.Server.Requests.Count(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == ApiRoutes.MachineEnroll));
            var inventory = machine.Server.Single(HttpMethod.Put, ApiRoutes.MachineInventory);
            Assert.Equal("MbusMachine mch_test", inventory.Header("Authorization"));
            Assert.Equal("w101", inventory.Header(MbusHeaders.Workspace));
            Assert.True(SignatureIsValid(inventory, enrolledBody!.PublicKey));
            Assert.Equal("mch_test", MessageBusJson.Deserialize<MachineInventory>(inventory.BodyText)!.MachineId);
            Assert.Equal("mch_test", store.Load().MachineId);
            Assert.Equal("w101", store.Load().WorkspaceId);
            Assert.Null(store.Load().ServerBinding);
            using var publishedKey = MessageBus.Cli.Security.MachineKey.FromPem(store.LoadKeyPem()!);
            Assert.Equal(enrolledBody.PublicKey, publishedKey.PublicKeyBase64);
            Assert.Equal(trustBytes, File.ReadAllBytes(trustPath));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_and_legacy_trust_validate_without_importing_or_rewriting(bool legacy)
    {
        using var machine = new TestMachine();
        var store = new ConfigStore(machine.MbusFolder, "file");
        store.Save(new CliConfig { Server = "https://old.example" });
        const string workspace = """{"w101":{"keys":{"-":[{"version":1,"keyId":"ek_pin","fingerprint":"saved pin"}]},"signers":{"mch_admin":{"keyId":"ek_admin","fingerprint":"admin pin"}}}}""";
        var json = legacy ? "{\"workspaces\":" + workspace + "}" : "{\"servers\":{\"https://old.example\":" + workspace + "}}";
        var path = Path.Combine(machine.MbusFolder, legacy ? "trusted-keys.json" : KeyTrust.FileName);
        File.WriteAllText(path, json);
        await store.WithProfileSecurityLockAsync(tx => { tx.DemandEnrollmentTrust(); return Task.FromResult(true); },
            TestContext.Current.CancellationToken);
        Assert.Equal(Encoding.UTF8.GetBytes(json), File.ReadAllBytes(path));
        if (legacy) Assert.False(File.Exists(Path.Combine(machine.MbusFolder, KeyTrust.FileName)));
    }

    [Theory]
    [InlineData("\n", false)]
    [InlineData("n\n", false)]
    [InlineData("y\n", true)]
    public Task Moving_servers_only_trusts_admins_after_an_explicit_yes(string input, bool accepted) =>
        CheckServerMoveAsync(true, input, accepted);

    [Fact]
    public Task Moving_servers_without_a_terminal_leaves_admin_trust_for_the_explicit_command() =>
        CheckServerMoveAsync(false, "", false);

    [Fact]
    public Task A_key_changed_during_confirmation_is_refused_without_failing_login() =>
        CheckServerMoveAsync(true, "y\n", false, changedWhileChecking: true);

    [Fact]
    public Task Admin_trust_accepts_the_checked_signing_key_even_if_a_newer_device_key_exists() =>
        CheckServerMoveAsync(true, "y\n", true, newerDeviceKey: true);

    [Fact]
    public Task Failure_to_read_admin_computers_does_not_fail_successful_login() =>
        CheckServerMoveAsync(true, "y\n", false, damagedDirectory: true);

    private static async Task CheckServerMoveAsync(bool terminal, string input, bool accepted,
        bool changedWhileChecking = false, bool newerDeviceKey = false, bool damagedDirectory = false)
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MachineKeyReason);
        using var machine = new TestMachine { Input = input };
        var store = new ConfigStore(machine.MbusFolder);
        const string oldServer = "https://old.example";
        store.Save(new CliConfig { Server = oldServer, WorkspaceId = "w101", MachineId = "mch_old" });
        var oldPins = new TrustedWorkspace();
        oldPins.Signers["mch_admin"] = new TrustedSigner("ek_old", "old fingerprint");
        KeyTrust.Save(machine.MbusFolder, "w101", oldPins, oldServer);
        using var adminKey = KeyPair.GenerateP256();
        var admin = new PublicKeyInfo
        {
            KeyId = "ek_admin", Scope = KeyScope.Machine, Owner = "mch_admin", Version = 1,
            Algorithm = adminKey.Algorithm, PublicKey = adminKey.PublicKey.ToBase64(),
            Fingerprint = adminKey.PublicKey.Fingerprint, CreatedAt = DateTimeOffset.UnixEpoch,
        };
        using var roleKey = KeyPair.GenerateP256();
        var role = admin with
        {
            KeyId = "ek_role", Scope = KeyScope.Role, Owner = "developer", PublicKey = roleKey.PublicKey.ToBase64(),
            Fingerprint = roleKey.PublicKey.Fingerprint, SigningKeyId = admin.KeyId,
            Signature = KeySignatures.Sign("w101", KeyScope.Role, "developer", roleKey.PublicKey, adminKey),
        };
        var directoryReads = 0;
        machine.Server
            .On(HttpMethod.Post, ApiRoutes.MachineEnroll, request => FakeServer.Json(EnrollResponse(MessageBusJson.Deserialize<MachineEnrollRequest>(request.BodyText)!)))
            .On(HttpMethod.Put, ApiRoutes.MachineInventory, _ => new HttpResponseMessage(HttpStatusCode.NoContent))
            .On(HttpMethod.Get, ApiRoutes.Caller, _ => Text("""{"subjectId":"member","permissions":[]}"""))
            .On(HttpMethod.Get, "/api/w/w101/machines", _ => Text("""
                [{"machineId":"mch_admin","name":"Admin laptop","kind":"personal"}, {"machineId":"mch_member","kind":"personal"}]
                """))
            .On(HttpMethod.Get, "/api/w/w101/keys", _ => damagedDirectory ? Text("not JSON") : FakeServer.Json<IReadOnlyList<PublicKeyInfo>>([
                changedWhileChecking && directoryReads++ > 0 ? admin with { Fingerprint = "changed" } : admin,
                role, admin with { Owner = "mch_member", KeyId = "ek_member" },
                .. newerDeviceKey ? new[] { admin with { KeyId = "ek_newer", Version = 2, Fingerprint = "newer fingerprint" } } : Array.Empty<PublicKeyInfo>()]));
        var output = new StringWriter();
        var error = new StringWriter();
        string[] args = ["login", "--server", FakeServer.Url, "--token", "enroll-test"];
        var exit = await CliApp.RunAsync(args, machine.CreateHost(output, error, terminal), TestContext.Current.CancellationToken);
        Assert.True(exit == 0, error.ToString());
        Assert.Contains("Starting a new trust record", output.ToString());
        Assert.Equal(terminal && !damagedDirectory, output.ToString().Contains("[y/N]", StringComparison.Ordinal));
        if (terminal && !damagedDirectory)
        {
            Assert.DoesNotContain("mch_member", output.ToString());
            Assert.Contains("Admin laptop (mch_admin)", output.ToString());
        }
        else if (!terminal) Assert.Contains("mbus keys trust --admins", output.ToString());
        if (damagedDirectory) Assert.Contains("Try again later: mbus keys trust --admins", output.ToString());
        if (changedWhileChecking) Assert.Contains("key changed while you were checking", output.ToString());
        var pins = KeyTrust.Load(machine.MbusFolder, "w101", FakeServer.Url);
        Assert.Equal(accepted, pins.Signers.ContainsKey("mch_admin"));
        Assert.False(pins.Signers.ContainsKey("mch_member"));
        if (accepted) Assert.Equal(admin.Fingerprint, pins.Signers["mch_admin"].Fingerprint);
        Assert.Equal("old fingerprint", KeyTrust.Load(machine.MbusFolder, "w101", oldServer).Signers["mch_admin"].Fingerprint);

        output.GetStringBuilder().Clear();
        exit = await CliApp.RunAsync(args, machine.CreateHost(output, error, terminal), TestContext.Current.CancellationToken);
        Assert.True(exit == 0, error.ToString());
        Assert.DoesNotContain("Starting a new trust record", output.ToString());
        Assert.DoesNotContain("[y/N]", output.ToString());

        // The command printed by unattended login uses the same trust path later.
        if (!terminal)
        {
            var trust = await machine.RunAsync("keys", "trust", "--admins", "--yes");
            Assert.True(trust.ExitCode == 0, trust.Error);
            Assert.Equal(admin.Fingerprint, KeyTrust.Load(machine.MbusFolder, "w101", FakeServer.Url).Signers["mch_admin"].Fingerprint);
        }
    }

    [Fact]
    public async Task Login_approved_in_the_browser_enrolls_with_that_sign_in_and_signs_later_requests_with_the_new_key()
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MbusReason);
        using var machine = new TestMachine();
        machine.InstallAgent("claude", "2.1.3 (Claude Code)");
        MachineEnrollRequest? enrolled = null;
        machine.Server
            .On(HttpMethod.Post, "/api/cli/auth/start", _ => Text("""{"deviceCode":"mbcd_secret","userCode":"BCDF-GHJK","expiresIn":600,"interval":5}"""))
            .On(HttpMethod.Post, "/api/cli/auth/token", _ => Text(
                """{"state":"approved","accessToken":"mbca_access","refreshToken":"mbcr_refresh","expiresIn":3600,"account":{"name":"Ada"}}"""))
            .On(HttpMethod.Post, ApiRoutes.MachineEnroll, request =>
            {
                enrolled = MessageBusJson.Deserialize<MachineEnrollRequest>(request.BodyText);
                return FakeServer.Json(EnrollResponse(enrolled!));
            })
            .On(HttpMethod.Put, ApiRoutes.MachineInventory, _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var run = await machine.RunAsync("login", "--server", FakeServer.Url + "/", "--location", "Mac mini");

        Assert.Equal(0, run.ExitCode);

        // The approval page opened with the code it printed.
        Assert.Equal(new Uri(FakeServer.Url + "/cli-login?code=BCDF-GHJK"), Assert.Single(machine.Browser.Opened));
        Assert.Contains("BCDF-GHJK", run.Output);

        // Enrollment carried the service's sign-in, the label and the first scan.
        var enroll = machine.Server.Single(HttpMethod.Post, ApiRoutes.MachineEnroll);
        Assert.Equal("MbusCli mbca_access", enroll.Header("Authorization"));
        Assert.Equal("Mac mini", enrolled!.Location);
        Assert.Equal("test-mac", enrolled.Name);
        Assert.Contains(enrolled.Inventory.Agents, a => a is { Kind: AgentKind.ClaudeCode, Found: true, Version: "2.1.3" });

        // The next request is signed with the key whose public half was enrolled.
        var inventory = machine.Server.Single(HttpMethod.Put, ApiRoutes.MachineInventory);
        Assert.Equal("MbusMachine mch_test", inventory.Header("Authorization"));
        Assert.Equal("w101", inventory.Header(MbusHeaders.Workspace));
        Assert.Equal(machine.Time.GetUtcNow().ToUnixTimeSeconds().ToString(), inventory.Header(MbusHeaders.Timestamp));
        Assert.True(SignatureIsValid(inventory, enrolled.PublicKey));
        Assert.Equal("mch_test", MessageBusJson.Deserialize<MachineInventory>(inventory.BodyText)!.MachineId);

        // Settings, key and sign-in are saved, the key and the sign-in readable by their owner only; on Windows
        // the key is protected for this user (DPAPI) and never written out in the clear (the original issue).
        var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(machine.MbusFolder, "config.json"))).RootElement;
        Assert.Equal(FakeServer.Url, config.GetProperty("server").GetString());
        Assert.Equal("mch_test", config.GetProperty("machineId").GetString());
        var generation = config.GetProperty("machineKeyGeneration").GetString();
        var keyFile = Path.Combine(machine.MbusFolder, $"machine-key.{generation}.pem");
        var sessionFile = Path.Combine(machine.MbusFolder, "cli-session.json");
        // The test machine's key store (the file on macOS), so the check doesn't move the test key into the login Keychain.
        Assert.Contains("PRIVATE KEY", new ConfigStore(machine.MbusFolder, machine.Env.GetValueOrDefault(ConfigStore.KeyStoreVariable)).LoadKeyPem());
        Assert.Contains("mbcr_refresh", File.ReadAllText(sessionFile));
        if (OperatingSystem.IsWindows())
        {
            Assert.False(File.Exists(keyFile));
            Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(Path.Combine(machine.MbusFolder, $"machine-key.{generation}.dpapi")));
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyFile));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(sessionFile));
        }
    }

    [Fact]
    public void A_key_file_from_an_older_mbus_moves_into_the_os_key_store_on_first_use()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "On macOS this would use the login Keychain; check it by hand on a Mac.");
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mbus-key-tests", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            using var key = MessageBus.Cli.Security.MachineKey.Create();
            var pem = key.ExportPrivateKeyPem();
            var legacy = Path.Combine(folder, "machine-key.pem");
            File.WriteAllText(legacy, pem);

            Assert.Equal(pem, new ConfigStore(folder).LoadKeyPem());

            // Windows: now only in DPAPI's keeping, and still the same key next time. Elsewhere the owner-only file is the store.
            Assert.Equal(!OperatingSystem.IsWindows(), File.Exists(legacy));
            Assert.Equal(pem, new ConfigStore(folder).LoadKeyPem());
            // MBUS_KEY_STORE=file can't read it any more on Windows: it really moved.
            Assert.Equal(OperatingSystem.IsWindows() ? null : pem, new ConfigStore(folder, "file").LoadKeyPem());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task A_runner_api_key_enrolls_a_shared_runner_once_and_from_then_on_mbus_signs_as_it_without_the_key()
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MbusReason);
        // the original issue: a container with only MBUS_SERVER and a runner key in MBUS_API_KEY.
        using var machine = new TestMachine();
        machine.Env["MBUS_SERVER"] = FakeServer.Url;
        machine.Env["MBUS_API_KEY"] = "mbk_runner";
        machine.Env["MBUS_MACHINE_NAME"] = "docker-runner";
        MachineEnrollRequest? enrolled = null;
        machine.Server
            .On(HttpMethod.Get, ApiRoutes.Caller, _ => Text("""{"kind":"api-key","scopes":["runner"],"permissions":[]}"""))
            .On(HttpMethod.Post, ApiRoutes.MachineEnroll, request =>
            {
                enrolled = MessageBusJson.Deserialize<MachineEnrollRequest>(request.BodyText);
                return FakeServer.Json(EnrollResponse(enrolled!));
            })
            .On(HttpMethod.Put, ApiRoutes.MachineInventory, _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var first = await machine.RunAsync("login");
        Assert.True(first.ExitCode == 0, first.Error);
        var enroll = machine.Server.Single(HttpMethod.Post, ApiRoutes.MachineEnroll);
        Assert.Equal($"{AuthSchemes.ApiKey} mbk_runner", enroll.Header("Authorization"));
        Assert.Equal("docker-runner", enrolled!.Name);
        var inventory = machine.Server.Single(HttpMethod.Put, ApiRoutes.MachineInventory);
        Assert.Equal("MbusMachine mch_test", inventory.Header("Authorization"));
        Assert.True(SignatureIsValid(inventory, enrolled.PublicKey));

        // Started again with the same key: already that machine, nothing enrolls again and the key isn't sent.
        var sent = machine.Server.Requests.Count;
        var again = await machine.RunAsync("login");
        Assert.True(again.ExitCode == 0, again.Error);
        Assert.Contains("docker-runner", again.Output);
        Assert.DoesNotContain(machine.Server.Requests.Skip(sent), r => r.Header("Authorization")?.StartsWith(AuthSchemes.ApiKey, StringComparison.Ordinal) == true);
        Assert.Single(machine.Server.Requests, r => r.Uri.AbsolutePath == ApiRoutes.MachineEnroll);
    }

    [Fact]
    public async Task Pending_Azure_enrollment_reuses_the_persisted_key_after_a_lost_response()
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MbusReason);
        using var machine = new TestMachine();
        machine.Env["MBUS_SERVER"] = FakeServer.Url;
        machine.Env["MBUS_API_KEY"] = "mbk_fixture_runner";
        machine.Env["MBUS_KEY_STORE"] = "file";
        using var key = MessageBus.Cli.Security.MachineKey.Create();
        var store = new ConfigStore(machine.MbusFolder, "file");
        store.SaveKeyPem(key.ExportPrivateKeyPem());
        store.Save(new CliConfig { Server = FakeServer.Url, KeyStore = "file",
            PendingRunnerEnrollment = MessageBus.Cli.Runner.RunnerKeyEnrollment.Fingerprint("mbk_fixture_runner") });
        var attempts = 0;
        machine.Server
            .On(HttpMethod.Get, ApiRoutes.Caller, _ => Text("""{"kind":"api-key","scopes":["runner"]}"""))
            .On(HttpMethod.Post, ApiRoutes.MachineEnroll, request =>
            {
                var body = MessageBusJson.Deserialize<MachineEnrollRequest>(request.BodyText)!;
                Assert.Equal(key.PublicKeyBase64, body.PublicKey);
                if (++attempts == 1) throw new HttpRequestException("Simulated enrollment response loss");
                return FakeServer.Json(EnrollResponse(body));
            })
            .On(HttpMethod.Put, ApiRoutes.MachineInventory, _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        Assert.NotEqual(0, (await machine.RunAsync("login")).ExitCode);
        Assert.NotNull(store.Load().PendingRunnerEnrollment);
        var retry = await machine.RunAsync("login");
        Assert.True(retry.ExitCode == 0, retry.Error);
        Assert.Null(store.Load().PendingRunnerEnrollment);
        Assert.Equal("mch_test", store.Load().MachineId);
        using var restored = MessageBus.Cli.Security.MachineKey.FromPem(store.LoadKeyPem()!);
        Assert.Equal(key.PublicKeyBase64, restored.PublicKeyBase64);
    }

    [Theory]
    [InlineData("not messagebus.ai")]
    [InlineData("no browser approval")]
    public async Task Login_stops_before_sign_in_when_the_server_cannot_take_it(string problem)
    {
        using var machine = new TestMachine();
        var info = problem == "not messagebus.ai"
            ? FakeServer.ConfiguredServerInfo with { Product = "something-else" }
            : FakeServer.ConfiguredServerInfo with { SignIn = FakeServer.ConfiguredServerInfo.SignIn! with { CliApprovalUrl = null } };
        machine.Server.On(HttpMethod.Get, "/api/server", _ => FakeServer.Json(info));

        var run = await machine.RunAsync("login", "--server", FakeServer.Url);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Empty(machine.Browser.Opened);
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath.StartsWith("/api/cli/", StringComparison.Ordinal));
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath == ApiRoutes.MachineEnroll);
        Assert.False(File.Exists(Path.Combine(machine.MbusFolder, "config.json")));
    }

    private static HttpResponseMessage Text(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static MachineEnrollResponse EnrollResponse(MachineEnrollRequest request) => new()
    {
        Machine = new MachineInfo
        {
            MachineId = "mch_test",
            WorkspaceId = "w101",
            OwnerSubjectId = "microsoft:owner",
            Kind = MachineKind.Personal,
            Name = request.Name,
            Location = request.Location,
            Os = request.Inventory.Os,
            Architecture = request.Inventory.Architecture,
            AcceptsWorkFrom = new AcceptsWorkFrom { Mode = AcceptsWorkFromMode.OwnProjects },
            KeyFingerprint = "unused",
            EnrolledAt = DateTimeOffset.UnixEpoch,
        },
        WorkspaceName = "Contoso",
        MinCliVersion = "0.0.1",
    };

    /// <summary>Verifies a machine request the way the service does (<see cref="MachineRequestSigning"/>).</summary>
    private static bool SignatureIsValid(RecordedRequest request, string publicKeyBase64)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
        var canonical = MachineRequestSigning.CanonicalString(
            request.Method.Method,
            request.Uri.PathAndQuery,
            long.Parse(request.Header(MbusHeaders.Timestamp)!),
            request.Header(MbusHeaders.Nonce)!,
            Convert.ToHexStringLower(SHA256.HashData(request.Body)));
        return key.VerifyData(
            Encoding.UTF8.GetBytes(canonical),
            Base64Url.DecodeFromChars(request.Header(MbusHeaders.Signature)!),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
