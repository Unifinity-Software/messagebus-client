using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MessageBus.Cli.Security;

/// <summary>
/// macOS, when this mbus carries a Developer ID signature (the original issue): the machine key is a login Keychain item that mbus
/// writes itself through Security.framework, so the item's access list trusts only mbus. Apple's
/// <c>security</c> tool, and any other program a run starts, can then no longer read it without macOS asking the
/// person first. Updates keep access because a Developer ID signature stays the same from one release to the next
/// (an ad-hoc signature changes with every build, so ad-hoc builds keep <see cref="KeychainKeyVault"/>).
/// </summary>
/// <remarks>
/// An item an older mbus wrote through <c>security</c> (it trusts that tool, not mbus) is read once through the tool and
/// written again here, which moves it to mbus-only access. mbus never lets macOS show a prompt itself: a runner has
/// nobody watching its screen, so an item it can't read silently counts as unreadable.
/// </remarks>
/// <param name="configDirectory">The settings folder (names the item, like <see cref="KeychainKeyVault"/>).</param>
/// <param name="viaTool">The same item through Apple's tool, for items written before mbus was Developer ID signed.</param>
internal sealed class AppOnlyKeychainKeyVault(string configDirectory, KeychainKeyVault viaTool) : IKeychainVault
{
    private readonly byte[] _service = Encoding.UTF8.GetBytes(KeychainKeyVault.Service);
    private readonly byte[] _account = Encoding.UTF8.GetBytes(viaTool.Account);

    public string Description => "in your login Keychain, readable only by mbus";

    /// <summary>The settings folder the item belongs to.</summary>
    public string ConfigDirectory { get; } = configDirectory;

    public string? Load() => Read(runner: false);

    public string? LoadWithoutPrompts() => Read(runner: true);

    private string? Read(bool runner)
    {
        var status = viaTool.Items.FindPassword(_service, _account, out var secret);
        if (status == KeychainStatus.Success)
        {
            return KeychainKeyVault.Decode(secret!);
        }
        if (status == KeychainStatus.ItemNotFound)
        {
            return null;
        }
        if (!KeychainStatus.IsAccessDenied(status))
        {
            throw new CliException($"Couldn't read this machine's key from the Keychain (error {status}). Unlock the login keychain, or run mbus login again.");
        }
        // Written through Apple's tool by an earlier mbus (or by one signed differently): read it that way once, then
        // write it again from here so only mbus can read it from now on.
        var pem = viaTool.LoadThroughTool(runner);
        if (pem is not null)
        {
            try
            {
                Save(pem);
            }
            catch (Exception ex) when (ex is IOException or CliException)
            {
                // Put back the way it was (Save may have deleted it); moving it is tried again next time.
                try
                {
                    if (viaTool.Load() is null)
                    {
                        viaTool.Save(pem);
                    }
                }
                catch (Exception again) when (again is IOException or CliException)
                {
                }
            }
        }
        return pem;
    }

    public void Save(string pem)
    {
        Delete();
        var status = viaTool.Items.Add(_service, _account, Encoding.UTF8.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes(pem))));
        if (status != KeychainStatus.Success)
        {
            throw new IOException($"The Keychain didn't save this machine's key (error {status}).");
        }
        string? back = null;
        try
        {
            back = viaTool.Items.FindPassword(_service, _account, out var saved) == KeychainStatus.Success ? KeychainKeyVault.Decode(saved!) : null;
        }
        catch (CliException)
        {
        }
        if (back != pem)
        {
            throw new IOException("The Keychain didn't give this machine's key back after saving it.");
        }
        viaTool.ForgetRefusal();
    }

    public void Delete()
    {
        var status = viaTool.Items.Delete(_service, _account);
        if (status is KeychainStatus.Success or KeychainStatus.ItemNotFound)
        {
            viaTool.ForgetRefusal();
            return;
        }
        if (KeychainStatus.IsAccessDenied(status))
        {
            viaTool.Delete(); // an item the tool wrote: the tool may delete it
            return;
        }
        throw new CliException($"Couldn't delete this machine's key from the Keychain (error {status}).");
    }

    /// <summary>Whether the item is there, without reading the key (no prompt, no access check).</summary>
    public bool Exists() => viaTool.Items.Exists(_service, _account);

    public KeychainLockState LockState() => viaTool.LockState();
}

/// <summary>Whether the keychain holding the machine key's item is unlocked (the original issue).</summary>
internal enum KeychainLockState
{
    Unlocked,
    Locked,

    /// <summary>macOS didn't say: treated like locked, so a runner waits rather than ends.</summary>
    Unknown,

    /// <summary>There is no such item (deleted since it was last looked up).</summary>
    ItemMissing,
}

/// <summary>The Keychain status codes (OSStatus) mbus acts on.</summary>
internal static class KeychainStatus
{
    public const int Success = 0;
    public const int ItemNotFound = -25300;
    private const int AuthFailed = -25293;
    private const int InteractionNotAllowed = -25308;
    private const int NoAccessForItem = -25243;

    /// <summary>The item exists but its access list doesn't trust this program (so macOS would have asked someone).</summary>
    public static bool IsAccessDenied(int status) => status is AuthFailed or InteractionNotAllowed or NoAccessForItem;
}

/// <summary>
/// The Keychain calls the vaults make, none of which lets macOS ask anyone: Security.framework on macOS
/// (<see cref="MacKeychainItems"/>), a fake in tests.
/// </summary>
internal interface IKeychainItems
{
    /// <summary>Reads a generic password; returns the status (<see cref="KeychainStatus"/>).</summary>
    int FindPassword(byte[] service, byte[] account, out byte[]? secret);

    /// <summary>Whether the keychain holding the item is unlocked, without reading its secret.</summary>
    KeychainLockState LockState(byte[] service, byte[] account);

    /// <summary>Whether the item exists; reads only its attributes, which needs no access.</summary>
    bool Exists(byte[] service, byte[] account);

    /// <summary>Adds a generic password to the login keychain, trusting only this program; returns the status.</summary>
    int Add(byte[] service, byte[] account, byte[] secret);

    /// <summary>Deletes the item; returns the status.</summary>
    int Delete(byte[] service, byte[] account);
}

/// <summary>The Keychain through Security.framework.</summary>
/// <param name="searchKeychain">The keychain items are looked up in (default: the search list; a test's scratch keychain).</param>
[SupportedOSPlatform("macos")]
internal sealed class MacKeychainItems(IntPtr searchKeychain = default) : IKeychainItems
{
    public int FindPassword(byte[] service, byte[] account, out byte[]? secret) => MacSecurity.FindPassword(service, account, out secret, searchKeychain);

    public KeychainLockState LockState(byte[] service, byte[] account) => MacSecurity.ItemKeychainLockState(service, account, searchKeychain);

    public bool Exists(byte[] service, byte[] account) => MacSecurity.ItemExists(service, account, searchKeychain);

    public int Add(byte[] service, byte[] account, byte[] secret) => MacSecurity.AddPassword(service, account, secret);

    public int Delete(byte[] service, byte[] account) => MacSecurity.DeleteItem(service, account);
}

/// <summary>Whether this mbus is signed with an Apple Developer ID certificate (the original issue), checked once against its own code signature.</summary>
[SupportedOSPlatform("macos")]
internal static class MacCodeSignature
{
    /// <summary>The assembly metadata key a release build writes: the Apple team the Developer ID belongs to (<c>-p:MbusAppleTeamId=…</c>).</summary>
    public const string TeamMetadataKey = "MbusAppleTeamId";

    // Apple's own definition of a Developer ID application: issued under Apple's root, through the Developer ID CA
    // (marker 1.2.840.113635.100.6.2.6), to a Developer ID Application leaf (marker 1.2.840.113635.100.6.1.13).
    private const string DeveloperId =
        "anchor apple generic and certificate 1[field.1.2.840.113635.100.6.2.6] exists and certificate leaf[field.1.2.840.113635.100.6.1.13] exists";

    private static readonly Lazy<bool> Signed = new(Check);

    /// <summary>The team built into this mbus, when the release build said.</summary>
    public static string? Team { get; } = typeof(MacCodeSignature).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == TeamMetadataKey)?.Value is { Length: > 0 } team ? team : null;

    /// <summary>True when the running mbus satisfies the Developer ID requirement (and is from <see cref="Team"/>, when built in).</summary>
    public static bool IsDeveloperIdSigned => Signed.Value;

    private static bool Check()
    {
        // A team id is ten letters and digits: nothing in it can change the requirement's meaning.
        var requirement = Team is { Length: 10 } team && team.All(char.IsAsciiLetterOrDigit)
            ? $"{DeveloperId} and certificate leaf[subject.OU] = \"{team}\""
            : DeveloperId;
        try
        {
            return MacSecurity.SelfSatisfies(requirement);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }
}

/// <summary>The few Security.framework, CoreFoundation and sandbox calls mbus makes on macOS.</summary>
[SupportedOSPlatform("macos")]
internal static class MacSecurity
{
    private const string SecurityLibrary = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationLibrary = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string SystemLibrary = "/usr/lib/libSystem.B.dylib";
    private const uint Utf8 = 0x08000100; // kCFStringEncodingUTF8

    private const int ErrSecSuccess = KeychainStatus.Success;
    private const int ErrSecItemNotFound = KeychainStatus.ItemNotFound;

    private static readonly Lock Gate = new();

    /// <summary>Reads a generic password without letting macOS ask anyone.</summary>
    public static int FindPassword(byte[] service, byte[] account, out byte[]? secret, IntPtr keychain = default)
    {
        secret = null;
        lock (Gate)
        {
            using var quiet = new NoPrompts();
            var status = SecKeychainFindGenericPassword(keychain, (uint)service.Length, service, (uint)account.Length, account,
                out var length, out var data, out var item);
            Release(item);
            if (status == ErrSecSuccess)
            {
                try
                {
                    secret = new byte[length];
                    Marshal.Copy(data, secret, 0, (int)length);
                }
                finally
                {
                    SecKeychainItemFreeContent(IntPtr.Zero, data);
                }
            }
            return status;
        }
    }

    /// <summary>Checks the keychain containing this item, without reading its secret or allowing prompts.</summary>
    public static KeychainLockState ItemKeychainLockState(byte[] service, byte[] account, IntPtr searchKeychain = default)
    {
        lock (Gate)
        {
            using var quiet = new NoPrompts();
            var status = SecKeychainFindGenericPassword(searchKeychain, (uint)service.Length, service, (uint)account.Length, account,
                IntPtr.Zero, IntPtr.Zero, out var item);
            IntPtr keychain = IntPtr.Zero;
            try
            {
                if (status == ErrSecItemNotFound) return KeychainLockState.ItemMissing;
                // the original issue: a state macOS won't give counts as locked (a runner waits), never as an error that ends it.
                if (status != ErrSecSuccess || SecKeychainItemCopyKeychain(item, out keychain) != ErrSecSuccess
                    || SecKeychainGetStatus(keychain, out var flags) != ErrSecSuccess)
                    return KeychainLockState.Unknown;
                return (flags & 1) != 0 ? KeychainLockState.Unlocked : KeychainLockState.Locked; // kSecUnlockStateStatus
            }
            finally
            {
                Release(keychain);
                Release(item);
            }
        }
    }

    /// <summary>Whether the item exists; reads only its attributes, which needs no access.</summary>
    public static bool ItemExists(byte[] service, byte[] account, IntPtr keychain = default)
    {
        lock (Gate)
        {
            using var quiet = new NoPrompts();
            var status = SecKeychainFindGenericPassword(keychain, (uint)service.Length, service, (uint)account.Length, account,
                IntPtr.Zero, IntPtr.Zero, out var item);
            Release(item);
            return status == ErrSecSuccess;
        }
    }

    /// <summary>Adds a generic password to the default (login) keychain. Its access list trusts only the calling program.</summary>
    public static int AddPassword(byte[] service, byte[] account, byte[] secret)
    {
        lock (Gate)
        {
            using var quiet = new NoPrompts();
            var status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                (uint)secret.Length, secret, out var item);
            Release(item);
            return status;
        }
    }

    /// <summary>Deletes the item, if there is one.</summary>
    public static int DeleteItem(byte[] service, byte[] account)
    {
        lock (Gate)
        {
            using var quiet = new NoPrompts();
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
                IntPtr.Zero, IntPtr.Zero, out var item);
            if (status != ErrSecSuccess)
            {
                Release(item);
                return status;
            }
            try
            {
                return SecKeychainItemDelete(item);
            }
            finally
            {
                Release(item);
            }
        }
    }

    /// <summary>Whether the running program's code signature satisfies <paramref name="requirement"/> (code-signing requirement language).</summary>
    public static bool SelfSatisfies(string requirement)
    {
        var text = CFStringCreateWithCString(IntPtr.Zero, requirement, Utf8);
        if (text == IntPtr.Zero)
        {
            return false;
        }
        IntPtr compiled = IntPtr.Zero, self = IntPtr.Zero;
        try
        {
            return SecRequirementCreateWithString(text, 0, out compiled) == ErrSecSuccess
                && SecCodeCopySelf(0, out self) == ErrSecSuccess
                && SecCodeCheckValidity(self, 0, compiled) == ErrSecSuccess;
        }
        finally
        {
            Release(self);
            Release(compiled);
            Release(text);
        }
    }

    /// <summary>Whether this process runs inside an App Sandbox or <c>sandbox-exec</c> profile (a run's macOS sandbox, the original issue).</summary>
    public static bool InSandbox()
    {
        try
        {
            return sandbox_check(Environment.ProcessId, IntPtr.Zero, 0) != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static void Release(IntPtr handle)
    {
        if (handle != IntPtr.Zero)
        {
            CFRelease(handle);
        }
    }

    /// <summary>Turns the Keychain's own prompts off for one call (a runner has nobody to answer them), and back on after.</summary>
    private readonly struct NoPrompts : IDisposable
    {
        public NoPrompts() => SecKeychainSetUserInteractionAllowed(false);

        public void Dispose() => SecKeychainSetUserInteractionAllowed(true);
    }

    [DllImport(SecurityLibrary)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [DllImport(SecurityLibrary, EntryPoint = "SecKeychainFindGenericPassword")]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, IntPtr passwordLength, IntPtr passwordData, out IntPtr itemRef);

    [DllImport(SecurityLibrary)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, out IntPtr itemRef);

    [DllImport(SecurityLibrary)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport(SecurityLibrary)]
    private static extern int SecKeychainItemCopyKeychain(IntPtr item, out IntPtr keychain);

    [DllImport(SecurityLibrary)]
    private static extern int SecKeychainGetStatus(IntPtr keychain, out uint status);

    [DllImport(SecurityLibrary)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(SecurityLibrary)]
    private static extern int SecKeychainSetUserInteractionAllowed([MarshalAs(UnmanagedType.U1)] bool state);

    [DllImport(SecurityLibrary)]
    private static extern int SecCodeCopySelf(uint flags, out IntPtr self);

    [DllImport(SecurityLibrary)]
    private static extern int SecRequirementCreateWithString(IntPtr text, uint flags, out IntPtr requirement);

    [DllImport(SecurityLibrary)]
    private static extern int SecCodeCheckValidity(IntPtr staticCode, uint flags, IntPtr requirement);

    [DllImport(CoreFoundationLibrary)]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);

    [DllImport(CoreFoundationLibrary)]
    private static extern void CFRelease(IntPtr handle);

    [DllImport(SystemLibrary)]
    private static extern int sandbox_check(int pid, IntPtr operation, int type);
}
