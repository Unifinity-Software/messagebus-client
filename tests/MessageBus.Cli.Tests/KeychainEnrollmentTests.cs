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

    public static IEnumerable<object[]> PreparedChanges()
    {
        foreach (var appOnly in new[] { false, true })
        foreach (var source in new[] { "file", "keychain" })
        foreach (var generated in new[] { false, true })
        foreach (var abandon in new[] { false, true })
            yield return [appOnly, source, generated, abandon];
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_reset_deletes_active_and_pending_native_generations_without_selecting_replacement(bool appOnly)
    {
        using var machine = new TestMachine();
        var backend = new KeychainFixture();
        IMachineKeyVault Vault() => backend.Vault(machine.MbusFolder, appOnly);
        var store = new ConfigStore(machine.MbusFolder, osVault: Vault());
        using var active = MachineKey.Create();
        using var replacement = MachineKey.Create();
        await Enroll(store, active, "active");
        var pending = await store.WithProfileSecurityLockAsync(tx => Task.FromResult(tx.StageRunnerReplacement(
            new RunnerEnrollmentBinding { SelectedEndpoint = ServerEndpoint.Parse(Server).Value,
                ApiKeyFingerprint = "0123456789abcdef", MachineKeyFingerprint = replacement.Fingerprint },
            replacement.ExportPrivateKeyPem(), Ct)), Ct);
        var before = store.Load();
        Assert.Equal(2, backend.Secrets.Count);
        await store.WithProfileSecurityLockAsync(tx => { tx.ResetIdentity(before); return Task.FromResult(true); }, Ct);
        var restarted = new ConfigStore(store.Directory, osVault: Vault());
        Assert.Null(restarted.Load().MachineId);
        Assert.Null(restarted.Load().PendingRunnerIdentity);
        Assert.Equal(before.MachineKeyGeneration, restarted.Load().MachineKeyGeneration);
        Assert.NotEqual(pending.KeyGeneration, restarted.Load().MachineKeyGeneration);
        Assert.Equal("keychain", restarted.KeyStoreName);
        Assert.False(restarted.HasKey);
        Assert.Null(restarted.LoadKeyPem());
        Assert.Empty(backend.Secrets);
    }

    [Theory]
    [InlineData("enroll")]
    [InlineData("backend")]
    [InlineData("pending")]
    public async Task Stale_logout_cannot_delete_separate_store_identity_publication(string change)
    {
        using var machine = new TestMachine();
        var backend = new KeychainFixture();
        IMachineKeyVault Vault() => backend.Vault(machine.MbusFolder, true);
        var store = new ConfigStore(machine.MbusFolder, "file", osVault: Vault());
        using var active = MachineKey.Create();
        using var replacement = MachineKey.Create();
        await Enroll(store, active, "active");
        var captured = store.Load();
        var competitor = new ConfigStore(machine.MbusFolder, osVault: Vault());
        if (change == "enroll") await Enroll(competitor, replacement, "replacement");
        else if (change == "backend") competitor.PrepareKeyStoreChange("keychain").Commit();
        else await competitor.WithProfileSecurityLockAsync(tx => Task.FromResult(tx.StageRunnerReplacement(
            new RunnerEnrollmentBinding { SelectedEndpoint = ServerEndpoint.Parse(Server).Value,
                ApiKeyFingerprint = "0123456789abcdef", MachineKeyFingerprint = replacement.Fingerprint },
            replacement.ExportPrivateKeyPem(), Ct)), Ct);
        var published = File.ReadAllBytes(competitor.ConfigPath);
        var secrets = backend.Secrets.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        await Assert.ThrowsAsync<ServerBindingException>(() => store.WithProfileSecurityLockAsync(tx =>
        {
            tx.ResetIdentity(captured);
            return Task.FromResult(true);
        }, Ct));
        Assert.Equal(published, File.ReadAllBytes(competitor.ConfigPath));
        var restarted = new ConfigStore(machine.MbusFolder, osVault: Vault());
        Assert.Equal(change == "enroll" ? replacement.ExportPrivateKeyPem() : active.ExportPrivateKeyPem(), restarted.LoadKeyPem());
        Assert.Equal(secrets.Keys.Order(), backend.Secrets.Keys.Order());
        foreach (var (account, bytes) in secrets) Assert.Equal(bytes, backend.Secrets[account]);
        if (change == "pending") await restarted.WithProfileSecurityLockAsync(tx =>
        {
            Assert.Equal(replacement.ExportPrivateKeyPem(), tx.LoadRunnerReplacementKey(tx.Load().PendingRunnerIdentity!));
            return Task.FromResult(true);
        }, Ct);
    }

    [Theory]
    [MemberData(nameof(PreparedChanges))]
    public async Task Prepared_A_cannot_publish_or_delete_after_separate_store_enrolls_B(
        bool appOnly, string source, bool generated, bool abandon)
    {
        using var machine = new TestMachine();
        var backend = new KeychainFixture();
        IMachineKeyVault Vault() => backend.Vault(machine.MbusFolder, appOnly);
        var a = new ConfigStore(machine.MbusFolder, source, osVault: Vault());
        using var oldKey = MachineKey.Create();
        using var newKey = MachineKey.Create();
        if (generated) await Enroll(a, oldKey, "old");
        else
        {
            a.Save(new CliConfig { Server = Server, MachineId = "old", KeyStore = source, KeyFingerprint = oldKey.Fingerprint });
            a.SaveKeyPem(oldKey.ExportPrivateKeyPem());
        }
        var prepared = a.PrepareKeyStoreChange(source == "file" ? "keychain" : "file");
        var b = new ConfigStore(machine.MbusFolder, osVault: Vault());
        await Enroll(b, newKey, "new");
        var published = File.ReadAllBytes(b.ConfigPath);
        if (abandon) prepared.Abandon();
        else Assert.Throws<ServerBindingException>(() => prepared.Commit());
        Assert.Equal(published, File.ReadAllBytes(b.ConfigPath));
        var restarted = new ConfigStore(machine.MbusFolder, osVault: Vault());
        Assert.Equal("new", restarted.Load().MachineId);
        Assert.Equal(newKey.Fingerprint, restarted.Load().KeyFingerprint);
        Assert.Equal(source, restarted.KeyStoreName);
        Assert.Equal(newKey.ExportPrivateKeyPem(), restarted.LoadKeyPem());
    }

    [Theory]
    [InlineData("file")]
    [InlineData("keychain")]
    public async Task Abandon_does_not_delete_a_target_selected_by_another_prepared_change(string source)
    {
        using var machine = new TestMachine();
        var backend = new KeychainFixture();
        IMachineKeyVault Vault() => backend.Vault(machine.MbusFolder, true);
        var a = new ConfigStore(machine.MbusFolder, source, osVault: Vault());
        using var key = MachineKey.Create();
        await Enroll(a, key, "active");
        var target = source == "file" ? "keychain" : "file";
        var abandoned = a.PrepareKeyStoreChange(target);
        var b = new ConfigStore(machine.MbusFolder, osVault: Vault());
        b.PrepareKeyStoreChange(target).Commit();
        abandoned.Abandon();
        var restarted = new ConfigStore(machine.MbusFolder, osVault: Vault());
        Assert.Equal(target, restarted.KeyStoreName);
        Assert.Equal(key.ExportPrivateKeyPem(), restarted.LoadKeyPem());
    }

    public static IEnumerable<object[]> PublicKeyInterleavings()
    {
        foreach (var operation in new[] { "save", "load-migration", "delete", "has", "save-fallback" })
        foreach (var separateStore in new[] { false, true })
        foreach (var enroll in new[] { false, true })
            // Deleting the only source intentionally prevents a subsequent backend copy.
            if (operation != "delete" || enroll) yield return [operation, separateStore, enroll];
    }

    [Fact]
    public async Task Abandon_keeps_file_that_became_recovery_after_destructive_native_save_failure()
    {
        using var machine = new TestMachine();
        var backend = new KeychainFixture();
        IMachineKeyVault Vault() => backend.Vault(machine.MbusFolder, true);
        var store = new ConfigStore(machine.MbusFolder, osVault: Vault());
        using var key = MachineKey.Create();
        await Enroll(store, key, "active");
        var prepared = store.PrepareKeyStoreChange("file");
        backend.FailNextNativeSave = true; // The actual provider deletes its old item before Add fails.
        store.SaveKeyPem(key.ExportPrivateKeyPem());
        Assert.Empty(backend.Secrets);
        prepared.Abandon();
        Assert.Equal(key.ExportPrivateKeyPem(), File.ReadAllText(store.KeyPath));
        var restarted = new ConfigStore(machine.MbusFolder, osVault: Vault());
        Assert.Equal("active", restarted.Load().MachineId);
        Assert.Equal(key.Fingerprint, restarted.Load().KeyFingerprint);
        Assert.Equal(key.ExportPrivateKeyPem(), restarted.LoadKeyPem());
        Assert.False(File.Exists(restarted.KeyPath)); // Recovery still migrates successfully.
    }

    [Theory]
    [InlineData("file")]
    [InlineData("keychain")]
    public async Task Removed_prepared_copy_cannot_be_published(string source)
    {
        using var machine = new TestMachine();
        var backend = new KeychainFixture();
        IMachineKeyVault Vault() => backend.Vault(machine.MbusFolder, true);
        var store = new ConfigStore(machine.MbusFolder, source, osVault: Vault());
        using var key = MachineKey.Create();
        await Enroll(store, key, "active");
        var before = File.ReadAllBytes(store.ConfigPath);
        var prepared = store.PrepareKeyStoreChange(source == "file" ? "keychain" : "file");
        prepared.Abandon();
        Assert.Throws<ServerBindingException>(() => prepared.Commit());
        Assert.Equal(before, File.ReadAllBytes(store.ConfigPath));
        Assert.Equal(key.ExportPrivateKeyPem(), new ConfigStore(machine.MbusFolder, osVault: Vault()).LoadKeyPem());
    }

    [Theory(Timeout = 30_000)]
    [MemberData(nameof(PublicKeyInterleavings))]
    public async Task Public_key_vault_boundary_holds_profile_lock_until_operation_finishes(
        string operation, bool separateStore, bool enroll)
    {
        using var machine = new TestMachine();
        var backend = new KeychainFixture();
        IMachineKeyVault Vault() => backend.Vault(machine.MbusFolder, false);
        var store = new ConfigStore(machine.MbusFolder, osVault: Vault());
        using var oldKey = MachineKey.Create();
        using var newKey = MachineKey.Create();
        await Enroll(store, oldKey, "old");
        var competitor = separateStore ? new ConfigStore(machine.MbusFolder, osVault: Vault()) : store;
        if (operation == "load-migration")
        {
            ((IGenerationMachineKeyVault)Vault()).ForGeneration(store.Load().MachineKeyGeneration!).Delete();
            File.WriteAllText(store.KeyPath, oldKey.ExportPrivateKeyPem());
        }
        var boundary = operation switch { "delete" => "delete-generic-password", "has" => "exists", _ => "-i" };
        Task? competing = null;
        backend.BeforeOperation = observed =>
        {
            if (observed != boundary) return;
            backend.BeforeOperation = null;
            // Begin synchronously at the actual provider boundary, so the lock wait has been reached
            // before IsCompleted is inspected. No scheduler sleeps or timing assumptions.
            competing = enroll ? Enroll(competitor, newKey, "new")
                : competitor.WithProfileSecurityLockAsync(tx =>
                {
                    var change = tx.PrepareKeyStoreChange("file");
                    tx.CommitKeyStoreChange(change);
                    return Task.FromResult(true);
                }, Ct);
            Assert.False(competing.IsCompleted);
            if (operation == "save-fallback") backend.FailNextSave = true;
        };
        try
        {
            switch (operation)
            {
                case "save": case "save-fallback": store.SaveKeyPem(oldKey.ExportPrivateKeyPem()); break;
                case "load-migration": Assert.Equal(oldKey.ExportPrivateKeyPem(), store.LoadKeyPem()); break;
                case "delete": store.DeleteKey(); break;
                case "has": Assert.True(store.HasKey); break;
            }
        }
        finally { backend.BeforeOperation = null; }
        Assert.NotNull(competing);
        await competing.WaitAsync(TestContext.Current.CancellationToken);
        var restarted = new ConfigStore(machine.MbusFolder, osVault: Vault());
        var expected = enroll ? newKey : oldKey;
        Assert.Equal(enroll ? "new" : "old", restarted.Load().MachineId);
        Assert.Equal(expected.Fingerprint, restarted.Load().KeyFingerprint);
        Assert.Equal(enroll ? "keychain" : "file", restarted.KeyStoreName);
        Assert.Equal(expected.ExportPrivateKeyPem(), restarted.LoadKeyPem());
    }

    [Theory]
    [InlineData("file")]
    [InlineData("keychain")]
    public async Task Earlier_identity_PEM_cannot_overwrite_the_current_enrollment(string source)
    {
        using var machine = new TestMachine();
        var backend = new KeychainFixture();
        IMachineKeyVault Vault() => backend.Vault(machine.MbusFolder, true);
        var store = new ConfigStore(machine.MbusFolder, source, osVault: Vault());
        using var oldKey = MachineKey.Create();
        using var newKey = MachineKey.Create();
        await Enroll(store, oldKey, "old");
        var earlier = store.LoadKeyPem()!;
        await Enroll(new ConfigStore(machine.MbusFolder, osVault: Vault()), newKey, "new");
        Assert.Throws<ServerBindingException>(() => store.SaveKeyPem(earlier));
        Assert.Equal(newKey.ExportPrivateKeyPem(), new ConfigStore(machine.MbusFolder, osVault: Vault()).LoadKeyPem());
    }

    [Fact]
    public async Task Published_callback_routes_key_operations_through_the_same_busy_and_lifetime_guard()
    {
        using var machine = new TestMachine();
        var backend = new KeychainFixture();
        var store = new ConfigStore(machine.MbusFolder, osVault: backend.Vault(machine.MbusFolder, false));
        using var key = MachineKey.Create();
        await Enroll(store, key, "active");
        ConfigStore.ProfileSecurityTransaction? escaped = null;
        backend.BeforeOperation = operation =>
        {
            if (operation != "-i") return;
            backend.BeforeOperation = null;
            Assert.Throws<InvalidOperationException>(() => store.LoadKeyPem());
            Assert.Throws<InvalidOperationException>(() => store.SaveKeyPem(key.ExportPrivateKeyPem()));
            Assert.Throws<InvalidOperationException>(() => store.DeleteKey());
            Assert.Throws<InvalidOperationException>(() => store.HasKey);
            Assert.Throws<InvalidOperationException>(() => store.PrepareKeyStoreChange("file"));
        };
#pragma warning disable CS0618 // Exercise the published callback overload.
        await store.WithSessionLockAsync(() =>
        {
            store.SaveKeyPem(key.ExportPrivateKeyPem());
            Assert.True(store.HasKey);
            Assert.Equal(key.ExportPrivateKeyPem(), store.LoadKeyPem());
            return Task.FromResult(true);
        }, Ct);
#pragma warning restore CS0618
        await store.WithProfileSecurityLockAsync(tx => { escaped = tx; return Task.FromResult(true); }, Ct);
        Assert.Throws<InvalidOperationException>(() => escaped!.LoadKeyPem());
        Assert.Throws<InvalidOperationException>(() => escaped!.SaveKeyPem(key.ExportPrivateKeyPem()));
        Assert.Throws<InvalidOperationException>(() => escaped!.DeleteKey());
        Assert.Throws<InvalidOperationException>(() => escaped!.HasKey());
        Assert.Equal(key.ExportPrivateKeyPem(), store.LoadKeyPem());
    }

    private static Task<CliConfig> Enroll(ConfigStore store, MachineKey key, string machine) => store.WithProfileSecurityLockAsync(tx =>
    {
        var current = tx.Load();
        return Task.FromResult(tx.CommitEnrollment(current, current with
        { Server = Server, MachineId = machine, KeyFingerprint = key.Fingerprint }, key.ExportPrivateKeyPem(), Ct));
    }, Ct);

    // The real Keychain providers run against fake process/framework boundaries, never the user's Keychain.
    private sealed class KeychainFixture : IKeychainTool, IKeychainItems
    {
        public Dictionary<string, byte[]> Secrets { get; } = new(StringComparer.Ordinal);
        public bool Locked { get; set; }
        public Action<string>? BeforeOperation { get; set; }
        public bool FailNextSave { get; set; }
        public bool FailNextNativeSave { get; set; }
        public IMachineKeyVault Vault(string directory, bool appOnly)
        {
            var tool = new KeychainKeyVault(directory, this, this);
            return appOnly ? new AppOnlyKeychainKeyVault(directory, tool) : tool;
        }
        public (int Exit, string Output) Run(IReadOnlyList<string> args, string? input)
        {
            BeforeOperation?.Invoke(args[0]);
            if (Locked) return (36, "locked fixture");
            if (args[0] == "-i")
            {
                if (FailNextSave) { FailNextSave = false; throw new IOException("fixture refused save"); }
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
        public bool Exists(byte[] service, byte[] account)
        {
            BeforeOperation?.Invoke("exists");
            return Secrets.ContainsKey(Encoding.UTF8.GetString(account));
        }
        public KeychainLockState LockState(byte[] service, byte[] account) => Locked ? KeychainLockState.Locked : KeychainLockState.Unlocked;
        public int Add(byte[] service, byte[] account, byte[] secret)
        {
            if (FailNextNativeSave) { FailNextNativeSave = false; return -25308; }
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
