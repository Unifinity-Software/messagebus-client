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
    [InlineData("https://old.example", false)]
    [InlineData(FakeServer.Url + "/", true)]
    public async Task Login_keeps_the_update_record_only_for_the_same_server(string previousServer, bool keepRecord)
    {
        using var machine = new TestMachine();
        new ConfigStore(machine.MbusFolder).Save(new CliConfig { Server = previousServer });
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
    }

    [Fact]
    public async Task Damaged_trust_stops_login_before_enrolling_the_computer()
    {
        using var machine = new TestMachine();
        var path = Path.Combine(machine.MbusFolder, KeyTrust.FileName);
        Directory.CreateDirectory(machine.MbusFolder);
        File.WriteAllText(path, "not JSON");

        var run = await machine.RunAsync("login", "--server", FakeServer.Url, "--token", "enroll-test");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains(path, run.Error);
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath == ApiRoutes.MachineEnroll);
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
        var keyFile = Path.Combine(machine.MbusFolder, "machine-key.pem");
        var sessionFile = Path.Combine(machine.MbusFolder, "cli-session.json");
        // The test machine's key store (the file on macOS), so the check doesn't move the test key into the login Keychain.
        Assert.Contains("PRIVATE KEY", new ConfigStore(machine.MbusFolder, machine.Env.GetValueOrDefault(ConfigStore.KeyStoreVariable)).LoadKeyPem());
        Assert.Contains("mbcr_refresh", File.ReadAllText(sessionFile));
        if (OperatingSystem.IsWindows())
        {
            Assert.False(File.Exists(keyFile));
            Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(Path.Combine(machine.MbusFolder, "machine-key.dpapi")));
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
