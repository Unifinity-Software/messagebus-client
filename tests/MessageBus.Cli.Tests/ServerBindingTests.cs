using System.Text.Json;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Server;
using MessageBus.Contracts.Security;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class ServerBindingTests
{
    private const string Origin = "https://eu.stamps.example.test";
    private const string Alias = "https://alias.stamps.example.test";
    private const string Legacy = """{"product":"messagebus.ai","signIn":{"microsoft":true},"capabilities":["approvalAffectedV1","settleAttemptV1"],"stamp":{"stampId":"ca","geography":"canada","azureRegion":"canadacentral","url":"https://legacy.example.test","aliases":[]}}""";
    private const string Isolated = """{"product":"messagebus.ai","url":"https://eu.stamps.example.test","capabilities":["approvalAffectedV1","settleAttemptV1"],"stamp":{"stampId":"eu","geography":"eu","azureRegion":"westeurope","url":"https://eu.stamps.example.test","aliases":["https://alias.stamps.example.test"]},"signIn":{"microsoft":true,"authority":{"version":1,"stampId":"eu","issuer":"https://eu.stamps.example.test","audience":"https://eu.stamps.example.test"},"machineSignature":{"version":2,"required":true}}}""";
    private const string Expected = """{"version":1,"transportOrigin":"https://eu.stamps.example.test","stampId":"eu","issuer":"https://eu.stamps.example.test","audience":"https://eu.stamps.example.test","authorityVersion":1,"minimumMachineSignatureVersion":2,"machineSignatureRequired":true}""";
    private static ServerBindingFloor NeverPinned => ServerBindingFloor.FromCompleteState([]);

    [Fact]
    public void Descriptive_stamp_preserves_legacy_but_surviving_or_provisioned_pin_blocks_stripping()
    {
        var endpoint = ServerEndpoint.Parse(Origin + "/gateway");
        foreach (var json in new[] { """{"product":"messagebus.ai"}""", Legacy })
        {
            var discovery = ServerDiscoveryParser.Parse(json);
            Assert.Equal(1, ServerBindingPolicy.Evaluate(endpoint, discovery, NeverPinned, null).MachineSignatureVersion);
            var provisioned = ServerBindingExpectation.Parse(Expected);
            Assert.Throws<ServerBindingException>(() => ServerBindingPolicy.Evaluate(endpoint, discovery, NeverPinned, provisioned));
            // A surviving credential/checkpoint snapshot is evidence even if the ledger is missing.
            var surviving = ServerBindingFloor.FromCompleteState([provisioned]);
            Assert.Throws<ServerBindingException>(() => ServerBindingPolicy.Evaluate(endpoint, discovery, surviving, null));
        }
        Assert.Throws<ServerBindingException>(() => ServerBindingPolicy.Evaluate(endpoint, ServerDiscoveryParser.Parse(Isolated), ServerBindingFloor.Unknown, null));
    }

    [Theory]
    [InlineData("\"authority\":null")]
    [InlineData("\"authority\":{}")]
    [InlineData("\"machineSignature\":null")]
    [InlineData("\"machineSignature\":{\"version\":2,\"required\":true}")]
    public void Any_descriptor_presence_requires_the_whole_bundle(string fields) => Assert.Throws<ServerBindingException>(() =>
        ServerDiscoveryParser.Parse("{\"product\":\"messagebus.ai\",\"signIn\":{" + fields + "}}"));

    [Theory]
    [InlineData("\"version\":2,\"required\":true", "\"version\":1,\"required\":true")]
    [InlineData("\"version\":2,\"required\":true", "\"version\":2,\"required\":false")]
    [InlineData("\"version\":1,\"stampId\"", "\"version\":1,\"version\":1,\"stampId\"")]
    [InlineData("\"authority\":{", "\"Authority\":{")]
    [InlineData("\"machineSignature\":{", "\"machineSignature\":null,\"machineSignature\":{")]
    [InlineData("\"stampId\":\"eu\",\"issuer\"", "\"stampId\":\"ca\",\"issuer\"")]
    [InlineData("\"audience\":\"https://eu.stamps.example.test\"", "\"audience\":\"https://ca.stamps.example.test\"")]
    [InlineData("\"required\":true", "\"required\":\"true\"")]
    [InlineData("\"aliases\":[\"https://alias.stamps.example.test\"]", "\"aliases\":null")]
    public void Malformed_partial_duplicate_and_downgraded_bundles_refuse(string old, string replacement) =>
        Assert.Throws<ServerBindingException>(() => ServerDiscoveryParser.Parse(Isolated.Replace(old, replacement, StringComparison.Ordinal)));

    [Fact]
    public void Alias_needs_both_trusted_local_mapping_and_current_server_configuration()
    {
        var selected = ServerEndpoint.Parse(Alias + "/gateway");
        var discovery = ServerDiscoveryParser.Parse(Isolated);
        Assert.Throws<ServerBindingException>(() => ServerBindingPolicy.Evaluate(selected, discovery, NeverPinned, null));
        var mapped = ServerBindingExpectation.Create(Alias, ServerBinding.Create("eu", Origin, Origin));
        var result = ServerBindingPolicy.Evaluate(selected, discovery, NeverPinned, mapped);
        Assert.Equal(Origin, result.Binding!.Issuer);
        Assert.True(result.RequiresIsolatedPersistence);
        var noAlias = ServerDiscoveryParser.Parse(Isolated.Replace("[\"https://alias.stamps.example.test\"]", "[]", StringComparison.Ordinal));
        Assert.Throws<ServerBindingException>(() => ServerBindingPolicy.Evaluate(selected, noAlias, NeverPinned, mapped));
        Assert.NotEqual(ServerEndpoint.Parse(Origin + "/gateway"), selected); // equal stamp never grants credential migration
        Assert.Throws<ServerBindingException>(() => ServerBindingPolicy.Evaluate(selected, discovery, NeverPinned, ServerBindingExpectation.Parse(Expected)));
    }

    [Fact]
    public void Provisioning_cannot_replace_a_persisted_tuple_and_source_generated_shape_stays_additive()
    {
        var selected = ServerEndpoint.Parse(Origin);
        var provisioned = ServerBindingExpectation.Parse(Expected);
        var conflict = ServerBindingExpectation.Create(Origin, ServerBinding.Create("ca", "https://ca.stamps.example.test", "https://ca.stamps.example.test"));
        Assert.Throws<ServerBindingException>(() => ServerBindingPolicy.Evaluate(selected, ServerDiscoveryParser.Parse(Isolated), ServerBindingFloor.FromCompleteState([conflict]), provisioned));
        var legacy = new ServerInfoResponse { Product = ServerInfoResponse.ProductName, SignIn = new ServerSignIn { Microsoft = true } };
        var json = JsonSerializer.Serialize(legacy, CliJsonContext.Default.ServerInfoResponse);
        Assert.DoesNotContain("authority", json);
        Assert.DoesNotContain("machineSignature", json);
        Assert.Equal(1, ServerBindingPolicy.Evaluate(selected, ServerDiscoveryParser.Parse(json), NeverPinned, null).MachineSignatureVersion);
        Assert.Equal(new[] { "approvalAffectedV1", "settleAttemptV1" }, ServerDiscoveryParser.Parse(Isolated).Info.Capabilities);
        foreach (var malformed in new[] { "{}", "null", Expected.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
            Expected.Replace("\"authorityVersion\":1", "\"authorityVersion\":1,\"authorityVersion\":1", StringComparison.Ordinal) })
            Assert.Throws<ServerBindingException>(() => ServerBindingExpectation.Parse(malformed));
        Assert.Throws<ServerBindingException>(() => ServerDiscoveryParser.Parse("{broken"));
    }

    [Fact]
    public void Origin_normalization_keeps_exact_base_scope_and_requires_explicit_local_http_policy()
    {
        Assert.Equal(Origin, ProtocolOrigin.Normalize("HTTPS://EU.STAMPS.EXAMPLE.TEST:443/"));
        Assert.Equal("https://xn--bcher-kva.example.test", ProtocolOrigin.Normalize("https://bücher.example.test"));
        Assert.Equal("https://[::1]:8443", ProtocolOrigin.Normalize("https://[::1]:8443/"));
        var scope = ServerEndpoint.Parse("HTTPS://EU.STAMPS.EXAMPLE.TEST:443/gateway");
        Assert.Equal(scope, ServerEndpoint.Parse(Origin + "/gateway/"));
        Assert.NotEqual(scope, ServerEndpoint.Parse(Origin + "/other"));
        Assert.True(scope.Contains(new Uri(Origin + "/gateway/api/caller?x=%2F")));
        foreach (var uri in new[] { Origin + "/gateway-evil/api", Origin + "/other/api", Alias + "/gateway/api", Origin + "/gateway/../other/api",
            Origin + "/gateway/%2e%2e%2foutside", Origin + "/gateway/%252e%252e%252foutside", Origin + "/gateway/%5coutside" })
            Assert.False(scope.Contains(new Uri(uri)));
        Assert.Throws<ServerBindingException>(() => ServerEndpoint.Parse(Origin + "/gateway%2foutside"));
        foreach (var bad in new[] { "https://user@eu.stamps.example.test", Origin + "/a/..", Origin + "/?x=1", Origin + ".", "https://%65u.stamps.example.test", "https://eu.stamps.example.test\\evil", "https://[fe80::1%25eth0]", "https://eu.stamps.example.test:" })
            Assert.Throws<ArgumentException>(() => ProtocolOrigin.Normalize(bad));
        Assert.Throws<ServerBindingException>(() => ServerBinding.Create("eu", "http://localhost:1234", "http://localhost:1234"));
        Assert.Equal("http://localhost:1234", ServerBinding.Create("eu", "http://localhost:1234", "http://localhost:1234", allowLoopbackHttp: true).Issuer);
        Assert.Throws<ServerBindingException>(() => ServerBinding.Create("eu", "http://foreign.example.test", "http://foreign.example.test", allowLoopbackHttp: true));
    }
}
