using System.Text.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;
using MessageBus.Mcp;

namespace MessageBus.Cli.Runner.Actions;

internal sealed class RecordWalkthroughAction(Func<string, string?> locateCommand,
    Func<string, string?>? getEnvironmentVariable = null, string? homeDirectory = null) : IRunnerAction
{
    private static readonly string[] ConfigExtensions = [".ts", ".js", ".mts", ".mjs", ".cts", ".cjs"];
    private const string PlaywrightCli = """
        const path = require('node:path');
        const cli = path.join(path.dirname(require.resolve('@playwright/test/package.json')), 'cli.js');
        process.argv.splice(1, 0, cli);
        require(cli);
        """;

    public string Kind => RunnerActionKinds.RecordWalkthrough;

    public bool IsAvailable()
    {
        if (locateCommand("node") is null) return false;

        var get = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var browsers = get("PLAYWRIGHT_BROWSERS_PATH");
        if (browsers == "0") return false; // Playwright installs into the individual checkout in this mode.
        if (string.IsNullOrEmpty(browsers))
        {
            var cache = OperatingSystem.IsWindows()
                ? get("LOCALAPPDATA") ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : OperatingSystem.IsMacOS()
                    ? Path.Combine(home, "Library", "Caches")
                    : get("XDG_CACHE_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".cache");
            browsers = Path.Combine(cache, "ms-playwright");
        }

        try
        {
            return Directory.Exists(browsers) && Directory.EnumerateDirectories(browsers).Any(directory =>
                Path.GetFileName(directory) is var name &&
                (name.StartsWith("chromium-", StringComparison.Ordinal)
                    || name.StartsWith("chromium_headless_shell-", StringComparison.Ordinal)
                    || name.StartsWith("firefox-", StringComparison.Ordinal)
                    || name.StartsWith("webkit-", StringComparison.Ordinal)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<SettleResult> RunAsync(RunnerActionContext context, CancellationToken cancellationToken)
    {
        var work = context.WorkDirectory;
        var config = ConfigExtensions.Select(extension => "playwright.config" + extension)
            .FirstOrDefault(name => File.Exists(Path.Combine(work, name)));
        if (config is null)
            return new() { Status = SettleStatus.Succeeded, Outcome = "nothing-to-record", Summary = "No Playwright configuration was found." };

        if (context.Exhibits is not { } exhibits)
            return Failed("The runner could not prepare walkthrough exhibit uploads.");

        if (locateCommand("node") is not { } node)
            return Failed("Node.js is required to run Playwright and was not found on the runner.");

        var run = context.RunProcessAsync ?? throw new InvalidOperationException("The action has no sandboxed process runner.");
        var id = Guid.NewGuid().ToString("N");
        // Keep the config beside the original so Playwright resolves its relative paths the same way.
        var recordingConfig = Path.Combine(work, $".mbus-walkthrough-{id}{Path.GetExtension(config)}");
        var scratch = Path.Combine(work, ".mbus", "walkthrough-" + id);
        if (!RunToolScope.IsInside(work, scratch))
            return Failed("The walkthrough output folder must be inside the checkout without symbolic links or junctions.");

        Directory.CreateDirectory(scratch);
        var output = Path.Combine(scratch, "results");
        var reportPath = Path.Combine(scratch, "report.json");
        (int ExitCode, IReadOnlyList<string> Tail) result;
        try
        {
            await File.WriteAllTextAsync(recordingConfig, RecordingConfig(config), cancellationToken).ConfigureAwait(false);
            result = await run(new ProcessLaunch
            {
                FileName = node,
                Arguments = ["--eval", PlaywrightCli, "--", "test", "--config", recordingConfig,
                    "--output", output, "--reporter", "line,json"],
                WorkingDirectory = work,
                Environment = new Dictionary<string, string?>
                {
                    ["PLAYWRIGHT_JSON_OUTPUT_FILE"] = reportPath,
                    ["PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD"] = "1",
                },
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            File.Delete(recordingConfig);
        }

        cancellationToken.ThrowIfCancellationRequested();
        JsonDocument? parsedReport = null;
        try
        {
            if (RunToolScope.IsInside(work, reportPath) && File.Exists(reportPath))
            {
                await using var reportFile = File.OpenRead(reportPath);
                parsedReport = await JsonDocument.ParseAsync(reportFile, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (JsonException)
        {
            return Failed("Playwright produced an invalid JSON report.", result.ExitCode);
        }

        using var report = parsedReport;
        var missingBrowser = result.ExitCode != 0 && (result.Tail.Any(IsMissingBrowser)
            || (report is not null && Errors(report.RootElement).Any(error => IsMissingBrowser(ReadString(error, "message") ?? ""))));
        if (report is null && !missingBrowser)
            return Failed("Playwright did not produce a test report. Check that the project's @playwright/test dependencies are installed and the configuration loads.", result.ExitCode);

        var summary = new List<string>
        {
            missingBrowser ? "The browser required by this Playwright project is not installed on the runner."
                : result.ExitCode == 0 ? "Playwright tests passed." : "Playwright tests failed. Available recordings were collected.",
        };
        var references = new List<Ref>();
        var reportedPaths = new HashSet<string>(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (report is not null)
        {
            foreach (var attachment in Attachments(report.RootElement))
            {
                var name = ReadString(attachment, "name");
                var contentType = ReadString(attachment, "contentType");
                var kind = contentType switch
                {
                    "image/png" or "image/jpeg" => "screenshot",
                    "video/webm" or "video/mp4" => "video",
                    "application/zip" when name == "trace" => "trace",
                    _ => null,
                };
                if (kind is null) continue;

                if (ReadString(attachment, "path") is { } path)
                {
                    await UploadAsync(path, name, kind).ConfigureAwait(false);
                }
                else if (kind == "screenshot" && ReadString(attachment, "body") is { } body)
                {
                    var extension = contentType == "image/png" ? ".png" : ".jpg";
                    path = Path.Combine(scratch, $"screenshot-{Guid.NewGuid():N}{extension}");
                    if (!RunToolScope.IsInside(work, path)) continue;
                    try
                    {
                        await File.WriteAllBytesAsync(path, Convert.FromBase64String(body), cancellationToken).ConfigureAwait(false);
                        await UploadAsync(path, name, kind).ConfigureAwait(false);
                    }
                    catch (FormatException)
                    {
                        summary.Add("Playwright reported a screenshot with invalid image data.");
                    }
                }
            }
        }

        // page.screenshot({ path: testInfo.outputPath(...) }) writes a file without adding a report attachment.
        if (RunToolScope.IsInside(work, output) && Directory.Exists(output))
        {
            foreach (var file in Directory.EnumerateFiles(output, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
            {
                if (Path.GetExtension(file).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg")
                    await UploadAsync(file, Path.GetFileName(file), "screenshot", skipSeen: true).ConfigureAwait(false);
            }
        }

        return new()
        {
            Status = missingBrowser ? SettleStatus.Failed : SettleStatus.Succeeded,
            Outcome = missingBrowser ? null : result.ExitCode == 0 ? "recorded" : "tests-failed",
            Error = missingBrowser ? summary[0] : null,
            Summary = string.Join('\n', summary),
            ExitCode = result.ExitCode,
            Refs = references,
        };

        async Task UploadAsync(string path, string? caption, string kind, bool skipSeen = false)
        {
            try
            {
                var full = Path.GetFullPath(path, work);
                if (skipSeen && reportedPaths.Contains(full)) return;
                reportedPaths.Add(full);
                references.Add(await exhibits.UploadAsync(full, caption, kind, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is McpToolException or IOException or HttpRequestException or JsonException or ArgumentException)
            {
                summary.Add($"Exhibit {Path.GetFileName(path)}: {ex.Message.ReplaceLineEndings(" ")}");
            }
        }
    }

    private static string RecordingConfig(string config) => $$"""
        import original from './{{config}}';
        const recording = { video: 'on', screenshot: 'on', trace: 'on' };
        export default {
            ...original,
            preserveOutput: 'always',
            use: { ...original.use, ...recording },
            projects: original.projects?.map(project => ({
                ...project,
                use: { ...project.use, ...recording },
            })),
        };
        """;

    private static bool IsMissingBrowser(string message) =>
        message.Contains("Executable doesn't exist at", StringComparison.OrdinalIgnoreCase)
        || message.Contains("Run `npx playwright install", StringComparison.OrdinalIgnoreCase);

    private static SettleResult Failed(string message, int? exitCode = null) =>
        new() { Status = SettleStatus.Failed, Error = message, ExitCode = exitCode };

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static IEnumerable<JsonElement> Items(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    private static IEnumerable<JsonElement> Attachments(JsonElement report) =>
        Results(report).SelectMany(result => Items(result, "attachments"));

    private static IEnumerable<JsonElement> Errors(JsonElement report) =>
        Items(report, "errors").Concat(Results(report).SelectMany(result => Items(result, "errors")));

    private static IEnumerable<JsonElement> Results(JsonElement report) => Items(report, "suites").SelectMany(SuiteResults);

    private static IEnumerable<JsonElement> SuiteResults(JsonElement suite) =>
        Items(suite, "specs").SelectMany(spec => Items(spec, "tests"))
            .SelectMany(test => Items(test, "results"))
            .Concat(Items(suite, "suites").SelectMany(SuiteResults));
}
