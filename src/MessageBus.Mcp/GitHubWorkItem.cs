using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MessageBus.Contracts.Messaging;

namespace MessageBus.Mcp;

/// <summary>Best-effort issue context read using the starter's own GitHub CLI credentials.</summary>
internal static partial class GitHubWorkItem
{
    [GeneratedRegex(@"^gh:([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)#([0-9]+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex Key();

    internal static async Task<(string Title, string Body)?> ReadAsync(string workItem, TimeProvider time,
        Func<ProcessStartInfo, CancellationToken, Task<(int ExitCode, string Output)>> run, CancellationToken ct)
    {
        var match = Key().Match(workItem);
        if (!match.Success) return null;
        var start = new ProcessStartInfo("gh")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "issue", "view", match.Groups[2].Value, "--repo", "github.com/" + match.Groups[1].Value, "--json", "title,body" })
            start.ArgumentList.Add(arg);
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            var result = await run(start, linked.Token).ConfigureAwait(false);
            if (result.ExitCode != 0) return null;
            using var json = JsonDocument.Parse(result.Output);
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String
                || !json.RootElement.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.String)
                return null;
            var text = title.GetString()!.Trim();
            if (text.Length == 0) return null;
            return (Cut(text, MessageLimits.MaxTitleLength), Cut(body.GetString()!, 30_000));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // Local context is optional: a failed lookup must not prevent starting the job.
        catch (Exception) { return null; }
    }

    private static string Cut(string text, int limit) => text.Length <= limit ? text :
        text[..(char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit)];

    internal static async Task<(int ExitCode, string Output)> RunAsync(ProcessStartInfo start, CancellationToken ct)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Couldn't start gh.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            return (process.ExitCode, await stdout.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or AggregateException) { }
            throw;
        }
    }
}
