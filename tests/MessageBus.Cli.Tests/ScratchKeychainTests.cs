using System.Runtime.InteropServices;
using System.Text;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Security;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class ScratchKeychainTests
{
    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    public void Runner_distinguishes_locked_keychain_from_tool_only_access()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("MBUS_TEST_SCRATCH_KEYCHAIN") == "1",
            "Opt in on macOS with MBUS_TEST_SCRATCH_KEYCHAIN=1 to create a disposable Keychain.");
        var folder = Path.Combine(Path.GetTempPath(), "mbus-scratch-keychain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "scratch.keychain-db");
        var tool = new SecurityTool();
        // A disposable password and synthetic key only; every command names the scratch keychain explicitly.
        Assert.Equal(0, tool.Run(["create-keychain", "-p", "scratch-test-only", path], null).Exit);
        IntPtr handle = IntPtr.Zero;
        try
        {
            Assert.Equal(0, SecKeychainOpen(path, out handle));
            Assert.Equal(0, tool.Run(["unlock-keychain", "-p", "scratch-test-only", path], null).Exit);
            var vault = new KeychainKeyVault(folder, new ScopedTool(path), new MacKeychainItems(handle));
            using var key = MachineKey.Create();
            var pem = key.ExportPrivateKeyPem();
            Assert.Equal(0, tool.Run(["add-generic-password", "-s", KeychainKeyVault.Service, "-a", vault.Account,
                "-w", Convert.ToBase64String(Encoding.UTF8.GetBytes(pem)), "-T", SecurityTool.Path, path], null).Exit);
            var store = new ConfigStore(folder, osVault: vault) { RunnerKeyReads = true };
            store.Save(new CliConfig { KeyStore = "keychain", KeyFingerprint = key.Fingerprint });
            var service = Encoding.UTF8.GetBytes(KeychainKeyVault.Service);
            var account = Encoding.UTF8.GetBytes(vault.Account);
            var unlocked = MacSecurity.FindPassword(service, account, out _, handle);
            Assert.True(KeychainStatus.IsAccessDenied(unlocked));
            Assert.Equal(KeychainLockState.Unlocked, MacSecurity.ItemKeychainLockState(service, account, handle));
            Assert.Equal(pem, store.LoadKeyPem());
            Assert.Equal(0, tool.Run(["lock-keychain", path], null).Exit);
            var locked = MacSecurity.FindPassword(service, account, out _, handle);
            Assert.True(KeychainStatus.IsAccessDenied(locked));
            Assert.Equal(KeychainLockState.Locked, MacSecurity.ItemKeychainLockState(service, account, handle));
            Assert.Throws<MachineKeyUnavailableException>(() => store.LoadKeyPem());
            File.WriteAllText(Path.Combine(folder, "results.txt"), $"unlocked={unlocked}; locked={locked}; runner unlocked read succeeded; runner locked read waits\n");
            Console.WriteLine($"Scratch Keychain: unlocked={unlocked}, locked={locked}; runner reads passed; results: {folder}");
        }
        finally
        {
            if (handle != IntPtr.Zero) CFRelease(handle);
            tool.Run(["delete-keychain", path], null);
        }
    }

    private sealed class ScopedTool(string path) : IKeychainTool
    {
        public (int Exit, string Output) Run(IReadOnlyList<string> args, string? input) => new SecurityTool().Run([.. args, path], input);
    }

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr keychain);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr value);
}
