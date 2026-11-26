using System.Net;
using MessageBus.Cli.Runner;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class ActionInventoryTests
{
    [Fact]
    public async Task RescanReportsOnlyAvailableActionsWhenServiceSupportsWorkflows()
    {
        using var cache = new TemporaryBrowserCache();
        var nodeInstalled = true;
        await using var runner = new RunnerHarness(
            environment: new Dictionary<string, string> { ["PLAYWRIGHT_BROWSERS_PATH"] = cache.Path },
            locateCommand: command => command == "node" && nodeInstalled ? "node" : null);
        var workflows = false;
        runner.Service.OnRequest = path => path == "/api/server"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(workflows
                    ? """{"product":"messagebus.ai","capabilities":["workflows"]}"""
                    : """{"product":"messagebus.ai","capabilities":[]}"""),
            }
            : null;

        var browser = Directory.CreateDirectory(Path.Combine(cache.Path, "chromium-1234"));
        Assert.Empty(await ScanAsync());

        workflows = true;
        string[] allActions = ["action:open-pull-request", "action:record-walkthrough", "action:run-command"];
        string[] withoutBrowser = ["action:open-pull-request", "action:run-command"];
        Assert.Equal(allActions, await ScanAsync());
        Assert.Equal(allActions, await ScanAsync());

        nodeInstalled = false;
        Assert.Equal(withoutBrowser, await ScanAsync());
        nodeInstalled = true;
        browser.Delete();
        Assert.Equal(withoutBrowser, await ScanAsync());

        Directory.CreateDirectory(browser.FullName);
        workflows = false;
        Assert.Empty(await ScanAsync());

        async Task<string[]> ScanAsync()
        {
            await runner.Engine.RescanAsync(TestContext.Current.CancellationToken);
            var inventory = await runner.Service.NextInventoryAsync();
            return [.. inventory.Capabilities.Where(capability => capability.StartsWith("action:", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
        }
    }

    private sealed class TemporaryBrowserCache : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("mbus-playwright-").FullName;

        public void Dispose() => RunWorkspace.TryDelete(Path);
    }
}
