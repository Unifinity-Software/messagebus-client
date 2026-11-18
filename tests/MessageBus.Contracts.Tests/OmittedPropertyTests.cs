using Xunit;
using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Contracts.Json;

namespace MessageBus.Contracts.Tests;

public class OmittedPropertyTests
{
    public static TheoryData<Type> ContractTypes => [.. ContractSamples.All.Keys];

    /// <summary>
    /// A request body, MCP input or runner message that leaves out an optional property must get the
    /// property's declared default (<c>= []</c>, <c>= new()</c>, <c>= 5</c>), not null or zero: every
    /// feature reads these types straight off the wire and indexes into the collections.
    /// </summary>
    [Theory]
    [MemberData(nameof(ContractTypes))]
    public void Omitted_properties_keep_their_declared_defaults(Type type)
    {
        var info = MessageBusJson.Options.GetTypeInfo(type);
        var full = JsonNode.Parse(JsonSerializer.Serialize(ContractSamples.All[type], info))!.AsObject();
        var minimal = new JsonObject();
        foreach (var property in info.Properties.Where(p => p.IsRequired))
            minimal[property.Name] = full[property.Name]!.DeepClone();

        var parsed = JsonSerializer.Deserialize(minimal.ToJsonString(), info)!;
        var declared = Activator.CreateInstance(type)!;

        foreach (var property in info.Properties.Where(p => !p.IsRequired && p.Get is not null))
        {
            var expected = property.Get!(declared);
            var actual = property.Get(parsed);
            if (expected is null) continue;
            Assert.True(actual is not null, $"{type.Name}.{property.Name} came back null instead of its declared default");
            var same = expected is IEnumerable list and not string
                ? list.Cast<object>().SequenceEqual(((IEnumerable)actual!).Cast<object>())
                : Equals(expected, actual);
            Assert.True(same, $"{type.Name}.{property.Name} came back {actual} instead of its declared default {expected}");
        }
    }
}
