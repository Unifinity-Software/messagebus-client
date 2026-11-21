using System.Text.Json;
using MessageBus.Cli.Security;
using MessageBus.Cli.Server;

namespace MessageBus.Cli.Configuration;

public sealed partial class ConfigStore
{
    private string EnrollmentTransitionPath => Path.Combine(Directory, "identity-publication.pending");

    private void ValidateEnrollmentTrust(CliConfig config)
    {
        // KeyTrust.Load can import legacy trust and write it. Validate the existing bytes instead;
        // migration still belongs to the old origin when the account/server is saved successfully.
        var path = Path.Combine(Directory, MessageBus.Mcp.KeyTrust.FileName);
        var json = ReadOptionalProfileFile(path);
        if (json is null) json = ReadOptionalProfileFile(path = Path.Combine(Directory, "trusted-keys.json"));
        if (json is null) return;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            TrustObject(root, ["servers", "workspaces"], requireAll: false);
            var servers = root.TryGetProperty("servers", out var serverMap);
            var legacy = root.TryGetProperty("workspaces", out var workspaces);
            if (!servers && !legacy) throw new JsonException("Missing trusted server or legacy workspace records.");
            if (servers)
            {
                TrustObject(serverMap);
                foreach (var server in serverMap.EnumerateObject()) ValidateTrustWorkspaces(server.Value);
            }
            if (legacy) ValidateTrustWorkspaces(workspaces);
            if (legacy && workspaces.EnumerateObject().Any() && config.Server is null)
                throw new JsonException("Legacy trust has no saved source server.");
            // Interpret legacy workspace-only pins only at the saved origin, never the selected target.
            if (config.Server is { } previous) _ = MessageBus.Mcp.KeyTrust.NormalizeServer(previous);
        }
        catch (JsonException ex)
        {
            throw new ServerBindingException($"{path} is damaged ({ex.Message}). Repair the trust record before signing in.", ex);
        }
    }

    private static void TrustObject(JsonElement value, string[]? fields = null, bool requireAll = true)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Trust records must be non-null objects.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
            if (string.IsNullOrWhiteSpace(property.Name) || !seen.Add(property.Name)
                || fields is not null && !fields.Contains(property.Name, StringComparer.Ordinal))
                throw new JsonException("Duplicate, unknown or ambiguously cased trust field.");
        if (fields is not null && requireAll && seen.Count != fields.Length)
            throw new JsonException("Incomplete trust record.");
    }

    private static void ValidateTrustWorkspaces(JsonElement workspaces)
    {
        TrustObject(workspaces);
        foreach (var workspace in workspaces.EnumerateObject())
        {
            TrustObject(workspace.Value, ["keys", "signers"]);
            var keys = workspace.Value.GetProperty("keys");
            TrustObject(keys);
            foreach (var owner in keys.EnumerateObject())
            {
                if (owner.Value.ValueKind != JsonValueKind.Array) throw new JsonException("Trusted keys must be a non-null array.");
                var versions = new HashSet<int>();
                foreach (var key in owner.Value.EnumerateArray())
                {
                    TrustObject(key, ["version", "keyId", "fingerprint"]);
                    var version = key.GetProperty("version");
                    if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number)
                        || number < 1 || !versions.Add(number)) throw new JsonException("Invalid or duplicate trusted key version.");
                    TrustText(key, "keyId");
                    TrustText(key, "fingerprint");
                }
            }
            var signers = workspace.Value.GetProperty("signers");
            TrustObject(signers);
            foreach (var signer in signers.EnumerateObject())
            {
                TrustObject(signer.Value, ["keyId", "fingerprint"]);
                TrustText(signer.Value, "keyId");
                TrustText(signer.Value, "fingerprint");
            }
        }
    }

    private static void TrustText(JsonElement record, string field)
    {
        var value = record.GetProperty(field);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new JsonException("Trusted key identifiers and fingerprints must be non-empty strings.");
    }

    // A key generation is a local opaque filename component, never a caller supplied path.
    private static void ValidateKeyGeneration(string? generation)
    {
        if (generation is not null && (generation.Length != 32 || generation.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw new ServerBindingException("Invalid machine key generation; restore the profile.");
    }

    private FileKeyVault GenerationFile(string? generation) => new(Path.Combine(Directory,
        generation is null ? CliConfig.MachineKeyFile : $"machine-key.{generation}.pem"), WriteOwnerOnly);

    private string? EnrollmentTrustMigration(CliConfig source)
    {
        if (ReadOptionalProfileFile(Path.Combine(Directory, MessageBus.Mcp.KeyTrust.FileName)) is not null) return null;
        var json = ReadOptionalProfileFile(Path.Combine(Directory, "trusted-keys.json"));
        if (json is null) return null;
        var trust = JsonSerializer.Deserialize(json, CliJsonContext.Default.TrustFile)!;
        if (trust.Workspaces is { Count: > 0 } legacy)
        {
            var origin = MessageBus.Mcp.KeyTrust.NormalizeServer(source.Server!);
            if (!trust.Servers.TryGetValue(origin, out var workspaces)) trust.Servers[origin] = workspaces = new(StringComparer.Ordinal);
            foreach (var (workspace, pins) in legacy) workspaces.TryAdd(workspace, pins);
        }
        trust.Workspaces = null;
        return JsonSerializer.Serialize(trust, CliJsonContext.Default.TrustFile);
    }

    private IMachineKeyVault GenerationVault(string generation, bool file)
    {
        if (file) return GenerationFile(generation);
        if (PlatformVault() is IGenerationMachineKeyVault generations) return generations.ForGeneration(generation);
        if (_osVault is null && OperatingSystem.IsWindows())
            return new DpapiKeyVault(Path.Combine(Directory, $"machine-key.{generation}.dpapi"), WriteOwnerOnly);
        throw new ServerBindingException("This key store cannot stage an identity replacement atomically. Use a separate private MBUS_HOME with MBUS_KEY_STORE=file for new enrollment.");
    }

    private void SelectKeyGeneration(CliConfig config)
    {
        ValidateKeyGeneration(config.MachineKeyGeneration);
        if (_keySelectionInitialized && _keyGeneration == config.MachineKeyGeneration && _selectedKeyStore == (config.KeyStore ?? _initialKeyStore)) return;
        var file = string.Equals(config.KeyStore ?? _initialKeyStore, "file", StringComparison.OrdinalIgnoreCase);
        _keyFile = GenerationFile(config.MachineKeyGeneration);
        KeyVault = config.MachineKeyGeneration is { } generation ? file ? _keyFile : GenerationVault(generation, false)
            : file ? _keyFile : PlatformVault() ?? _keyFile;
        _keyGeneration = config.MachineKeyGeneration;
        _selectedKeyStore = config.KeyStore ?? _initialKeyStore;
        _keySelectionInitialized = true;
    }

    private void PublishKeyStoreGenerationLocked(CliConfig expected, string choice, string generation)
    {
        var state = ReadBindingState();
        if (state.Config != expected) throw new ServerBindingException("The profile changed during key store migration.");
        ValidateKeyGeneration(generation);
        WriteOwnerOnly(ConfigPath, JsonSerializer.Serialize(state.Config with
            { KeyStore = choice, MachineKeyGeneration = generation }, CliJsonContext.Default.CliConfig));
    }

    public sealed partial class ProfileSecurityTransaction
    {
        /// <summary>Forgets the logged-out identity and both active and staged keys, retaining all server floors.</summary>
        internal void ResetIdentity(CliConfig expected) => Execute(() =>
        {
            var current = _store.ReadBindingState().Config;
            // Remote logout awaits do not own a later enrollment or key-store migration. Refuse
            // before deleting anything if either active or independently staged identity changed.
            if (current.Server != expected.Server || current.MachineId != expected.MachineId
                || current.WorkspaceId != expected.WorkspaceId || current.KeyFingerprint != expected.KeyFingerprint
                || current.MachineKeyGeneration != expected.MachineKeyGeneration || current.KeyStore != expected.KeyStore
                || current.EnrolledAt != expected.EnrolledAt || current.EnrolledWithApiKey != expected.EnrolledWithApiKey
                || current.PendingRunnerEnrollment != expected.PendingRunnerEnrollment
                || current.RunnerEnrollmentBinding != expected.RunnerEnrollmentBinding
                || current.PendingRunnerIdentity != expected.PendingRunnerIdentity)
                throw new ServerBindingException("The profile identity changed during logout; its current identity and keys were preserved. Run mbus logout again.");

            _store.DeleteSessionLocked();
            _store.DeleteKeyLocked();
            if (current.PendingRunnerIdentity is { } pending)
                _store.GenerationVault(pending.KeyGeneration, pending.KeyStore == "file").Delete();
            _store.SaveConfigLocked(current with
            {
                MachineId = null, WorkspaceId = null, WorkspaceName = null, KeyFingerprint = null, EnrolledAt = null,
                EnrolledWithApiKey = null, PendingRunnerEnrollment = null, RunnerEnrollmentBinding = null,
                PendingRunnerIdentity = null,
            });
            return true;
        });

        /// <summary>Stages a runner replacement without changing the active identity or key selector.</summary>
        public PendingRunnerIdentity StageRunnerReplacement(RunnerEnrollmentBinding binding, string pem, CancellationToken ct) => Execute(() =>
        {
            ct.ThrowIfCancellationRequested();
            var current = _store.ReadBindingState().Config;
            _store.ValidateEnrollmentTrust(current);
            if (current.PendingRunnerIdentity is not null) throw new ServerBindingException("A runner replacement is already pending.");
            _store.SelectKeyGeneration(current);
            var pending = new PendingRunnerIdentity { KeyGeneration = Guid.NewGuid().ToString("N"),
                KeyStore = ReferenceEquals(_store.KeyVault, _store._keyFile) ? "file" : "keychain", Binding = binding };
            var staged = _store.GenerationVault(pending.KeyGeneration, pending.KeyStore == "file");
            try
            {
                staged.Save(pem);
                using var verified = MachineKey.FromPem(staged.Load() ?? throw new IOException("The pending runner key could not be read."));
                if (verified.Fingerprint != binding.MachineKeyFingerprint) throw new ServerBindingException("Pending runner key mismatch.");
                ct.ThrowIfCancellationRequested();
                _store.SaveConfigLocked(current with { PendingRunnerIdentity = pending });
                return pending;
            }
            finally
            {
                if (_store.ReadConfig().PendingRunnerIdentity?.KeyGeneration != pending.KeyGeneration) staged.Delete();
            }
        });

        /// <summary>Reads only the independently staged replacement key, without migrating the active key.</summary>
        public string LoadRunnerReplacementKey(PendingRunnerIdentity pending) => Execute(() =>
        {
            if (_store._environment is not null && RunContext.IsInsideRun(_store._environment))
                throw new CliException(RunContext.KeyRefused);
            if (_store.ReadBindingState().Config.PendingRunnerIdentity != pending)
                throw new ServerBindingException("The pending runner changed.");
            var vault = _store.GenerationVault(pending.KeyGeneration, pending.KeyStore == "file");
            var pem = vault is IKeychainVault keychain && _store.RunnerKeyReads ? keychain.LoadWithoutPrompts() : vault.Load();
            using var key = MachineKey.FromPem(pem ?? throw new ServerBindingException("The pending runner key is missing."));
            if (key.Fingerprint != pending.Binding.MachineKeyFingerprint) throw new ServerBindingException("The pending runner key changed.");
            return pem!;
        });

        /// <summary>Reads the current origin's trust before enrollment, without importing or rewriting it.</summary>
        public void DemandEnrollmentTrust() => Execute(() =>
        {
            _store.ValidateEnrollmentTrust(_store.ReadBindingState().Config);
            return true;
        });

        /// <summary>Reads the current key without migration, fallback writes or ordinary config operations.</summary>
        public string? LoadEnrollmentKeyPem() => Execute(() =>
        {
            if (_store._environment is not null && RunContext.IsInsideRun(_store._environment))
                throw new CliException(RunContext.KeyRefused);
            var config = _store.ReadBindingState().Config;
            _store.SelectKeyGeneration(config);
            // Recovery is read-only: a registered matching PEM wins even if the OS vault is stale,
            // locked or refuses access. No migration or Keychain access is needed in this case.
            var filePem = _store._keyFile.Load();
            if (filePem is not null && config.KeyFingerprint is { } expected)
            {
                try
                {
                    using var fileKey = MachineKey.FromPem(filePem);
                    if (fileKey.Fingerprint == expected) return filePem;
                }
                catch (Exception ex) when (ex is CliException or System.Security.Cryptography.CryptographicException) { }
            }
            var pem = _store.KeyVault is IKeychainVault keychain && _store.RunnerKeyReads
                ? keychain.LoadWithoutPrompts() : _store.KeyVault.Load();
            // Old profiles may retain their actual registered key in the file. Never migrate it here.
            if (pem is null) pem = filePem;
            if (pem is not null && config.KeyFingerprint is { } fingerprint)
            {
                using var key = MachineKey.FromPem(pem);
                if (key.Fingerprint != fingerprint) throw new ServerBindingException("The saved machine key does not match its identity.");
            }
            return pem;
        });

        /// <summary>Refuses before enrollment if this store cannot publish a new key without overwriting the current key.</summary>
        public void DemandStagedEnrollment() => Execute(() =>
        {
            var config = _store.ReadBindingState().Config;
            _store.ValidateEnrollmentTrust(config);
            _store.SelectKeyGeneration(config);
            _ = _store.GenerationVault(Guid.NewGuid().ToString("N"), ReferenceEquals(_store.KeyVault, _store._keyFile));
            return true;
        });

        /// <summary>
        /// Stages a separate key and publishes its generation and identity in one config replacement.
        /// The old key is never overwritten. An indeterminate filesystem publication retains both keys and refuses further use.
        /// </summary>
        public CliConfig CommitEnrollment(CliConfig expected, CliConfig config, string pem, CancellationToken ct) => Execute(() =>
        {
            ct.ThrowIfCancellationRequested();
            var state = _store.ReadBindingState();
            if (state.Config != expected) throw new ServerBindingException("The profile changed during enrollment; its identity was not replaced.");
            _store.ValidateEnrollmentTrust(state.Config);
            var trustPath = Path.Combine(_store.Directory, MessageBus.Mcp.KeyTrust.FileName);
            var trustMigration = _store.EnrollmentTrustMigration(state.Config);
            config = config with { ServerBinding = MergeSnapshot(state.Config.ServerBinding, config.ServerBinding, config.Server, state.Ledger) };
            using var key = MachineKey.FromPem(pem);
            if (config.KeyFingerprint != key.Fingerprint) throw new ServerBindingException("Enrollment key fingerprint mismatch.");
            _store.SelectKeyGeneration(state.Config);
            _store.EnsureDirectory();
            DurableProfileFile.ValidatePrivateDirectory(_store.Directory);
            var pendingKey = state.Config.PendingRunnerIdentity;
            var reusePending = pendingKey is not null && config.PendingRunnerIdentity is null
                && config.RunnerEnrollmentBinding == pendingKey.Binding && config.KeyFingerprint == pendingKey.Binding.MachineKeyFingerprint;
            var generation = reusePending ? pendingKey!.KeyGeneration : Guid.NewGuid().ToString("N");
            var file = reusePending ? pendingKey!.KeyStore == "file" : ReferenceEquals(_store.KeyVault, _store._keyFile);
            var staged = _store.GenerationVault(generation, file);
            config = config with { MachineKeyGeneration = generation, KeyStore = file ? "file" : "keychain" };
            var json = JsonSerializer.Serialize(config, CliJsonContext.Default.CliConfig);
            var published = false;
            try
            {
                _store.BindingWriteBoundary?.Invoke("before-enrollment-key-save");
                if (!reusePending) staged.Save(pem);
                using var verified = MachineKey.FromPem(staged.Load() ?? throw new IOException("The staged key could not be read."));
                if (verified.Fingerprint != key.Fingerprint) throw new IOException("The staged key did not verify.");
                _store.BindingWriteBoundary?.Invoke("after-enrollment-key-save");
                ct.ThrowIfCancellationRequested();
                _store.BindingWriteBoundary?.Invoke("before-config-save");
                // If replacement/flush fails after publication, the marker preserves a restrictive recovery state.
                var previousJson = JsonSerializer.Serialize(state.Config, CliJsonContext.Default.CliConfig);
                _store.WriteOwnerOnly(_store.EnrollmentTransitionPath, previousJson);
                try
                {
                    _store.BindingWriteBoundary?.Invoke("before-enrollment-config-publish");
                    if (trustMigration is not null) _store.WriteOwnerOnly(trustPath, trustMigration);
                    _store.WriteOwnerOnly(_store.ConfigPath, json);
                    _store.BindingWriteBoundary?.Invoke("after-enrollment-config-publish");
                    File.Delete(_store.EnrollmentTransitionPath);
                    DurableProfileFile.FlushDirectory(_store.Directory);
                }
                catch
                {
                    // Restore the previous config while its old key is still intact. A failed restoration keeps the marker.
                    _store.WriteOwnerOnly(_store.EnrollmentTransitionPath, previousJson);
                    _store.WriteOwnerOnly(_store.ConfigPath, previousJson);
                    if (trustMigration is not null) File.Delete(trustPath);
                    File.Delete(_store.EnrollmentTransitionPath);
                    DurableProfileFile.FlushDirectory(_store.Directory);
                    throw;
                }
                published = true;
                _store.SelectKeyGeneration(config);
                if (state.Config.Server is { } previous && config.Server is { } selected
                    && ServerEndpoint.Parse(previous) != ServerEndpoint.Parse(selected))
                    Update.RunnerUpdateRecord.In(_store.Directory).Clear();
                return config;
            }
            finally
            {
                // Never delete a possibly published key, even if the metadata flush outcome is unknown.
                if (!published && !File.Exists(_store.EnrollmentTransitionPath)
                    && _store.ReadConfig().MachineKeyGeneration != generation
                    && _store.ReadConfig().PendingRunnerIdentity?.KeyGeneration != generation) staged.Delete();
            }
        });
    }
}
