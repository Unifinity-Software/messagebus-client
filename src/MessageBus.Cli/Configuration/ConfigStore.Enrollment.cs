using System.Text.Json;
using MessageBus.Cli.Security;
using MessageBus.Cli.Server;

namespace MessageBus.Cli.Configuration;

public sealed partial class ConfigStore
{
    private string EnrollmentTransitionPath => Path.Combine(Directory, "identity-publication.pending");

    // A key generation is a local opaque filename component, never a caller supplied path.
    private static void ValidateKeyGeneration(string? generation)
    {
        if (generation is not null && (generation.Length != 32 || generation.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw new ServerBindingException("Invalid machine key generation; restore the profile.");
    }

    private FileKeyVault GenerationFile(string? generation) => new(Path.Combine(Directory,
        generation is null ? CliConfig.MachineKeyFile : $"machine-key.{generation}.pem"), WriteOwnerOnly);

    private IMachineKeyVault GenerationVault(string generation, bool file)
    {
        if (file) return GenerationFile(generation);
        if (_osVault is null && OperatingSystem.IsWindows())
            return new DpapiKeyVault(Path.Combine(Directory, $"machine-key.{generation}.dpapi"), WriteOwnerOnly);
        throw new ServerBindingException("This key store cannot stage an identity replacement atomically. Use a separate private MBUS_HOME with MBUS_KEY_STORE=file for new enrollment.");
    }

    private void SelectKeyGeneration(CliConfig config)
    {
        ValidateKeyGeneration(config.MachineKeyGeneration);
        if (_keyGeneration == config.MachineKeyGeneration) return;
        var file = string.Equals(config.KeyStore ?? _initialKeyStore, "file", StringComparison.OrdinalIgnoreCase);
        _keyFile = GenerationFile(config.MachineKeyGeneration);
        KeyVault = config.MachineKeyGeneration is { } generation ? file ? _keyFile : GenerationVault(generation, false)
            : file ? _keyFile : PlatformVault() ?? _keyFile;
        _keyGeneration = config.MachineKeyGeneration;
    }

    public sealed partial class ProfileSecurityTransaction
    {
        /// <summary>Reads the current key without migration, fallback writes or ordinary config operations.</summary>
        public string? LoadEnrollmentKeyPem() => Execute(() =>
        {
            if (_store._environment is not null && RunContext.IsInsideRun(_store._environment))
                throw new CliException(RunContext.KeyRefused);
            var config = _store.ReadBindingState().Config;
            _store.SelectKeyGeneration(config);
            var pem = _store.KeyVault is IKeychainVault keychain && _store.RunnerKeyReads
                ? keychain.LoadWithoutPrompts() : _store.KeyVault.Load();
            // Old profiles may retain their actual registered key in the file. Never migrate it here.
            if (pem is null) pem = _store._keyFile.Load();
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
            if (File.Exists(Path.Combine(_store.Directory, "trusted-keys.json"))
                && !File.Exists(Path.Combine(_store.Directory, MessageBus.Mcp.KeyTrust.FileName)))
                throw new ServerBindingException("Bind legacy key trust at its original server before replacing this identity.");
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
            // Trust import is a separate publication. Refuse instead of changing it during a failed identity commit.
            if (File.Exists(Path.Combine(_store.Directory, "trusted-keys.json"))
                && !File.Exists(Path.Combine(_store.Directory, MessageBus.Mcp.KeyTrust.FileName)))
                throw new ServerBindingException("Legacy key trust must be bound at its original server before replacing this identity.");
            config = config with { ServerBinding = MergeSnapshot(state.Config.ServerBinding, config.ServerBinding, config.Server, state.Ledger) };
            using var key = MachineKey.FromPem(pem);
            if (config.KeyFingerprint != key.Fingerprint) throw new ServerBindingException("Enrollment key fingerprint mismatch.");
            _store.SelectKeyGeneration(state.Config);
            _store.EnsureDirectory();
            DurableProfileFile.ValidatePrivateDirectory(_store.Directory);
            var generation = Guid.NewGuid().ToString("N");
            var file = ReferenceEquals(_store.KeyVault, _store._keyFile);
            var staged = _store.GenerationVault(generation, file);
            config = config with { MachineKeyGeneration = generation, KeyStore = file ? "file" : "keychain" };
            var json = JsonSerializer.Serialize(config, CliJsonContext.Default.CliConfig);
            var published = false;
            try
            {
                _store.BindingWriteBoundary?.Invoke("before-enrollment-key-save");
                staged.Save(pem);
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
                    && _store.ReadConfig().MachineKeyGeneration != generation) staged.Delete();
            }
        });
    }
}
