using System.Security.Cryptography;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Security;
using Xunit;

namespace MessageBus.Crypto.Tests;

/// <summary>
/// Role keys reach machines only through signed grants: a machine with a grant opens role messages,
/// and a forged or redirected grant is refused. (Rotation runs through the service in KeyEndpointTests.)
/// </summary>
public sealed class GrantTests : IDisposable
{
    private const string Workspace = "w101";
    private const string Admin = "microsoft:admin";

    // The admin's PC (holds the role key), a runner that joins the role, and a third machine.
    private readonly KeyPair _adminDevice = KeyPair.GenerateP256();
    private readonly KeyPair _runnerDevice = KeyPair.GenerateP256();
    private readonly KeyPair _otherDevice = KeyPair.GenerateP256();
    private readonly PublicKeyInfo _adminInfo;
    private readonly PublicKeyInfo _runnerInfo;
    private readonly PublicKeyInfo _otherInfo;

    public GrantTests()
    {
        _adminInfo = TestKeys.Info(_adminDevice, KeyScope.Machine, "mch_admin");
        _runnerInfo = TestKeys.Info(_runnerDevice, KeyScope.Machine, "mch_runner");
        _otherInfo = TestKeys.Info(_otherDevice, KeyScope.Machine, "mch_other");
    }

    public void Dispose()
    {
        _adminDevice.Dispose();
        _runnerDevice.Dispose();
        _otherDevice.Dispose();
    }

    [Fact]
    public void A_runner_with_a_grant_reads_role_messages_and_other_machines_cant_open_its_grant()
    {
        using var roleKey = KeyPair.GenerateP256();
        var roleInfo = TestKeys.Info(roleKey, KeyScope.Role, "developer");
        var grant = GrantCrypto.Create(Workspace, roleInfo, roleKey, _runnerInfo, _adminInfo, _adminDevice, Admin, TestKeys.Now);
        var message = BodyCrypto.Seal(new MessageBody { Summary = "Implement due dates" }, roleInfo);

        using var runner = RingFor(_runnerInfo, _runnerDevice);
        runner.Add(roleInfo.KeyId, GrantCrypto.Open(grant, Workspace, roleInfo, _adminInfo, runner));
        Assert.Equal("Implement due dates", BodyCrypto.Open(message.ToSealedBody(), runner).Summary);

        using var other = RingFor(_otherInfo, _otherDevice);
        Assert.ThrowsAny<CryptographicException>(() => GrantCrypto.Open(grant, Workspace, roleInfo, _adminInfo, other));
        Assert.Equal(_runnerInfo.KeyId, GrantCrypto.SealedToKeyId(grant));
    }

    public static TheoryData<string> Forgeries =>
        ["signed-by-a-non-admin-key", "sealed-key-swapped", "other-workspace", "other-role", "other-version", "signature-garbled"];

    [Theory]
    [MemberData(nameof(Forgeries))]
    public void A_grant_that_wasnt_signed_for_exactly_this_key_and_machine_is_refused(string forgery)
    {
        using var roleKey = KeyPair.GenerateP256();
        using var attackerKey = KeyPair.GenerateP256();
        var roleInfo = TestKeys.Info(roleKey, KeyScope.Role, "developer");
        var genuine = GrantCrypto.Create(Workspace, roleInfo, roleKey, _runnerInfo, _adminInfo, _adminDevice, Admin, TestKeys.Now);
        var workspace = Workspace;
        var grantedKey = roleInfo;
        var signer = _adminInfo;
        var grant = genuine;

        switch (forgery)
        {
            case "signed-by-a-non-admin-key":
                // Someone who can write to storage seals their own key to the runner and signs with their own device key.
                var attackerRole = roleInfo with { PublicKey = attackerKey.PublicKey.ToBase64(), Fingerprint = attackerKey.PublicKey.Fingerprint };
                grant = GrantCrypto.Create(Workspace, attackerRole, attackerKey, _runnerInfo, _otherInfo, _otherDevice, Admin, TestKeys.Now)
                    with { SigningKeyId = _adminInfo.KeyId };
                grantedKey = attackerRole;
                break;
            case "sealed-key-swapped":
                var forged = GrantCrypto.Create(Workspace, roleInfo with { PublicKey = attackerKey.PublicKey.ToBase64(), Fingerprint = attackerKey.PublicKey.Fingerprint },
                    attackerKey, _runnerInfo, _otherInfo, _otherDevice, Admin, TestKeys.Now);
                grant = genuine with { SealedPrivateKey = forged.SealedPrivateKey };
                break;
            case "other-workspace": workspace = "w202"; break;
            case "other-role": grant = genuine with { Role = "reviewer" }; grantedKey = roleInfo with { Owner = "reviewer" }; break;
            case "other-version": grant = genuine with { KeyVersion = 2 }; grantedKey = roleInfo with { Version = 2 }; break;
            case "signature-garbled": grant = genuine with { Signature = genuine.Signature[1..] + "A" }; break;
        }

        Assert.False(GrantCrypto.Verify(grant, workspace, grantedKey, signer));
        using var runner = RingFor(_runnerInfo, _runnerDevice);
        Assert.ThrowsAny<CryptographicException>(() => GrantCrypto.Open(grant, workspace, grantedKey, signer, runner));
    }

    private static KeyRing RingFor(PublicKeyInfo info, KeyPair device) =>
        // The ring owns what it holds; give it its own copy so the fixture's key outlives it.
        new KeyRing().Add(info.KeyId, KeyPair.FromPkcs8(device.ExportPkcs8()));
}
