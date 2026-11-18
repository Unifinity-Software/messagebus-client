using Xunit;
using System.Reflection;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Projects;
using MessageBus.Contracts.Security;

namespace MessageBus.Contracts.Tests;

public class NameAndKeyTests
{
    [Theory]
    [InlineData(typeof(McpToolNames))]
    [InlineData(typeof(PermissionNames))]
    [InlineData(typeof(ApiKeyScopes))]
    public void Every_constant_is_listed_in_All_once(Type catalog)
    {
        var constants = catalog.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet();
        var all = (IReadOnlyList<string>)catalog.GetProperty("All")!.GetValue(null)!;

        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(constants.Order(), all.Order());
    }

    [Theory]
    [InlineData("role:developer", typeof(RoleTarget))]
    [InlineData("agent:agt_01", typeof(AgentTarget))]
    [InlineData("inbox:inb_01", typeof(InboxTarget))]
    [InlineData("capabilities:os:macos,tool:dotnet", typeof(CapabilityTarget))]
    public void Target_text_parses_and_formats_back(string text, Type expected)
    {
        Assert.True(MessageTarget.TryParse(text, out var target));
        Assert.IsType(expected, target);
        Assert.Equal(text, target.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("developer")]
    [InlineData("role:")]
    [InlineData("role:a:b")]
    [InlineData("team:developer")]
    [InlineData("capabilities:,")]
    public void Malformed_target_text_is_rejected(string text) =>
        Assert.False(MessageTarget.TryParse(text, out _));

    [Theory]
    [InlineData("ado:contoso/web#123", "ado", "contoso/web", "123")]
    [InlineData("GH:acme/todo#42", "gh", "acme/todo", "42")]
    [InlineData("gh:acme/todo", "gh", "acme/todo", null)]
    [InlineData("file:samples/work-items/add-due-dates.md", "file", "samples/work-items/add-due-dates.md", null)]
    public void Source_refs_parse_into_a_canonical_key(string text, string system, string path, string? item)
    {
        Assert.True(SourceRef.TryParse(text, out var source));
        Assert.Equal((system, path, item), (source.System, source.Path, source.Item));
        Assert.True(SourceRef.TryParse(source.ToString(), out var again));
        Assert.Equal(source, again);
    }

    [Theory]
    [InlineData("ado:contoso#1")]
    [InlineData("gh:acme/todo#abc")]
    [InlineData("gh:acme//todo")]
    [InlineData("ado:a/b#")]
    [InlineData(":x")]
    [InlineData("1x:y")]
    [InlineData("file:has space.md")]
    public void Malformed_source_refs_are_rejected(string text) =>
        Assert.False(SourceRef.TryParse(text, out _));

    [Fact]
    public void Ids_are_well_formed_and_sort_by_time()
    {
        var t = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        var earlier = ContractIds.New(ContractIds.Message, t);
        var later = ContractIds.New(ContractIds.Message, t.AddMilliseconds(1));

        Assert.True(ContractIds.IsValid(earlier, ContractIds.Message));
        Assert.False(ContractIds.IsValid(earlier, ContractIds.Thread));
        Assert.True(string.CompareOrdinal(earlier, later) < 0);
    }
}
