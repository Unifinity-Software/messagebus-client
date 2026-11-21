using System.Text;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Security;
using MessageBus.Cli.Server;
using MessageBus.Cli.Tests.Infrastructure;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class KeychainEnrollmentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const string Server = "https://fixture.example.test";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Fresh_or_legacy_keychain_enrollment_selects_independent_item_after_restart(bool appOnly, bool legacy)
    {
        using var machine = new TestMachine();
        var backend = new KeychainFixture();
        IMachineKeyVault Vault() => backend.Vault(machine.MbusFolder, appOnly);
        var store = new ConfigStore(machine.MbusFolder, osVault: Vault());
        using var oldKey = MachineKey.Create();
        using var key = MachineKey.Create();
        if (legacy)
        {
            store.Save(new CliConfig { Server = Server, MachineId = "old", KeyFingerprint = oldKey.Fingerprint });
            store.SaveKeyPem(oldKey.ExportPrivateKeyPem());
        }
        await store.WithProfileSecurityLockAsync(tx =>
        {
            tx.DemandStagedEnrollment();
            var current = tx.Load();
            tx.CommitEnrollment(current, current with { Server = Server, MachineId = "new", KeyFingerprint = key.Fingerprint }, key.ExportPrivateKeyPem(), Ct);
            return Task.FromResult(true);
        }, Ct);
        var restarted = new ConfigStore(store.Directory, osVault: Vault());
        Assert.NotNull(restarted.Load().MachineKeyGeneration);
        Assert.Equal(key.ExportPrivateKeyPem(), restarted.LoadKeyPem());
        if (legacy) Assert.Equal(oldKey.ExportPrivateKeyPem(), Vault().Load());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_keychain_replacement_preserves_legacy_item_and_active_selector(bool appOnly)
    {
        using var machine = new TestMachine();
        var backend = new KeychainFixture();
        var store = new ConfigStore(machine.MbusFolder, osVault: backend.Vault(machine.MbusFolder, appOnly));
        using var oldKey = MachineKey.Create();
        using var newKey = MachineKey.Create();
        store.Save(new CliConfig { Server = Server, MachineId = "old", KeyFingerprint = oldKey.Fingerprint });
        store.SaveKeyPem(oldKey.ExportPrivateKeyPem());
        var before = File.ReadAllBytes(store.ConfigPath);
        store.BindingWriteBoundary = boundary => { if (boundary == "after-enrollment-config-publish") throw new IOException("fixture"); };
        await Assert.ThrowsAsync<IOException>(() => store.WithProfileSecurityLockAsync(tx =>
        {
            var current = tx.Load();
            tx.CommitEnrollment(current, current with { KeyFingerprint = newKey.Fingerprint }, newKey.ExportPrivateKeyPem(), Ct);
            return Task.FromResult(true);
        }, Ct));
        Assert.Equal(before, File.ReadAllBytes(store.ConfigPath));
        var restarted = new ConfigStore(store.Directory, osVault: backend.Vault(machine.MbusFolder, appOnly));
        Assert.Equal(oldKey.ExportPrivateKeyPem(), restarted.LoadKeyPem());
        Assert.Single(backend.Secrets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task File_identity_moves_to_its_keychain_item_and_back_across_restart(bool generated)
    {
        using var machine = new TestMachine();
        var backend = new KeychainFixture();
        IMachineKeyVault Vault() => backend.Vault(machine.MbusFolder, true);
        var store = new ConfigStore(machine.MbusFolder, "file", osVault: Vault());
        using var key = MachineKey.Create();
        if (generated) await store.WithProfileSecurityLockAsync(tx =>
        {
            tx.CommitEnrollment(tx.Load(), new CliConfig { Server = Server, MachineId = "new", KeyFingerprint = key.Fingerprint }, key.ExportPrivateKeyPem(), Ct);
            return Task.FromResult(true);
        }, Ct);
        else
        {
            store.Save(new CliConfig { Server = Server, MachineId = "old", KeyFingerprint = key.Fingerprint });
            store.SaveKeyPem(key.ExportPrivateKeyPem());
        }
        var generation = store.Load().MachineKeyGeneration;
        var file = store.KeyPath;
        store.PrepareKeyStoreChange("keychain").Commit();
        Assert.False(File.Exists(file));
        var restarted = new ConfigStore(store.Directory, osVault: Vault());
        Assert.Equal(key.ExportPrivateKeyPem(), restarted.LoadKeyPem());
        if (generated) Assert.Equal(generation, restarted.Load().MachineKeyGeneration);
        else Assert.NotNull(restarted.Load().MachineKeyGeneration);
        restarted.PrepareKeyStoreChange("file").Commit();
        Assert.Empty(backend.Secrets);
        Assert.Equal(key.ExportPrivateKeyPem(), new ConfigStore(store.Directory, osVault: Vault()).LoadKeyPem());
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("locked")]
    [InlineData("refused")]
    public async Task Matching_leftover_file_recovers_without_reading_or_writing_os_vault(string mode)
    {
        using var machine = new TestMachine();
        using var key = MachineKey.Create();
        using var stale = MachineKey.Create();
        var store = new ConfigStore(machine.MbusFolder, osVault: new RefusingVault(mode, stale.ExportPrivateKeyPem())) { RunnerKeyReads = true };
        store.Save(new CliConfig { Server = Server, MachineId = "old", KeyFingerprint = key.Fingerprint });
        File.WriteAllText(store.KeyPath, key.ExportPrivateKeyPem());
        var before = File.ReadAllBytes(store.ConfigPath);
        await store.WithProfileSecurityLockAsync(tx =>
        {
            Assert.Equal(key.ExportPrivateKeyPem(), tx.LoadEnrollmentKeyPem());
            return Task.FromResult(true);
        }, Ct);
        Assert.Equal(before, File.ReadAllBytes(store.ConfigPath));
        Assert.True(File.Exists(store.KeyPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrong_or_missing_file_never_substitutes_for_registered_key(bool wrongFile)
    {
        using var machine = new TestMachine();
        using var key = MachineKey.Create();
        using var wrong = MachineKey.Create();
        var store = new ConfigStore(machine.MbusFolder, osVault: new RefusingVault("locked", null));
        store.Save(new CliConfig { Server = Server, KeyFingerprint = key.Fingerprint });
        if (wrongFile) File.WriteAllText(store.KeyPath, wrong.ExportPrivateKeyPem());
        await Assert.ThrowsAsync<MachineKeyUnavailableException>(() => store.WithProfileSecurityLockAsync(tx =>
        {
            tx.LoadEnrollmentKeyPem();
            return Task.FromResult(true);
        }, Ct));
    }

    [Fact]
    public async Task Locked_native_keychain_staging_leaves_active_file_usable()
    {
        using var machine = new TestMachine();
        using var key = MachineKey.Create();
        var backend = new KeychainFixture();
        var store = new ConfigStore(machine.MbusFolder, "file", osVault: backend.Vault(machine.MbusFolder, true));
        store.Save(new CliConfig { Server = Server, KeyFingerprint = key.Fingerprint });
        store.SaveKeyPem(key.ExportPrivateKeyPem());
        backend.Locked = true;
        Assert.Throws<CliException>(() => store.PrepareKeyStoreChange("keychain"));
        Assert.Equal(key.ExportPrivateKeyPem(), store.LoadKeyPem());
        Assert.Empty(backend.Secrets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_trust_import_uses_source_origin_and_rolls_back_with_failed_publication(bool fail)
    {
        using var machine = new TestMachine();
        var store = new ConfigStore(machine.MbusFolder, "file");
        using var oldKey = MachineKey.Create();
        using var newKey = MachineKey.Create();
        store.Save(new CliConfig { Server = Server, MachineId = "old", KeyFingerprint = oldKey.Fingerprint });
        store.SaveKeyPem(oldKey.ExportPrivateKeyPem());
        var legacyPath = Path.Combine(store.Directory, "trusted-keys.json");
        const string legacy = """{"workspaces":{"fixture-workspace":{"keys":{},"signers":{}}}}""";
        File.WriteAllText(legacyPath, legacy);
        store.BindingWriteBoundary = boundary => { if (fail && boundary == "after-enrollment-config-publish") throw new IOException("fixture"); };
        async Task Publish() => await store.WithProfileSecurityLockAsync(tx =>
        {
            tx.DemandStagedEnrollment();
            var current = tx.Load();
            tx.CommitEnrollment(current, current with { Server = "https://destination.example.test", KeyFingerprint = newKey.Fingerprint }, newKey.ExportPrivateKeyPem(), Ct);
            return Task.FromResult(true);
        }, Ct);
        if (fail) await Assert.ThrowsAsync<IOException>(Publish);
        else await Publish();
        Assert.Equal(legacy, File.ReadAllText(legacyPath));
        var migrated = Path.Combine(store.Directory, MessageBus.Mcp.KeyTrust.FileName);
        Assert.Equal(!fail, File.Exists(migrated));
        if (!fail)
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(migrated));
            Assert.True(json.RootElement.GetProperty("servers").TryGetProperty(Server, out _));
            Assert.False(json.RootElement.GetProperty("servers").TryGetProperty("https://destination.example.test", out _));
        }
    }

    private sealed class RefusingVault(string mode, string? stale) : IMachineKeyVault
    {
        public string Description => "isolated fixture";
        public string? Load() => mode switch
        {
            "locked" => throw new MachineKeyUnavailableException(),
            "refused" => throw new MachineKeyRefusedException(),
            _ => throw new InvalidOperationException("A matching PEM must bypass even a stale vault: " + (stale is not null)),
        };
        public void Save(string pem) => throw new InvalidOperationException("Recovery must not write.");
        public void Delete() => throw new InvalidOperationException("Recovery must not delete.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_runner_keychain_generation_restarts_without_replacing_active_identity(bool appOnly)
    {
        using var machine = new TestMachine();
        var backend = new KeychainFixture();
        IMachineKeyVault Vault() => backend.Vault(machine.MbusFolder, appOnly);
        var store = new ConfigStore(machine.MbusFolder, osVault: Vault());
        using var oldKey = MachineKey.Create();
        using var newKey = MachineKey.Create();
        store.Save(new CliConfig { Server = Server, MachineId = "old", KeyFingerprint = oldKey.Fingerprint });
        store.SaveKeyPem(oldKey.ExportPrivateKeyPem());
        var binding = new RunnerEnrollmentBinding { SelectedEndpoint = ServerEndpoint.Parse(Server).Value,
            ApiKeyFingerprint = "0123456789abcdef", MachineKeyFingerprint = newKey.Fingerprint };
        var pending = await store.WithProfileSecurityLockAsync(tx => Task.FromResult(
            tx.StageRunnerReplacement(binding, newKey.ExportPrivateKeyPem(), Ct)), Ct);
        var restarted = new ConfigStore(store.Directory, osVault: Vault());
        Assert.Equal("old", restarted.Load().MachineId);
        Assert.Equal(oldKey.ExportPrivateKeyPem(), restarted.LoadKeyPem());
        await restarted.WithProfileSecurityLockAsync(tx =>
        {
            Assert.Equal(newKey.ExportPrivateKeyPem(), tx.LoadRunnerReplacementKey(pending));
            var current = tx.Load();
            tx.CommitEnrollment(current, current with { MachineId = "new", KeyFingerprint = newKey.Fingerprint,
                PendingRunnerIdentity = null, RunnerEnrollmentBinding = binding, EnrolledWithApiKey = binding.ApiKeyFingerprint },
                newKey.ExportPrivateKeyPem(), Ct);
            return Task.FromResult(true);
        }, Ct);
        var completed = new ConfigStore(store.Directory, osVault: Vault());
        Assert.Equal(pending.KeyGeneration, completed.Load().MachineKeyGeneration);
        Assert.Equal(newKey.ExportPrivateKeyPem(), completed.LoadKeyPem());
        Assert.Equal(oldKey.ExportPrivateKeyPem(), Vault().Load());
    }

    // The real Keychain providers run against fake process/framework boundaries, never the user's Keychain.
    private sealed class KeychainFixture : IKeychainTool, IKeychainItems
    {
        public Dictionary<string, byte[]> Secrets { get; } = new(StringComparer.Ordinal);
        public bool Locked { get; set; }
        public IMachineKeyVault Vault(string directory, bool appOnly)
        {
            var tool = new KeychainKeyVault(directory, this, this);
            return appOnly ? new AppOnlyKeychainKeyVault(directory, tool) : tool;
        }
        public (int Exit, string Output) Run(IReadOnlyList<string> args, string? input)
        {
            if (Locked) return (36, "locked fixture");
            if (args[0] == "-i")
            {
                var parts = input!.Trim().Split(' ');
                Secrets[parts[Array.IndexOf(parts, "-a") + 1]] = Encoding.UTF8.GetBytes(parts[Array.IndexOf(parts, "-w") + 1]);
                return (0, "");
            }
            var account = args[args.ToList().IndexOf("-a") + 1];
            if (args[0] == "delete-generic-password") return (Secrets.Remove(account) ? 0 : 44, "");
            return Secrets.TryGetValue(account, out var value) ? (0, Encoding.UTF8.GetString(value)) : (44, "");
        }
        public int FindPassword(byte[] service, byte[] account, out byte[]? secret)
        {
            secret = null;
            if (Locked) return -25308;
            return Secrets.TryGetValue(Encoding.UTF8.GetString(account), out secret) ? KeychainStatus.Success : KeychainStatus.ItemNotFound;
        }
        public bool Exists(byte[] service, byte[] account) => Secrets.ContainsKey(Encoding.UTF8.GetString(account));
        public KeychainLockState LockState(byte[] service, byte[] account) => Locked ? KeychainLockState.Locked : KeychainLockState.Unlocked;
        public int Add(byte[] service, byte[] account, byte[] secret)
        {
            if (Locked) return -25308;
            return Secrets.TryAdd(Encoding.UTF8.GetString(account), secret) ? 0 : -25299;
        }
        public int Delete(byte[] service, byte[] account)
        {
            if (Locked) return -25308;
            return Secrets.Remove(Encoding.UTF8.GetString(account)) ? 0 : KeychainStatus.ItemNotFound;
        }
    }
}
