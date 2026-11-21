using System.Text.Json;

namespace MessageBus.Cli.Configuration;

/// <summary>
/// Reads and writes mbus's folder (<c>~/.mbus</c> or <c>MBUS_HOME</c>). The folder is owner-only
/// (<c>700</c>) and each file in it <c>600</c> on macOS and Linux; on Windows the folder sits in the
/// user's profile, whose permissions already keep other users out. Files are replaced in one step,
/// so an interrupted write never leaves half a settings file. The machine's private key goes to the
/// OS's own store where there is one (<see cref="Security.IMachineKeyVault"/>, the original issue).
/// </summary>
public sealed partial class ConfigStore
{
    /// <summary><c>os</c> (the default: DPAPI on Windows, the Keychain on macOS) or <c>file</c> (the owner-only PEM file everywhere).</summary>
    public const string KeyStoreVariable = "MBUS_KEY_STORE";

    private readonly Security.FileKeyVault _keyFile;
    private readonly Security.IMachineKeyVault? _osVault;

    // Runner reads check the actual keychain lock state before using the trusted tool.
    internal bool RunnerKeyReads { get; set; }
    internal Action<string>? KeyReadLog { get; set; }
    private bool _leftoverFileLogged;
    private readonly Func<string, string?>? _environment;

    /// <param name="directory">The folder.</param>
    /// <param name="keyStore">The <see cref="KeyStoreVariable"/> setting.</param>
    /// <param name="environment">
    /// Reads environment variables, to tell whether this mbus runs inside a task's run (the original issue): there it never loads the
    /// machine key. Null (tests that use the folder directly) skips that check.
    /// </param>
    /// <param name="osVault">An OS-store implementation (tests can substitute an isolated fake).</param>
    public ConfigStore(string directory, string? keyStore = null, Func<string, string?>? environment = null, Security.IMachineKeyVault? osVault = null)
    {
        Directory = directory;
        _environment = environment;
        _keyFile = new Security.FileKeyVault(Path.Combine(directory, CliConfig.MachineKeyFile), WriteOwnerOnly);
        _osVault = osVault;
        var choice = Load().KeyStore ?? keyStore;
        KeyVault = string.Equals(choice?.Trim(), "file", StringComparison.OrdinalIgnoreCase) ? _keyFile : PlatformVault() ?? _keyFile;
    }

    /// <summary>The folder.</summary>
    public string Directory { get; }

    /// <summary>Full path of the settings file.</summary>
    public string ConfigPath => Path.Combine(Directory, CliConfig.FileName);

    /// <summary>Full path of the machine key file (PEM; where the key lives without an OS store, and where older mbus kept it).</summary>
    public string KeyPath => _keyFile.Path;

    /// <summary>Where the machine key is kept.</summary>
    public Security.IMachineKeyVault KeyVault { get; private set; }

    /// <summary>The selected backend, also passed to the runner service.</summary>
    public string KeyStoreName => ReferenceEquals(KeyVault, _keyFile) ? "file" : "keychain";

    private Security.IMachineKeyVault? PlatformVault() => _osVault ?? OsKeyVault();

    /// <summary>What to type to unlock the login keychain in a terminal.</summary>
    internal const string UnlockCommand = "security unlock-keychain ~/Library/Keychains/login.keychain-db";

    /// <summary>
    /// Copies this machine's key to <paramref name="choice"/>'s store and checks the copy, without switching to it yet:
    /// <see cref="KeyStoreChange.Commit"/> saves the choice and deletes the old copy, <see cref="KeyStoreChange.Abandon"/>
    /// removes the new one. A runner that is still running reads the old copy until it stops (the original issue).
    /// </summary>
    public KeyStoreChange PrepareKeyStoreChange(string choice)
    {
        if (_environment is not null && Security.RunContext.IsInsideRun(_environment))
            throw new CliException(Security.RunContext.KeyRefused);
        if (choice is not ("file" or "keychain")) throw new CliException("Choose --key-store file or keychain.");
        var target = KeyStoreName == choice ? KeyVault : choice == "file" ? _keyFile : PlatformVault() ?? throw new CliException("The Keychain is unavailable on this computer.");
        var config = Load();
        if (config.KeyStore == choice && KeyStoreName == choice) return new KeyStoreChange(null, choice, target, null);
        if (config.MachineId is null && !HasKey) return new KeyStoreChange(this, choice, target, null);
        // the original issue: the file first when it holds this machine's key, so a locked Keychain (with an older copy, say) doesn't
        // stop a move to the file. The Keychain is read only when the file can't be used.
        var source = KeyVault;
        string? pem;
        if (!ReferenceEquals(source, _keyFile) && _keyFile.Load() is { } fileKey && MatchesStoredFingerprint(fileKey))
        {
            source = _keyFile;
            pem = fileKey;
        }
        else
        {
            pem = LoadForChange(source, choice);
        }
        if (pem is null && !ReferenceEquals(source, _keyFile))
        {
            source = _keyFile;
            pem = source.Load();
        }
        if (pem is null) throw new CliException("This machine has no saved key. Run mbus login first.");
        using var key = Security.MachineKey.FromPem(pem);
        if (config.KeyFingerprint is { } expected && key.Fingerprint != expected)
            throw new CliException("The saved key does not match this machine. The key store was not changed.");
        if (ReferenceEquals(source, target) && choice == "keychain")
        {
            return new KeyStoreChange(this, choice, target, null);
        }
        if (choice == "file" && !OperatingSystem.IsWindows())
        {
            EnsureDirectory();
            File.SetUnixFileMode(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        // The current store's copy is never removed for a failed or abandoned change: it was in use before.
        var newCopy = ReferenceEquals(source, target) || ReferenceEquals(target, KeyVault) ? null : target;
        try
        {
            target.Save(pem);
            using var verified = Security.MachineKey.FromPem(target.Load() ?? throw new CliException("The new key store did not return the key."));
            if (verified.Fingerprint != key.Fingerprint) throw new CliException("The new key store returned a different key.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CliException or System.Security.Cryptography.CryptographicException)
        {
            if (newCopy is not null)
            {
                try { newCopy.Delete(); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or CliException)
                {
                    throw new CliException("The key store was not changed, but the unverified target copy could not be removed.", 1, cleanup);
                }
            }
            // the original issue: an install that can't ask (no terminal) picks the Keychain, which may be locked.
            throw new CliException(choice == "keychain" && ex is IOException
                ? $"Couldn't save this machine's key in the login Keychain, so the key store was not changed: unlock the keychain ({UnlockCommand}) or use --key-store file, then run this again."
                : "The key store was not changed: " + ex.Message, 1, ex);
        }
        // Moving from the Keychain to the file: its copy goes too, also when the file was the source (an older one, say).
        var oldCopies = new[] { source, KeyVault }.Distinct().Where(v => !ReferenceEquals(v, target)).ToArray();
        return new KeyStoreChange(this, choice, target, oldCopies, newCopy);
    }

    /// <summary>Reads the key to move it, saying what to do when the Keychain is locked.</summary>
    private static string? LoadForChange(Security.IMachineKeyVault source, string choice)
    {
        try
        {
            return source.Load();
        }
        catch (Security.MachineKeyUnavailableException ex)
        {
            throw new CliException(choice == "file"
                ? $"The login Keychain is locked: unlock it first ({UnlockCommand}), then run this again."
                : $"The login Keychain is locked: unlock it ({UnlockCommand}) or use --key-store file, then run this again.", 1, ex);
        }
    }

    /// <summary>A key-store change whose copy is made and checked (<see cref="PrepareKeyStoreChange"/>).</summary>
    public sealed class KeyStoreChange
    {
        private readonly ConfigStore? _store;
        private readonly string _choice;
        private readonly Security.IMachineKeyVault _target;
        private readonly Security.IMachineKeyVault[] _oldCopies;
        private readonly Security.IMachineKeyVault? _newCopy;

        internal KeyStoreChange(ConfigStore? store, string choice, Security.IMachineKeyVault target, Security.IMachineKeyVault[]? oldCopies,
            Security.IMachineKeyVault? newCopy = null)
        {
            _store = store;
            _choice = choice;
            _target = target;
            _oldCopies = oldCopies ?? [];
            _newCopy = newCopy;
        }

        /// <summary>Switches to the new store and deletes the old copy; returns a warning when that copy couldn't be deleted.</summary>
        public string? Commit()
        {
            if (_store is null) return null;
            _store.Save(_store.Load() with { KeyStore = _choice });
            _store.KeyVault = _target;
            string? warning = null;
            foreach (var old in _oldCopies)
            {
                // A locked keychain would ask for its password (or keep the tool waiting): its copy stays then.
                if (old is Security.IKeychainVault keychain && keychain.LockState() != Security.KeychainLockState.Unlocked) continue;
                try
                {
                    old.Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CliException)
                {
                    warning = $"The key store is changed, but the old copy of this machine's key couldn't be deleted: {ex.Message}";
                }
            }
            return warning;
        }

        /// <summary>Removes the new copy and leaves everything as it was.</summary>
        public void Abandon()
        {
            try
            {
                _newCopy?.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CliException)
            {
                // A second copy of the same key, in a store that isn't used: harmless, and replaced by the next change.
            }
        }
    }

    /// <summary>Allowance windows observed by runs in this settings scope.</summary>
    public Runner.AgentCapacityStore AgentCapacity => new(Directory, WriteOwnerOnly);

    /// <summary>The agent sign-in variables kept for the runner service (the original issue).</summary>
    public Runner.Service.AgentEnvironmentStore AgentEnvironment => new(Directory, WriteOwnerOnly);

    private Security.IMachineKeyVault? OsKeyVault()
    {
        if (OperatingSystem.IsWindows())
        {
            return new Security.DpapiKeyVault(Path.Combine(Directory, CliConfig.ProtectedMachineKeyFile), WriteOwnerOnly);
        }
        if (OperatingSystem.IsMacOS() && File.Exists(Security.SecurityTool.Path))
        {
            var viaTool = new Security.KeychainKeyVault(Directory, new Security.SecurityTool(), new Security.MacKeychainItems());
            // the original issue: a Developer ID signed mbus keeps the key where only it can read it; ad-hoc builds use Apple's tool.
            return Security.MacCodeSignature.IsDeveloperIdSigned ? new Security.AppOnlyKeychainKeyVault(Directory, viaTool) : viaTool;
        }
        return null; // Linux: the 600 file
    }

    /// <summary>The stored settings, or empty settings when there are none.</summary>
    public CliConfig Load()
        => InSecurityTransaction(context => context.Load());

    private CliConfig ReadConfig()
    {
        var json = ReadOptionalProfileFile(ConfigPath);
        if (json is null)
        {
            return new CliConfig();
        }
        try
        {
            ValidateSavedJson(json);
            return JsonSerializer.Deserialize(json, CliJsonContext.Default.CliConfig) ?? throw new Server.ServerBindingException("Empty settings state.");
        }
        catch (JsonException ex)
        {
            throw new Server.ServerBindingException("Unreadable settings state; restore the profile before using credentials.", ex);
        }
    }

    /// <summary>Saves the settings.</summary>
    public void Save(CliConfig config)
        => InSecurityTransaction(context => { context.Save(config); return true; });

    private void SaveConfigLocked(CliConfig config)
    {
        var state = ReadBindingState();
        config = config with { ServerBinding = MergeSnapshot(state.Config.ServerBinding, config.ServerBinding, config.Server, state.Ledger) };
        BindingWriteBoundary?.Invoke("before-config-save");
        var previousServer = state.Config.Server;
        if (previousServer != config.Server)
            MessageBus.Mcp.KeyTrust.BindLegacy(Directory, previousServer ?? config.Server ?? "");
        // the original issue: every login path saves here, including enrollment with a runner API key.
        if (previousServer is not null && config.Server is not null
            && MessageBus.Mcp.KeyTrust.NormalizeServer(previousServer) != MessageBus.Mcp.KeyTrust.NormalizeServer(config.Server))
            Update.RunnerUpdateRecord.In(Directory).Clear();
        WriteOwnerOnly(ConfigPath, JsonSerializer.Serialize(config, CliJsonContext.Default.CliConfig));
    }

    /// <summary>
    /// Keeps the name the server gave for <paramref name="workspaceId"/> (the original issue), when this machine is still in that
    /// workspace and the name changed (renamed after <c>mbus login</c>), so the header and summaries show it. Never
    /// fails the command: settings that can't be read or written keep the old name.
    /// </summary>
    public void RememberWorkspaceName(string? workspaceId, string? name)
    {
        if (workspaceId is null || string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        try
        {
            var stored = Load();
            if (stored.WorkspaceId == workspaceId && stored.WorkspaceName != name)
            {
                Save(stored with { WorkspaceName = name });
            }
        }
        catch (Exception ex) when (ex is CliException or IOException or UnauthorizedAccessException)
        {
            // The next command that hears from the server tries again.
        }
    }

    /// <summary>
    /// The machine's private key in PEM, or null when this machine isn't enrolled. A key file left by an
    /// older mbus (or by a save the OS store refused) moves into the OS store here, and the file is deleted
    /// once the store gives the same key back; if the store can't take it, the file keeps working.
    /// </summary>
    public string? LoadKeyPem()
    {
        if (_environment is not null && Security.RunContext.IsInsideRun(_environment))
        {
            throw new CliException(Security.RunContext.KeyRefused);
        }
        if (RunnerKeyReads && KeyVault is Security.IKeychainVault keychain)
        {
            // Leave legacy-file migration to an interactive CLI command; no identity changes while waiting.
            if (_keyFile.Load() is { } fileKey && MatchesStoredFingerprint(fileKey))
            {
                // Every request reads the key: say it once (the original issue).
                if (KeyReadLog is { } log && !_leftoverFileLogged)
                {
                    _leftoverFileLogged = true;
                    log("Using the leftover machine key file: its fingerprint matches this machine's saved fingerprint.");
                }
                return fileKey;
            }
            return keychain.LoadWithoutPrompts();
        }
        if (ReferenceEquals(KeyVault, _keyFile) || _keyFile.Load() is not { } legacy)
        {
            return KeyVault.Load();
        }
        if (!MatchesStoredFingerprint(legacy) && Load().KeyFingerprint is not null)
            return KeyVault.Load(); // Never replace the registered identity with a different leftover file.
        if (Load().KeyFingerprint is null)
        {
            // With no registered fingerprint, preserve any identity already in the protected store.
            try
            {
                if (KeyVault.Load() is { } existing) return existing;
            }
            catch (Exception ex) when (ex is IOException or CliException)
            {
                return legacy; // Can't verify the destination; leave both stores alone.
            }
        }
        try
        {
            KeyVault.Save(legacy);
            if (KeyVault.Load() == legacy)
            {
                _keyFile.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or CliException
                                       or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            // Stays in the file this time; the next command tries again.
        }
        return legacy;
    }

    private bool MatchesStoredFingerprint(string pem)
    {
        if (Load().KeyFingerprint is not { } expected) return false;
        try
        {
            using var key = Security.MachineKey.FromPem(pem);
            return key.Fingerprint == expected;
        }
        catch (CliException) { return false; }
    }

    /// <summary>Whether a machine key is saved here (without decrypting it).</summary>
    public bool HasKey => File.Exists(KeyPath) || (!ReferenceEquals(KeyVault, _keyFile) && SafeHasProtectedKey());

    private bool SafeHasProtectedKey()
    {
        if (KeyVault is Security.DpapiKeyVault)
        {
            return File.Exists(Path.Combine(Directory, CliConfig.ProtectedMachineKeyFile));
        }
        if (KeyVault is Security.IKeychainVault keychain)
        {
            return keychain.Exists();
        }
        try
        {
            return (RunnerKeyReads ? LoadKeyPem() : KeyVault.Load()) is not null;
        }
        catch (CliException)
        {
            return true; // there is one; using it will say what's wrong
        }
    }

    /// <summary>Saves the machine's private key in the OS store, or in the owner-only file when the store refuses it.</summary>
    public void SaveKeyPem(string pem)
    {
        if (!ReferenceEquals(KeyVault, _keyFile))
        {
            try
            {
                KeyVault.Save(pem);
                _keyFile.Delete();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException
                                           or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
            {
                // A locked keychain over SSH, say: the file works everywhere, and moves to the store later.
            }
        }
        _keyFile.Save(pem);
    }

    /// <summary>Deletes the machine's private key, wherever it is.</summary>
    public void DeleteKey()
    {
        _keyFile.Delete();
        if (!ReferenceEquals(KeyVault, _keyFile))
        {
            KeyVault.Delete();
        }
    }

    /// <summary>Full path of the person's mbus sign-in (<see cref="CliSessionFile"/>).</summary>
    public string SessionPath => Path.Combine(Directory, CliSessionFile.FileName);

    /// <summary>The saved sign-in, or null when absent. Unreadable state refuses rather than erasing a possible floor.</summary>
    public CliSessionFile? LoadSession()
        => InSecurityTransaction(context => context.LoadSession());

    private CliSessionFile? ReadSession()
    {
        var json = ReadOptionalProfileFile(SessionPath);
        if (json is null)
        {
            return null;
        }
        try
        {
            ValidateSavedJson(json);
            return JsonSerializer.Deserialize(json, CliJsonContext.Default.CliSessionFile)
                ?? throw new Server.ServerBindingException("Empty saved sign-in state.");
        }
        catch (JsonException ex)
        {
            throw new Server.ServerBindingException("Unreadable saved sign-in state; restore the profile before using credentials.", ex);
        }
    }

    /// <summary>Saves the sign-in (owner-only, replaced in one step).</summary>
    public void SaveSession(CliSessionFile session) => InSecurityTransaction(context =>
    { context.SaveSession(session); return true; });

    private void SaveSessionLocked(CliSessionFile session)
    {
        var state = ReadBindingState();
        session = session with { ServerBinding = MergeSnapshot(state.Session?.ServerBinding, session.ServerBinding, session.Server, state.Ledger) };
        WriteOwnerOnly(SessionPath, JsonSerializer.Serialize(session, CliJsonContext.Default.CliSessionFile));
    }

    /// <summary>Forgets the sign-in.</summary>
    public void DeleteSession()
        => InSecurityTransaction(context => { context.DeleteSession(); return true; });

    private void DeleteSessionLocked()
    {
        _ = ReadBindingState(); // A surviving snapshot may never be discarded to repair a missing/corrupt ledger.
        if (File.Exists(SessionPath))
        {
            File.Delete(SessionPath);
            DurableProfileFile.FlushDirectory(Directory);
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> SessionGates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// Runs <paramref name="body"/> while holding the sign-in lock, so two mbus processes (or two
    /// commands in one process) never refresh at once: refresh tokens change on every use, and a
    /// second refresh with the old one would end the sign-in. Inside one process a semaphore queues
    /// the callers; across processes an exclusive lock file does. Whoever waited must read the
    /// sign-in again once inside: another process may have refreshed it meanwhile.
    /// </summary>
    public Task<T> WithSessionLockAsync<T>(Func<ProfileSecurityTransaction, Task<T>> body, CancellationToken ct) =>
        WithProfileSecurityLockAsync(body, ct);

    /// <summary>
    /// Runs a legacy sign-in callback under the profile lock. Ordinary Load/Save and session operations on
    /// this store use the held transaction's operation guard, including after awaits. Overlapping operations
    /// refuse before state access; inherited callbacks cannot use the transaction after its completion.
    /// Use the explicit-context overload for new code and helpers that prepare or consume sends.
    /// </summary>
    [Obsolete("Use the overload receiving ProfileSecurityTransaction for new code.")]
    public Task<T> WithSessionLockAsync<T>(Func<Task<T>> body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        return WithProfileSecurityLockAsync(async context =>
        {
            var previous = _compatibilitySession.Value;
            _compatibilitySession.Value = context;
            try { return await body().ConfigureAwait(false); }
            finally { _compatibilitySession.Value = previous; }
        }, ct);
    }

    /// <summary>Runs a serialized profile transaction. Pass its context to nested helpers.</summary>
    public async Task<T> WithProfileSecurityLockAsync<T>(Func<ProfileSecurityTransaction, Task<T>> body, CancellationToken ct)
    {
        if (_compatibilitySession.Value is not null)
            throw new InvalidOperationException("A legacy session callback cannot acquire another profile transaction. Use the explicit-context overload and pass its context to helpers.");
        EnsureDirectory();
        var lockPath = Path.GetFullPath(Path.Combine(Directory, CliSessionFile.LockFileName));
        var gate = SessionGates.GetOrAdd(lockPath, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var file = await AcquireLockFileAsync(lockPath, ct).ConfigureAwait(false);
            var context = new ProfileSecurityTransaction(this);
            try { return await body(context).ConfigureAwait(false); }
            finally { await context.CompleteAsync().ConfigureAwait(false); }
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<FileStream> AcquireLockFileAsync(string path, CancellationToken ct)
    {
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        // Another mbus process holds it only for one refresh (a request or two), so wait briefly. This
        // waits on the real clock on purpose: it is about another process, not mbus's own timers.
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, options);
            }
            catch (IOException) when (started.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new CliException($"Another mbus process is holding {path}. Close it and try again.", 1, ex);
            }
        }
    }

    private void WriteOwnerOnly(string path, string contents) => WriteOwnerOnly(path, new System.Text.UTF8Encoding(false).GetBytes(contents));

    private void WriteOwnerOnly(string path, byte[] contents)
    {
        EnsureDirectory();
        if (string.Equals(path, KeyPath, StringComparison.Ordinal))
        {
            DeleteLeftoverCopies(path);
        }
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        try
        {
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows())
            {
                // Retain owner-only permissions even if the process umask removed an owner permission.
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            DurableProfileFile.Replace(temp, path);
        }
        catch
        {
            // the original issue: never leave the copy behind (for the key file, a whole copy of the private key).
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Deletes the temporary copies of the key file a write that crashed before moving it into place left (the original issue). Two
    /// processes writing the key at once don't happen (sign-in, enrolment and a key-store change are one at a time).
    /// </summary>
    private static void DeleteLeftoverCopies(string path)
    {
        try
        {
            foreach (var leftover in System.IO.Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"))
            {
                TryDelete(leftover);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void EnsureDirectory()
    {
        DurableProfileFile.RejectDirectoryAliases(Directory);
        if (OperatingSystem.IsWindows())
        {
            DurableProfileFile.CreatePrivateDirectory(Directory);
            return;
        }
        // A new folder is owner-only; an existing one (MBUS_HOME pointed somewhere) is left as it is,
        // and the files in it are owner-only either way.
        var missing = new List<string>();
        var current = Path.GetFullPath(Directory);
        while (!System.IO.Directory.Exists(current))
        {
            missing.Add(current);
            current = Path.GetDirectoryName(current) ?? throw new IOException("Cannot locate the profile parent directory.");
        }
        System.IO.Directory.CreateDirectory(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // A flushed file in a new home is insufficient if the home's own parent entry can disappear on a crash.
        foreach (var created in missing) DurableProfileFile.FlushDirectory(Path.GetDirectoryName(created)!);
    }
}
