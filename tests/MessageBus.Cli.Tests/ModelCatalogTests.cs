using MessageBus.Cli.Agents;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Machines;
using Xunit;

namespace MessageBus.Cli.Tests;

/// <summary>The models each agent CLI reports (the original issue), parsed from what the CLIs wrote, cut down to the fields read.</summary>
public sealed class ModelCatalogTests
{
    /// <summary><c>codex debug models</c> from codex-cli 0.169.0 (first entries; every model has many more fields).</summary>
    private const string CodexDebugModels = """
        {
          "models": [
            { "slug": "gpt-6-astra", "display_name": "GPT-6-Astra", "visibility": "list", "priority": 2, "supported_in_api": true },
            { "slug": "gpt-6.1-sol", "display_name": "GPT-6.1-Sol", "description": "Latest workhorse model for coding and everyday work.",
              "default_reasoning_level": "low", "visibility": "list", "priority": 1, "supported_in_api": true },
            { "slug": "gpt-reserve", "display_name": "GPT-Reserve", "visibility": "hide", "priority": 5, "supported_in_api": true },
            { "slug": "gpt-5.6-luna", "display_name": "GPT-5.6-Luna", "visibility": "list", "priority": 9, "supported_in_api": true },
            { "slug": "codex-auto-review", "display_name": "Codex Auto Review", "visibility": "hide", "priority": 10, "supported_in_api": true }
          ]
        }
        """;

    /// <summary>Claude Code 2.1.325's cached model catalog (<c>~/.claude/cache/model-catalog/…-cc.json</c>), cut down.</summary>
    private const string ClaudeCatalog = """
        {"version":2,"fetchedAt":1794778222303,"staleAt":1794781742125,"catalog":{"surface":"cc","config":{"id":"cc","models":[
          {"id":"claude-opus-5-5","name":"Opus 5.5","short_name":"Opus","section":"main","min_claude_code_version":"2.1.280"},
          {"id":"claude-fable-5-2","name":"Fable 5.2","short_name":"Fable","section":"main","min_claude_code_version":"2.1.290"},
          {"id":"claude-sonnet-5-5","name":"Sonnet 5.5","section":"main"},
          {"id":"claude-opus-4-6","name":"Opus 4.6","section":"overflow"}],
          "provider_alias_targets":{"fable":{"default":"claude-fable-5-2","per_provider":{"gateway":"claude-fable-5"}},
            "opus":{"default":"claude-opus-5-5","per_provider":{"foundry":"claude-opus-4-6"}}}},"state":{}}}
        """;

    [Fact]
    public void Codex_reports_the_models_its_picker_lists_in_its_order()
    {
        Assert.Equal(["gpt-6.1-sol", "gpt-6-astra", "gpt-5.6-luna"], ModelCatalog.ParseCodexModels(CodexDebugModels));
        // An older Codex without the command prints its usage instead: no list, so the report is partial.
        Assert.Null(ModelCatalog.ParseCodexModels("error: unrecognized subcommand 'models'"));
    }

    [Fact]
    public void Claude_code_reports_the_catalog_models_its_version_runs_and_resolves_an_alias_to_one()
    {
        var catalog = ModelCatalog.ParseClaudeCatalog(ClaudeCatalog, "2.1.285")!;
        Assert.Equal(["claude-opus-5-5", "claude-sonnet-5-5", "claude-opus-4-6"], catalog.Models); // Fable needs 2.1.290
        Assert.Equal("claude-fable-5-2[1m]", ModelCatalog.ResolveClaudeAlias("fable[1m]", catalog.Aliases));
        Assert.Equal("claude-opus-4-8", ModelCatalog.ResolveClaudeAlias("claude-opus-4-8", catalog.Aliases));
        // Claude Desktop's catalog sits beside it; only the CLI's counts.
        Assert.Null(ModelCatalog.ParseClaudeCatalog(ClaudeCatalog.Replace("\"surface\":\"cc\"", "\"surface\":\"ccd\"", StringComparison.Ordinal), "2.1.325"));
    }

    [Fact]
    public void A_broken_claude_catalog_reads_as_none()
    {
        // A fetchedAt past what a date can hold, a duplicated key, and plain garbage: no catalog, never an exception.
        Assert.Null(ModelCatalog.ParseClaudeCatalog(ClaudeCatalog.Replace("1794778222303", "99999999999999999", StringComparison.Ordinal), null));
        Assert.Null(ModelCatalog.ParseClaudeCatalog(ClaudeCatalog.Replace("{\"version\":2,", "{\"catalog\":1,", StringComparison.Ordinal), null));
        Assert.Null(ModelCatalog.ParseClaudeCatalog("{\"catalog\":[1,2", null));
    }

    [Fact]
    public async Task Reading_models_that_throws_reports_them_unknown_so_the_scan_goes_on()
    {
        using var machine = new TestMachine();
        var env = new SignInEnvironment(_ => throw new InvalidOperationException("broken"), new Dictionary<string, string>());
        var read = await ModelCatalog.ReadAsync(AgentKind.ClaudeCode, "claude", "2.1.325", new AgentLocations(machine.CreateHost()), env,
            TestContext.Current.CancellationToken);
        Assert.Same(AgentModels.Unknown, read);
    }
}
