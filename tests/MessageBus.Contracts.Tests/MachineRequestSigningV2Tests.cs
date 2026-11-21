using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using MessageBus.Contracts.Security;
using Xunit;

namespace MessageBus.Contracts.Tests;

public sealed class MachineRequestSigningV2Tests
{
    private const string Machine = "mch_00000000000000000000000000";
    private const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private const string BodyHash = "a8e2131cfaf51b3a525a74979e4e9dbec75c29e42a0fe070dd816692eece5cd8";
    private const string Nonce = "AAAAAAAAAAAAAAAAAAAAAA";
    // Independently generated using Python stdlib P-256 arithmetic and hashlib, never the production builder/signer.
    // Synthetic public point G (fixture scalar 1); no production identity or private key is stored here.
    private const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEaxfR8uEsQkf4vOblY6RA8ncDfYEt6zOg9KE5RdiYwpZP40Li/hp/m47n60p8D54WK84zV2sxXs7LtkBoN79R9Q==";
    private const string Absent = "mbus-machine-v2\nhttps://eu.stamps.example.test\neu\nmch_00000000000000000000000000\n\nGET\n/api/machines/me?x=%2F&y=1\n1795219200\nAAAAAAAAAAAAAAAAAAAAAA\n" + EmptyHash;
    private const string Present = "mbus-machine-v2\nhttps://eu.stamps.example.test\neu\nmch_00000000000000000000000000\nw101\nPOST\n/gateway/api/machines/me?x=%2F&y=1\n1795219200\nAAAAAAAAAAAAAAAAAAAAAA\n" + BodyHash;

    [Theory]
    [InlineData(false, "dbb7553068d6cef6aa3bf561ee4d3546aecf929c9511c145495380e788785e9e", "v0Y2yGTG1GAHubR4GRUmA-Z3MWbX_yw-NPcXrEEYQlp39V5X96gYsAlCDt6MYCQpfpw5MAwUUX_iWompWDzWFQ")]
    [InlineData(true, "d4cbbabb288e7beb69cac57385f92b183fb6a870a580c227b1feb81f560f3731", "sFE1BhBeMpeauF3eFmjApbVtdKT9jif4vl24gM2jaTaYSaUchd7cqDjkXu_p0IYrTb6pS3kZ830Vrcl9YydPdg")]
    public void Independent_vectors_bind_every_field_and_exact_framing(bool workspace, string digest, string signature)
    {
        var literal = workspace ? Present : Absent;
        var actual = MachineRequestSigningV2.CanonicalString("https://eu.stamps.example.test", "eu", Machine,
            workspace ? "w101" : null, workspace ? "post" : "get",
            workspace ? "/gateway/api/machines/me?x=%2F&y=1" : "/api/machines/me?x=%2F&y=1",
            1795219200, Nonce, workspace ? BodyHash : EmptyHash);
        Assert.Equal(Encoding.UTF8.GetBytes(literal), Encoding.UTF8.GetBytes(actual));
        Assert.Equal(digest, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(actual))));
        if (workspace) Assert.Equal(BodyHash, Convert.ToHexStringLower(SHA256.HashData("{ \"x\": 1 }\n"u8)));
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
        var sig = Base64Url.DecodeFromChars(signature);
        bool Verify(string candidate) => key.VerifyData(Encoding.UTF8.GetBytes(candidate), sig,
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Assert.True(Verify(actual));
        for (var i = 0; i < 10; i++)
        {
            var fields = literal.Split('\n');
            fields[i] += "x";
            Assert.False(Verify(string.Join('\n', fields)));
        }
        Assert.False(Verify(actual + "\n"));
        Assert.False(Verify("\ufeff" + actual));
        Assert.False(Verify(actual.Replace("\n", "\r\n", StringComparison.Ordinal)));
        // The same independently registered public key and machine ID cannot verify EU bytes as CA.
        var ca = MachineRequestSigningV2.CanonicalString("https://ca.stamps.example.test", "ca", Machine,
            workspace ? "w101" : null, workspace ? "POST" : "GET",
            workspace ? "/gateway/api/machines/me?x=%2F&y=1" : "/api/machines/me?x=%2F&y=1",
            1795219200, Nonce, workspace ? BodyHash : EmptyHash);
        Assert.False(Verify(ca));
        Assert.False(Verify(MachineRequestSigning.CanonicalString("GET", "/api/machines/me?x=%2F&y=1", 1795219200, Nonce, EmptyHash)));
    }

    [Theory]
    [InlineData("//foreign/path")]
    [InlineData("https://foreign.example.test/api")]
    [InlineData("/api#fragment")]
    [InlineData("/api\\bad")]
    [InlineData("/api\nGET")]
    [InlineData("/api?bad=%G0")]
    [InlineData("/api with space")]
    public void Ambiguous_targets_cannot_be_signed(string target) => Assert.Throws<ArgumentException>(() =>
        MachineRequestSigningV2.CanonicalString("https://eu.stamps.example.test", "eu", Machine, null, "GET", target, 1, Nonce, EmptyHash));

    [Fact]
    public void Malformed_headers_cannot_select_or_frame_v2()
    {
        Assert.True(MachineRequestSigningV2.IsVersionHeader(["2"]));
        foreach (var values in new string[][] { [], ["1"], ["3"], ["2", "2"], ["2,2"], [" 2"] })
            Assert.False(MachineRequestSigningV2.IsVersionHeader(values));
        foreach (var timestamp in new[] { "-1", "+1", "01", " 1", "1,1", "253402300800", "9223372036854775808" })
            Assert.False(MachineRequestSigningV2.TryParseTimestamp(timestamp, out _));
        Assert.True(MachineRequestSigningV2.TryParseTimestamp("0", out _));
        Assert.True(MachineRequestSigningV2.IsCanonicalBase64Url(Nonce, 16));
        Assert.False(MachineRequestSigningV2.IsCanonicalBase64Url("AAAAAAAAAAAAAAAAAAAAAB", 16));
        Assert.False(MachineRequestSigningV2.IsCanonicalBase64Url(Nonce + "==", 16));
        Assert.Throws<ArgumentException>(() => MachineRequestSigningV2.CanonicalString("https://eu.stamps.example.test", "eu", Machine, "", "GET", "/", 1, Nonce, EmptyHash));
        Assert.Throws<ArgumentException>(() => MachineRequestSigningV2.CanonicalString("https://eu.stamps.example.test", "eu", Machine, "w101\nw102", "GET", "/", 1, Nonce, EmptyHash));
    }
}
