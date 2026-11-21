using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace MessageBus.Cli.Security;

/// <summary>
/// Where this machine's private key is kept (the original issue): Windows protects it for the signed-in user with DPAPI,
/// macOS keeps it in the login Keychain, and Linux (and anything else, or <c>MBUS_KEY_STORE=file</c>)
/// keeps the owner-only (<c>600</c>) PEM file in <c>~/.mbus</c>.
/// </summary>
public interface IMachineKeyVault
{
    /// <summary>For people: where the key is (one short phrase).</summary>
    string Description { get; }

    /// <summary>The key's PEM, or null when there is none.</summary>
    string? Load();

    /// <summary>Saves the key, replacing any other.</summary>
    void Save(string pem);

    /// <summary>Deletes the key, if there is one.</summary>
    void Delete();
}

/// <summary>An OS vault that can address independent immutable identity generations.</summary>
public interface IGenerationMachineKeyVault : IMachineKeyVault
{
    /// <summary>Selects a separate item without reading, replacing or deleting the current item.</summary>
    IMachineKeyVault ForGeneration(string generation);
}

/// <summary>A key in the login Keychain: what a runner and a change of key store ask it without letting macOS prompt.</summary>
internal interface IKeychainVault : IMachineKeyVault
{
    /// <summary>
    /// The key for a runner (nobody watches its screen): <see cref="MachineKeyUnavailableException"/> while the keychain is
    /// locked, <see cref="MachineKeyRefusedException"/> when it is unlocked and still won't give mbus the key.
    /// </summary>
    string? LoadWithoutPrompts();

    /// <summary>Whether the item is there, without reading the key.</summary>
    bool Exists();

    /// <summary>Whether the item's keychain is unlocked.</summary>
    KeychainLockState LockState();
}

/// <summary>The PEM file, readable and writable by the owner only (written in one step by <see cref="Configuration.ConfigStore"/>).</summary>
internal sealed class FileKeyVault(string path, Action<string, string> writeOwnerOnly) : IMachineKeyVault
{
    public string Path { get; } = path;

    public string Description => "a file only you can read (" + Path + ")";

    public string? Load() => File.Exists(Path) ? File.ReadAllText(Path) : null;

    public void Save(string pem) => writeOwnerOnly(Path, pem);

    public void Delete()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}

/// <summary>
/// Windows: the key encrypted with DPAPI for the current user (<c>machine-key.dpapi</c>). Only this Windows
/// account on this PC can decrypt it, so a copy of the file (a backup, a synced folder) is useless elsewhere.
/// Programs running as you can still ask Windows to decrypt it.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class DpapiKeyVault(string path, Action<string, byte[]> writeOwnerOnly) : IMachineKeyVault
{
    // Ties the blob to this use, so another program's DPAPI blob can't be swapped in for it.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("messagebus.ai mbus machine key v1");

    public string Description => "protected by Windows for your account (DPAPI)";

    public string? Load()
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException ex)
        {
            throw new CliException("This machine's key can't be decrypted (it was saved by another Windows account or PC). Run mbus login again.", 1, ex);
        }
    }

    public void Save(string pem) =>
        writeOwnerOnly(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(pem), Entropy, DataProtectionScope.CurrentUser));

    public void Delete()
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// macOS: a generic password in the login Keychain, through Apple's <c>security</c> tool. The item is named
/// after the settings folder, so two <c>MBUS_HOME</c>s keep two keys. The key goes to <c>security</c> on its
/// standard input, never on a command line other programs could see.
/// </summary>
/// <param name="configDirectory">The settings folder (names the item).</param>
/// <param name="tool">Apple's <c>security</c> tool.</param>
/// <param name="items">The Keychain without prompts (Security.framework on macOS), for a runner's reads.</param>
internal sealed class KeychainKeyVault(string configDirectory, IKeychainTool tool, IKeychainItems? items = null, string? generation = null) : IKeychainVault, IGenerationMachineKeyVault
{
    public const string Service = "ai.messagebus.mbus.machine-key";

    /// <summary>
    /// Left in the settings folder when the unlocked Keychain refused Apple's tool this machine's key (the original issue), so a runner
    /// launchd starts again fails at once instead of running the tool (and the dialog it may show for 30 s) every time.
    /// Saving or deleting the key, or a read through the tool that works, removes it.
    /// </summary>
    public const string RefusedFile = "machine-key.refused";

    private readonly string _refusedPath = System.IO.Path.Combine(configDirectory, generation is null ? RefusedFile : $"machine-key.{generation}.refused");

    public IMachineKeyVault ForGeneration(string value)
    {
        if (value.Length != 32 || value.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Invalid key generation.", nameof(value));
        return new KeychainKeyVault(configDirectory, tool, items, value);
    }

    /// <summary>The item's account: a hash of the settings folder (letters and digits only, nothing to quote).</summary>
    public string Account { get; } = "mbus-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(configDirectory))))[..24]
        + (generation is null ? "" : "-" + generation);

    public string Description => "in your login Keychain";

    internal IKeychainItems Items => items ?? throw new PlatformNotSupportedException("Reading the Keychain without prompts needs macOS.");

    private byte[] ServiceBytes => Encoding.UTF8.GetBytes(Service);

    private byte[] AccountBytes => Encoding.UTF8.GetBytes(Account);

    public string? Load()
    {
        var (exit, output) = tool.Run(["find-generic-password", "-s", Service, "-a", Account, "-w"], null);
        if (exit == 44)
        {
            return null; // errSecItemNotFound
        }
        if (exit is 36 or 51 or -1)
        {
            throw new MachineKeyUnavailableException();
        }
        if (exit != 0)
        {
            throw new CliException($"Couldn't read this machine's key from the Keychain (security exited {exit}: {output.Trim()}). Unlock the login keychain, or run mbus login again.");
        }
        ForgetRefusal();
        return Decode(Encoding.UTF8.GetBytes(output));
    }

    public bool Exists() => Items.Exists(ServiceBytes, AccountBytes);

    public KeychainLockState LockState() => Items.LockState(ServiceBytes, AccountBytes);

    public string? LoadWithoutPrompts()
    {
        var status = Items.FindPassword(ServiceBytes, AccountBytes, out var secret);
        if (status == KeychainStatus.ItemNotFound) return null;
        if (KeychainStatus.IsAccessDenied(status)) return LoadThroughTool(runner: true); // The item may trust Apple's tool rather than this build.
        if (status != KeychainStatus.Success)
            throw new CliException($"Couldn't read this machine's key from the Keychain (error {status}).");
        return Decode(secret!);
    }

    /// <summary>
    /// After the Keychain refused this mbus the key: reads it through Apple's tool, which an item written by an earlier
    /// mbus trusts. A locked keychain (or one whose state macOS won't give) is waited for; an unlocked one that refuses the
    /// tool too needs a new key, and a runner remembers that (<see cref="RefusedFile"/>) instead of asking again.
    /// </summary>
    /// <param name="runner">A runner's read: one already refused fails without running the tool.</param>
    internal string? LoadThroughTool(bool runner)
    {
        switch (LockState())
        {
            case KeychainLockState.ItemMissing:
                return null; // deleted since it was looked up
            case not KeychainLockState.Unlocked:
                throw new MachineKeyUnavailableException();
        }
        if (runner && File.Exists(_refusedPath))
        {
            throw new MachineKeyRefusedException();
        }
        try
        {
            return Load();
        }
        catch (MachineKeyUnavailableException) when (LockState() == KeychainLockState.Unlocked)
        {
            // Still unlocked, so not a lock that came meanwhile: the item trusts neither this mbus nor the tool.
            try
            {
                File.WriteAllText(_refusedPath, "The unlocked login Keychain refused this machine's key to mbus and to Apple's security tool. Run mbus login again.\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            throw new MachineKeyRefusedException();
        }
    }

    /// <summary>Removes <see cref="RefusedFile"/>: the key was saved, deleted or read again.</summary>
    internal void ForgetRefusal()
    {
        try
        {
            File.Delete(_refusedPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>The key's PEM from the item's secret (base64).</summary>
    internal static string Decode(byte[] secret)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.UTF8.GetString(secret).Trim()));
        }
        catch (FormatException ex)
        {
            throw new CliException("This machine's key in the Keychain isn't readable. Run mbus login again.", 1, ex);
        }
    }

    public void Save(string pem)
    {
        // Interactive mode reads the command from stdin; base64 has no characters that need quoting.
        var line = $"add-generic-password -U -s {Service} -a {Account} -l messagebus.ai -w {Convert.ToBase64String(Encoding.UTF8.GetBytes(pem))}\n";
        var (exit, output) = tool.Run(["-i"], line);
        // Interactive mode exits 0 whatever the command did: read the key back to know it is there.
        string? saved;
        try
        {
            saved = exit == 0 ? Load() : null;
        }
        catch (CliException)
        {
            saved = null;
        }
        if (saved != pem)
        {
            throw new IOException($"security add-generic-password didn't save the key (exit {exit}): {output.Replace("security>", "", StringComparison.Ordinal).Trim()}");
        }
    }

    public void Delete()
    {
        var (exit, output) = tool.Run(["delete-generic-password", "-s", Service, "-a", Account], null);
        if (exit is not (0 or 44))
        {
            throw new CliException($"Couldn't delete this machine's key from the Keychain (security exited {exit}: {output.Trim()}).");
        }
        ForgetRefusal();
    }
}

/// <summary>Runs <c>/usr/bin/security</c>.</summary>
internal interface IKeychainTool
{
    /// <summary>Exit code and what it printed (stdout and stderr).</summary>
    (int Exit, string Output) Run(IReadOnlyList<string> args, string? input);
}

internal sealed class SecurityTool : IKeychainTool
{
    public const string Path = "/usr/bin/security";

    public (int Exit, string Output) Run(IReadOnlyList<string> args, string? input)
    {
        var start = new ProcessStartInfo(Path)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = Process.Start(start) ?? throw new IOException("Couldn't start " + Path);
        if (input is not null)
        {
            process.StandardInput.Write(input);
        }
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(30)))
        {
            process.Kill();
            return (-1, "security didn't answer (is the keychain asking for a password on screen?)");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
}
