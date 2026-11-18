using System.Text;
using MessageBus.Cli.Security;
using Xunit;

namespace MessageBus.Cli.Tests;

/// <summary>A runner's Keychain reads (the original issue), against a fake Keychain and a fake <c>security</c> tool: no Mac needed.</summary>
public sealed class KeychainVaultTests
{
    [Fact]
    public void Developer_id_vault_waits_while_locked_and_moves_a_tool_written_item_once_unlocked()
    {
        var folder = NewFolder();
        using var key = MachineKey.Create();
        var pem = key.ExportPrivateKeyPem();
        var tool = new FakeTool { FindExit = 0, FindOutput = Base64(pem) };
        var items = new FakeItems { Lock = KeychainLockState.Locked }; // written through the tool: mbus itself is refused
        var vault = new AppOnlyKeychainKeyVault(folder, new KeychainKeyVault(folder, tool, items));
        Assert.Throws<MachineKeyUnavailableException>(vault.LoadWithoutPrompts);
        items.Lock = KeychainLockState.Unknown; // macOS won't say: wait too, don't end the runner
        Assert.Throws<MachineKeyUnavailableException>(vault.LoadWithoutPrompts);
        Assert.Equal(0, tool.Finds);
        items.Lock = KeychainLockState.ItemMissing; // deleted between the two lookups
        Assert.Null(vault.LoadWithoutPrompts());
        items.Lock = KeychainLockState.Unlocked;
        Assert.Equal(pem, vault.LoadWithoutPrompts());
        Assert.Equal(pem, vault.LoadWithoutPrompts()); // now mbus's own item: the tool isn't needed again
        Assert.Equal(1, tool.Finds);
    }

    [Fact]
    public void Unlocked_keychain_that_refuses_the_tool_too_asks_for_a_new_key_without_asking_again_after_a_restart()
    {
        var folder = NewFolder();
        var tool = new FakeTool { FindExit = -1 }; // the access dialog nobody answered
        var items = new FakeItems { Lock = KeychainLockState.Unlocked };
        Assert.Throws<MachineKeyRefusedException>(new KeychainKeyVault(folder, tool, items).LoadWithoutPrompts);
        Assert.Throws<MachineKeyRefusedException>(new KeychainKeyVault(folder, tool, items).LoadWithoutPrompts); // launchd started it again
        Assert.Equal(1, tool.Finds);
        // Allowed at a terminal (or saved again by mbus login): the runner reads it again.
        using var key = MachineKey.Create();
        tool.FindExit = 0;
        tool.FindOutput = Base64(key.ExportPrivateKeyPem());
        Assert.Equal(key.ExportPrivateKeyPem(), new KeychainKeyVault(folder, tool, items).Load());
        Assert.Equal(key.ExportPrivateKeyPem(), new KeychainKeyVault(folder, tool, items).LoadWithoutPrompts());
    }

    private static string NewFolder() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mbus-keychain-" + Guid.NewGuid().ToString("N"))).FullName;

    private static string Base64(string pem) => Convert.ToBase64String(Encoding.UTF8.GetBytes(pem)) + "\n";

    private sealed class FakeTool : IKeychainTool
    {
        public int FindExit { get; set; }
        public string FindOutput { get; set; } = "";
        public int Finds { get; private set; }

        public (int Exit, string Output) Run(IReadOnlyList<string> args, string? input)
        {
            if (args[0] != "find-generic-password") return (0, "");
            Finds++;
            return (FindExit, FindOutput);
        }
    }

    /// <summary>An item the tool wrote (mbus is refused) until mbus adds its own.</summary>
    private sealed class FakeItems : IKeychainItems
    {
        private const int InteractionNotAllowed = -25308;
        private byte[]? _own;

        public KeychainLockState Lock { get; set; }

        public int FindPassword(byte[] service, byte[] account, out byte[]? secret)
        {
            secret = _own;
            return _own is null ? InteractionNotAllowed : KeychainStatus.Success;
        }

        public KeychainLockState LockState(byte[] service, byte[] account) => Lock;

        public bool Exists(byte[] service, byte[] account) => true;

        public int Add(byte[] service, byte[] account, byte[] secret)
        {
            _own = secret;
            return KeychainStatus.Success;
        }

        public int Delete(byte[] service, byte[] account)
        {
            if (_own is null) return InteractionNotAllowed; // the tool's item: the vault deletes it through the tool
            _own = null;
            return KeychainStatus.Success;
        }
    }
}
