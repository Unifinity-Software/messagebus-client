using Xunit;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;

namespace MessageBus.Contracts.Tests;

public class JsonContractTests
{
    private static readonly Assembly Contracts = typeof(MessageBusJson).Assembly;

    public static TheoryData<Type> ContractTypes => [.. ContractSamples.All.Keys];

    [Fact]
    public void Every_public_contract_type_has_a_sample()
    {
        var types = Contracts.GetExportedTypes()
            .Where(t => t.IsClass && !t.IsAbstract && !typeof(JsonSerializerContext).IsAssignableFrom(t))
            // The catalog's runtime descriptions and exceptions are not JSON wire contracts.
            .Where(t => t != typeof(McpToolSpec) && t != typeof(McpToolException))
            .Select(t => t.FullName);

        Assert.Empty(types.Except(ContractSamples.All.Keys.Select(t => t.FullName)));
    }

    [Theory]
    [MemberData(nameof(ContractTypes))]
    public void Round_trips_with_every_property_populated(Type type)
    {
        var info = MessageBusJson.Options.GetTypeInfo(type);
        var json = JsonSerializer.Serialize(ContractSamples.All[type], info);

        var node = JsonNode.Parse(json)!.AsObject();
        var missing = info.Properties.Select(p => p.Name).Where(name => !node.ContainsKey(name));
        Assert.Empty(missing);
        AssertNothingEmpty(node, type.Name);

        var back = JsonSerializer.Deserialize(json, info);
        Assert.IsType(type, back);
        Assert.Equal(json, JsonSerializer.Serialize(back, info));
    }

    [Fact]
    public void Older_heartbeat_ack_does_not_enable_resource_samples()
    {
        var ack = MessageBusJson.Deserialize<HeartbeatAck>("""{"leases":[],"runnerConnected":true}""")!;
        Assert.False(ack.SupportsResourceSamples);
        Assert.DoesNotContain("supportsResourceSamples", MessageBusJson.Serialize(ack));
    }

    [Fact]
    public void Heartbeat_distinguishes_missing_metrics_from_measured_zero()
    {
        var heartbeat = MessageBusJson.Deserialize<RunnerHeartbeat>("""
            {
              "slots": 2,
              "slotsBusy": 0,
              "resources": {
                "schemaVersion": 1,
                "sequence": 3,
                "sampledAt": "2026-01-01T00:00:00Z",
                "status": "partial",
                "cpu": { "utilizationPercent": null },
                "memory": { "availableBytes": 0 }
              }
            }
            """)!;
        var resources = Assert.IsType<RunnerResourceSample>(heartbeat.Resources);
        Assert.Null(resources.Cpu?.UtilizationPercent);
        Assert.Null(resources.WindowMs);
        Assert.Equal(0L, resources.Memory?.AvailableBytes);

        var root = JsonNode.Parse(MessageBusJson.Serialize(heartbeat))!["resources"]!;
        Assert.False(root["cpu"]!.AsObject().ContainsKey("utilizationPercent"));
        Assert.Equal(0L, root["memory"]!["availableBytes"]!.GetValue<long>());
    }

    [Fact]
    public void Enums_are_written_as_lowercase_hyphenated_names_and_read_back()
    {
        var pattern = new Regex("^[a-z0-9]+(-[a-z0-9]+)*$");
        foreach (var enumType in Contracts.GetExportedTypes().Where(t => t.IsEnum))
        {
            var info = MessageBusJson.Options.GetTypeInfo(enumType);
            var names = new HashSet<string>();
            foreach (var value in Enum.GetValues(enumType))
            {
                var json = JsonSerializer.Serialize(value, info);
                var name = JsonSerializer.Deserialize<string>(json)!;
                Assert.Matches(pattern, name);
                Assert.True(names.Add(name), $"{enumType.Name}.{value} repeats '{name}'");
                Assert.Equal(value, JsonSerializer.Deserialize(json, info));
            }
        }
    }

    [Theory]
    [InlineData(ThreadStatus.DeadLettered, "dead-lettered")]
    [InlineData(ThreadStatus.WaitingOnJoin, "waiting-on-join")]
    [InlineData(Expect.Reply, "reply")]
    [InlineData(Expect.Outcome, "outcome")]
    [InlineData(DeliveryMode.FirstN, "first-n")]
    [InlineData(DeliveryMode.RequestReply, "request-reply")]
    [InlineData(SettleStatus.Abandoned, "abandoned")]
    [InlineData(AgentKind.ClaudeCode, "claude-code")]
    [InlineData(MachineOs.MacOs, "macos")]
    [InlineData(CodexWindowsSandbox.Sandbox, "sandbox")]
    [InlineData(CodexWindowsSandbox.SameAsOtherAgents, "same-as-other-agents")]
    public void Enum_values_that_clients_send_keep_their_wire_names(object value, string expected)
    {
        var json = JsonSerializer.Serialize(value, MessageBusJson.Options.GetTypeInfo(value.GetType()));
        Assert.Equal($"\"{expected}\"", json);
    }

    [Fact]
    public void Targets_carry_a_kind_discriminator_that_may_come_anywhere_in_the_object()
    {
        Assert.Equal("""{"kind":"role","role":"developer"}""", MessageBusJson.Serialize<MessageTarget>(new RoleTarget { Role = "developer" }));
        Assert.Equal("""{"kind":"capabilities","capabilities":["os:macos"]}""",
            MessageBusJson.Serialize<MessageTarget>(new CapabilityTarget { Capabilities = ["os:macos"] }));
        Assert.Equal("""{"kind":"agent","agentId":"agt_1"}""", MessageBusJson.Serialize<MessageTarget>(new AgentTarget { AgentId = "agt_1" }));
        Assert.Equal("""{"kind":"inbox","inboxId":"inb_1"}""", MessageBusJson.Serialize<MessageTarget>(new InboxTarget { InboxId = "inb_1" }));

        var parsed = MessageBusJson.Deserialize<MessageTarget>("""{"role":"tester","kind":"role"}""");
        Assert.Equal(new RoleTarget { Role = "tester" }, parsed);
    }

    [Fact]
    public void Thread_events_carry_a_kind_discriminator()
    {
        var settled = (ThreadEvent)ContractSamples.All[typeof(ThreadSettled)];

        var json = MessageBusJson.Serialize(settled);

        Assert.StartsWith("""{"kind":"settled",""", json);
        var back = MessageBusJson.Deserialize<ThreadEvent>(json);
        Assert.Equal(SettleStatus.Succeeded, Assert.IsType<ThreadSettled>(back).Result.Status);
    }

    [Fact]
    public void Envelope_field_names_are_the_camel_case_wire_names()
    {
        var node = JsonNode.Parse(MessageBusJson.Serialize(ContractSamples.Envelope))!.AsObject();

        string[] expected =
        [
            "specVersion", "id", "type", "source", "time", "workspaceId", "target", "replyTo", "threadId", "parentId",
            "projectId", "title", "labels", "refs", "expect", "priority", "dedupeKey", "expiresAt", "hopCount", "trace",
            "sender", "body", "attachments", "encryption", "excludeAgents", "model", "verification",
        ];
        Assert.Equal(expected, node.Select(p => p.Key));
        Assert.Equal(["algorithm", "keyWrapAlgorithm", "keyId", "wrappedKey", "nonce", "ephemeralPublicKey"],
            node["encryption"]!.AsObject().Select(p => p.Key));
        Assert.Equal("traceParent", node["trace"]!.AsObject().First().Key);
    }

    [Fact]
    public void Verification_and_current_friction_are_independent_of_completion_and_friction_history()
    {
        var json = MessageBusJson.Serialize(ContractSamples.Thread with { CurrentFrictionKinds = [] });
        var thread = MessageBusJson.Deserialize<ThreadSummary>(json)!;

        Assert.Equal(SettleStatus.Succeeded, thread.Result!.Status);
        Assert.Equal(VerificationState.Failed, thread.Verification!.State);
        Assert.Empty(thread.CurrentFrictionKinds!);
        Assert.Equal(["missing-context"], thread.Result.FrictionKinds);

        var legacy = MessageBusJson.Deserialize<ThreadSummary>(
            MessageBusJson.Serialize(thread with { Verification = null, CurrentFrictionKinds = null }))!;
        Assert.Null(legacy.Verification);
        Assert.Null(legacy.CurrentFrictionKinds);
    }

    [Fact]
    public void Agent_settlement_has_no_verification_claim_on_the_wire()
    {
        var input = MessageBusJson.Deserialize<SettleInput>("""
            {"threadId":"thr_test","result":{"status":"succeeded","verification":{"state":"passed","environment":"frontend"}},
             "verification":{"state":"passed","environment":"frontend"}}
            """)!;
        var serialized = MessageBusJson.Serialize(input);

        Assert.DoesNotContain("verification", serialized);
        Assert.Equal(SettleStatus.Succeeded, input.Result.Status);
    }

    [Fact]
    public void Options_configured_for_aspnet_write_the_same_json_as_the_shared_options()
    {
        var aspnet = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        MessageBusJson.Configure(aspnet);

        foreach (var sample in new object[] { ContractSamples.Envelope, ContractSamples.Thread, ContractSamples.Role })
        {
            Assert.Equal(
                JsonSerializer.Serialize(sample, MessageBusJson.Options.GetTypeInfo(sample.GetType())),
                JsonSerializer.Serialize(sample, sample.GetType(), aspnet));
        }
    }

    private static void AssertNothingEmpty(JsonNode? node, string path)
    {
        switch (node)
        {
            case null:
                Assert.Fail($"{path} is null");
                break;
            case JsonObject obj:
                Assert.True(obj.Count > 0, $"{path} is an empty object");
                foreach (var (key, value) in obj) AssertNothingEmpty(value, $"{path}.{key}");
                break;
            case JsonArray array:
                Assert.True(array.Count > 0, $"{path} is an empty array");
                for (var i = 0; i < array.Count; i++) AssertNothingEmpty(array[i], $"{path}[{i}]");
                break;
        }
    }
}
