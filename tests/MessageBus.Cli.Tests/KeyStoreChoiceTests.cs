using MessageBus.Cli.Configuration;
using MessageBus.Cli.Security;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class KeyStoreChoiceTests
{
    [Fact]
    public void Converts_both_ways_and_persisted_choice_wins_over_environment()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mbus-key-choice-" + Guid.NewGuid().ToString("N"));
        using var key = MachineKey.Create();
        var pem = key.ExportPrivateKeyPem();
        var vault = new MemoryVault { Pem = pem };
        var store = new ConfigStore(folder, osVault: vault);
        store.Save(new CliConfig { KeyFingerprint = key.Fingerprint });
        Change(store, "file");
        Assert.Null(vault.Pem);
        Assert.Equal(pem, File.ReadAllText(store.KeyPath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.KeyPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
        }
        var reopened = new ConfigStore(folder, "os", osVault: vault);
        Assert.Equal("file", reopened.KeyStoreName);
        Assert.Equal(pem, reopened.LoadKeyPem());
        Assert.Null(vault.Pem); // No automatic migration back.
        Change(reopened, "keychain");
        Assert.False(File.Exists(store.KeyPath));
        Assert.Equal(pem, vault.Pem);
        Assert.Equal("keychain", new ConfigStore(folder, "file", osVault: vault).KeyStoreName);
        Change(reopened, "keychain"); // Reinstalling must never delete the current key.
        Assert.Equal(pem, vault.Pem);
        reopened.DeleteKey();
        Assert.Null(vault.Pem);
    }

    [Fact]
    public void Failed_verification_keeps_source_and_choice()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mbus-key-choice-" + Guid.NewGuid().ToString("N"));
        using var key = MachineKey.Create();
        var vault = new MemoryVault { Refuse = true };
        var store = new ConfigStore(folder, "file", osVault: vault);
        store.SaveKeyPem(key.ExportPrivateKeyPem());
        Assert.Throws<CliException>(() => Change(store, "keychain"));
        Assert.Equal(key.ExportPrivateKeyPem(), File.ReadAllText(store.KeyPath));
        Assert.Null(store.Load().KeyStore);
    }

    [Fact]
    public void Locked_security_tool_exit_is_transient_and_does_not_echo_its_output()
    {
        var vault = new KeychainKeyVault(Path.GetTempPath(), new LockedTool());
        var error = Assert.Throws<MachineKeyUnavailableException>(() => vault.Load());
        Assert.DoesNotContain("sensitive output", error.Message);
        Assert.DoesNotContain("login again", error.Message);
    }

    [Fact]
    public void A_leftover_file_with_the_wrong_fingerprint_never_replaces_the_keychain_identity()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mbus-key-choice-" + Guid.NewGuid().ToString("N"));
        using var registered = MachineKey.Create();
        using var other = MachineKey.Create();
        var vault = new MemoryVault { Pem = registered.ExportPrivateKeyPem() };
        var store = new ConfigStore(folder, osVault: vault);
        store.Save(new CliConfig { KeyStore = "keychain", KeyFingerprint = registered.Fingerprint });
        File.WriteAllText(store.KeyPath, other.ExportPrivateKeyPem());
        Assert.Equal(registered.ExportPrivateKeyPem(), store.LoadKeyPem());
        store.RunnerKeyReads = true;
        Assert.Equal(registered.ExportPrivateKeyPem(), store.LoadKeyPem());
        Assert.Equal(registered.ExportPrivateKeyPem(), vault.Pem);
        store.RunnerKeyReads = false;
        store.Save(new CliConfig { KeyStore = "keychain" });
        Assert.Equal(registered.ExportPrivateKeyPem(), store.LoadKeyPem());
        Assert.Equal(registered.ExportPrivateKeyPem(), vault.Pem);
        store.Save(new CliConfig { KeyStore = "keychain", KeyFingerprint = registered.Fingerprint });
        File.WriteAllText(store.KeyPath, "invalid PEM");
        Assert.Equal(registered.ExportPrivateKeyPem(), store.LoadKeyPem());
    }

    [Fact]
    public void Persisted_choice_does_not_read_an_unavailable_key()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mbus-key-choice-" + Guid.NewGuid().ToString("N"));
        var store = new ConfigStore(folder, osVault: new UnavailableVault());
        store.Save(new CliConfig { KeyStore = "keychain", MachineId = "existing" });
        Change(store, "keychain");
    }

    [Fact]
    public void Verification_failure_removes_the_new_target_and_preserves_the_source()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mbus-key-choice-" + Guid.NewGuid().ToString("N"));
        using var source = MachineKey.Create();
        using var wrong = MachineKey.Create();
        var vault = new MemoryVault { Replacement = wrong.ExportPrivateKeyPem() };
        var store = new ConfigStore(folder, "file", osVault: vault);
        store.SaveKeyPem(source.ExportPrivateKeyPem());
        Assert.Throws<CliException>(() => Change(store, "keychain"));
        Assert.Null(vault.Pem);
        Assert.Equal(source.ExportPrivateKeyPem(), store.LoadKeyPem());
        Assert.Null(store.Load().KeyStore);
    }

    [Fact]
    public void Change_store_inside_a_run_refuses_before_checking_for_an_identity()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mbus-key-choice-" + Guid.NewGuid().ToString("N"));
        var store = new ConfigStore(folder, "file", name => name == MessageBus.Cli.Runner.RunToolsVariables.File ? "run-tools.json" : null);
        Assert.Throws<CliException>(() => Change(store, "file"));
        Assert.False(File.Exists(store.ConfigPath));
    }

    [Fact]
    public void Moving_to_the_file_with_a_locked_keychain_uses_the_file_when_it_holds_this_machines_key()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mbus-key-choice-" + Guid.NewGuid().ToString("N"));
        using var current = MachineKey.Create();
        using var old = MachineKey.Create();
        var vault = new MemoryVault { Pem = old.ExportPrivateKeyPem(), Locked = true };
        var store = new ConfigStore(folder, osVault: vault);
        store.Save(new CliConfig { KeyStore = "keychain", MachineId = "m", KeyFingerprint = current.Fingerprint });
        // Without the file, the key has to come out of the Keychain: say how to unlock it.
        var locked = Assert.Throws<CliException>(() => Change(store, "file"));
        Assert.Contains(ConfigStore.UnlockCommand, locked.Message);
        File.WriteAllText(store.KeyPath, current.ExportPrivateKeyPem());
        var crashed = store.KeyPath + ".0123456789abcdef.tmp"; // a write that never finished
        File.WriteAllText(crashed, current.ExportPrivateKeyPem());
        Change(store, "file");
        Assert.Equal("file", store.Load().KeyStore);
        Assert.Equal(current.ExportPrivateKeyPem(), File.ReadAllText(store.KeyPath));
        Assert.False(File.Exists(crashed));
    }

    [Fact]
    public void An_abandoned_change_leaves_the_key_where_the_running_runner_reads_it()
    {
        var folder = Path.Combine(Path.GetTempPath(), "mbus-key-choice-" + Guid.NewGuid().ToString("N"));
        using var key = MachineKey.Create();
        var vault = new MemoryVault { Pem = key.ExportPrivateKeyPem() };
        var store = new ConfigStore(folder, osVault: vault);
        store.Save(new CliConfig { MachineId = "m", KeyFingerprint = key.Fingerprint });
        store.PrepareKeyStoreChange("file").Abandon(); // the runner wouldn't stop
        Assert.Equal(key.ExportPrivateKeyPem(), vault.Pem);
        Assert.False(File.Exists(store.KeyPath));
        Assert.Null(store.Load().KeyStore);
        Assert.Equal("keychain", store.KeyStoreName);
    }

    private static void Change(ConfigStore store, string choice) => store.PrepareKeyStoreChange(choice).Commit();

    private sealed class UnavailableVault : IMachineKeyVault
    {
        public string Description => "unavailable";
        public string? Load() => throw new InvalidOperationException("Must not load for an unchanged persisted choice.");
        public void Save(string pem) => throw new InvalidOperationException();
        public void Delete() => throw new InvalidOperationException();
    }

    private sealed class LockedTool : IKeychainTool
    {
        public (int Exit, string Output) Run(IReadOnlyList<string> args, string? input) => (36, "sensitive output");
    }

    private sealed class MemoryVault : IMachineKeyVault
    {
        public string? Pem { get; set; }
        public bool Refuse { get; init; }
        public string? Replacement { get; init; }
        public bool Locked { get; init; }
        public string Description => "fake Keychain";
        public string? Load() => Locked ? throw new MachineKeyUnavailableException() : Pem;
        public void Save(string pem) { if (!Refuse) Pem = Replacement ?? pem; }
        public void Delete() => Pem = null;
    }
}
