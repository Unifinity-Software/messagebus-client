using MessageBus.Cli.Configuration;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;
using MessageBus.Mcp;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class KeyTrustTests
{
    [Fact]
    public void Saving_a_new_server_after_logout_binds_all_old_workspaces_to_the_previous_server_once()
    {
        using var machine = new TestMachine();
        var store = new ConfigStore(machine.MbusFolder);
        store.Save(new CliConfig { Server = "https://old.example" }); // logged out: no workspace or credentials
        var legacy = Path.Combine(machine.MbusFolder, "trusted-keys.json");
        const string oldJson = """
            {"workspaces":{
              "w100":{"keys":{"developer":[{"version":1,"keyId":"ek_old","fingerprint":"old-role"}]},
                      "signers":{"mch_admin":{"keyId":"ek_admin","fingerprint":"old-admin"}}},
              "w101":{"keys":{"-":[{"version":1,"keyId":"ek_ws","fingerprint":"old-workspace"}]}}
            }}
            """;
        File.WriteAllText(legacy, oldJson);

        store.Save(new CliConfig { Server = "https://new.example" });

        Assert.Empty(KeyTrust.Load(machine.MbusFolder, "w100", "https://new.example").Keys);
        var original = KeyTrust.Load(machine.MbusFolder, "w100", "https://old.example");
        Assert.Equal("old-role", Assert.Single(original.Keys["developer"]).Fingerprint);
        Assert.Equal("old-admin", original.Signers["mch_admin"].Fingerprint);
        Assert.Equal("old-workspace", Assert.Single(KeyTrust.Load(machine.MbusFolder, "w101", "https://old.example").Keys["-"]).Fingerprint);
        Assert.Equal(oldJson, File.ReadAllText(legacy));
        // An older client can still rewrite its file, but can't change the imported v2 pins.
        File.WriteAllText(legacy, "{\"workspaces\":{}}");
        Assert.Equal("old-admin", KeyTrust.Load(machine.MbusFolder, "w100", "https://old.example").Signers["mch_admin"].Fingerprint);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Old_trust_without_a_saved_server_uses_the_explicit_server_on_load_or_save(bool save)
    {
        using var machine = new TestMachine();
        Directory.CreateDirectory(machine.MbusFolder);
        File.WriteAllText(Path.Combine(machine.MbusFolder, "trusted-keys.json"), """
            {"workspaces":{"w100":{"signers":{"mch_admin":{"keyId":"ek_admin","fingerprint":"old-admin"}}}}}
            """);
        if (save) KeyTrust.Save(machine.MbusFolder, "w101", new TrustedWorkspace(), "app.messagebus.ai");
        Assert.Equal("old-admin", KeyTrust.Load(machine.MbusFolder, "w100", "https://app.messagebus.ai").Signers["mch_admin"].Fingerprint);
        Assert.Empty(KeyTrust.Load(machine.MbusFolder, "w100", "https://other.example").Signers);
    }

    [Fact]
    public void Damaged_settings_are_identified_before_importing_old_trust()
    {
        using var machine = new TestMachine();
        Directory.CreateDirectory(machine.MbusFolder);
        File.WriteAllText(Path.Combine(machine.MbusFolder, "config.json"), "broken");
        File.WriteAllText(Path.Combine(machine.MbusFolder, "trusted-keys.json"), "{\"workspaces\":{\"w100\":{}}}");
        var error = Assert.Throws<InvalidDataException>(() => KeyTrust.Load(machine.MbusFolder, "w100", "https://example.test"));
        Assert.Contains("config.json is damaged", error.Message);
    }

    [Theory]
    [InlineData("app.messagebus.ai", "https://app.messagebus.ai")]
    [InlineData("localhost:5200", "https://localhost:5200")]
    [InlineData(" https://ONE.example:443/ignored/path/ ", "https://one.example")]
    public void Server_names_use_the_same_default_scheme_as_login(string input, string expected) =>
        Assert.Equal(expected, KeyTrust.NormalizeServer(input));

    [Fact]
    public void Servers_with_the_same_workspace_keep_independent_keys_and_signers_across_restarts()
    {
        using var machine = new TestMachine();
        var store = new ConfigStore(machine.MbusFolder);
        const string serverA = "https://ONE.example:443/ignored/path/";
        const string serverB = "https://one.example:8443";
        store.Save(new CliConfig { Server = serverA });
        var first = new PublicKeyInfo
        {
            KeyId = "ek_a", Scope = KeyScope.Role, Owner = "developer", Version = 1,
            Algorithm = "P-256", PublicKey = "unused", Fingerprint = "fingerprint-a", CreatedAt = DateTimeOffset.UnixEpoch,
        };
        var second = first with { KeyId = "ek_b", Fingerprint = "fingerprint-b" };
        var pins = KeyTrust.Load(machine.MbusFolder, "w100", serverA);
        KeyTrust.Accept(pins, first, null);
        pins.Signers["mch_admin"] = new TrustedSigner("ek_admin_a", "admin-a");
        KeyTrust.Save(machine.MbusFolder, "w100", pins, serverA);

        var other = KeyTrust.Load(machine.MbusFolder, "w100", serverB);
        Assert.Empty(other.Signers);
        Assert.Equal(TrustStatus.New, KeyTrust.Check(other, second, new KeyDirectory([]), "w100", null).Status);
        KeyTrust.Accept(other, second, null);
        KeyTrust.Save(machine.MbusFolder, "w100", other, serverB);

        var original = KeyTrust.Load(machine.MbusFolder, "w100", "https://one.EXAMPLE/");
        Assert.Equal(TrustStatus.Trusted, KeyTrust.Check(original, first, new KeyDirectory([]), "w100", null).Status);
        Assert.Equal(TrustStatus.Changed, KeyTrust.Check(original, second, new KeyDirectory([]), "w100", null).Status);
        Assert.Equal("admin-a", original.Signers["mch_admin"].Fingerprint);
        Assert.Equal(TrustStatus.Trusted, KeyTrust.Check(KeyTrust.Load(machine.MbusFolder, "w100", serverB), second, new KeyDirectory([]), "w100", null).Status);
        Assert.Empty(KeyTrust.Load(machine.MbusFolder, "w101", serverA).Keys);
        Assert.Empty(KeyTrust.Load(machine.MbusFolder, "w100", "http://one.example").Keys);
    }

}
