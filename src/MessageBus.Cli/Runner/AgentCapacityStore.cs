using System.Text.Json;
using System.Text.Json.Serialization;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner;

/// <summary>Locally observed allowance windows; no account details or error text.</summary>
public sealed class AgentCapacityStore(string directory, Action<string, string> write)
{
    private static readonly object Gate = new();
    private string FilePath => Path.Combine(directory, "agent-capacity.json");

    /// <summary>Reads without contacting the service.</summary>
    public IReadOnlyList<AgentCapacityReport> Load()
    {
        lock (Gate)
        {
            try
            {
                return (JsonSerializer.Deserialize(File.ReadAllText(FilePath), CapacityJsonContext.Default.ListAgentCapacityReport) ?? [])
                    .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.AgentKind))
                    .GroupBy(r => r.AgentKind).Select(g => new AgentCapacityReport
                    {
                        AgentKind = g.Key,
                        Windows = g.SelectMany(r => r.Windows ?? []).Where(w => w is not null && !string.IsNullOrWhiteSpace(w.Window))
                            .GroupBy(w => w.Window).Select(w => w.MaxBy(v => v.MeasuredAt)!).ToArray(),
                        LimitHit = g.Select(r => r.LimitHit).Where(h => h is not null).MaxBy(h => h!.At),
                    }).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
        }
    }

    /// <summary>Merges only newer readings, preserving each window's observation time.</summary>
    public AgentCapacityReport Merge(AgentCapacityReport reading)
    {
        lock (Gate)
        {
            var all = Load().ToDictionary(r => r.AgentKind);
            all.TryGetValue(reading.AgentKind, out var previous);
            var windows = (previous?.Windows ?? []).ToDictionary(w => w.Window);
            foreach (var window in reading.Windows)
                if (!windows.TryGetValue(window.Window, out var old) || old.MeasuredAt <= window.MeasuredAt) windows[window.Window] = window;
            var hit = reading.LimitHit;
            if (previous?.LimitHit is { } prior && (hit is null || prior.At > hit.At)) hit = prior;
            var merged = reading with { Windows = windows.Values.ToArray(), LimitHit = hit };
            all[reading.AgentKind] = merged;
            write(FilePath, JsonSerializer.Serialize(all.Values.ToList(), CapacityJsonContext.Default.ListAgentCapacityReport));
            return merged;
        }
    }

    /// <summary>Plain-language offline diagnostics.</summary>
    public IEnumerable<string> Describe(DateTimeOffset now)
    {
        var all = Load();
        foreach (var kind in AgentKinds.Launchable)
        {
            var name = AgentKinds.DisplayName(kind);
            var report = all.FirstOrDefault(r => r.AgentKind == AgentKinds.Name(kind));
            if (report is null || report.Windows.Count == 0)
            {
                yield return $"{name}: usage limits unknown (none reported yet)";
                continue;
            }
            foreach (var window in report.Windows)
            {
                var used = window.UsedPercent is { } percent ? $"{percent:0.#}% used" : window.Status ?? "usage unknown";
                var reset = window.ResetsAt is { } at ? at > now ? $", resets in {Duration(at - now)}" : ", reset time passed" : ", reset time unknown";
                yield return $"{name}: {window.Window} {used}{reset} (checked {Duration(now > window.MeasuredAt ? now - window.MeasuredAt : TimeSpan.Zero)} ago)";
            }
        }
    }

    private static string Duration(TimeSpan span) => span.TotalDays >= 1 ? $"{(int)span.TotalDays} days" : span.TotalHours >= 1 ? $"{(int)span.TotalHours} h" : $"{(int)span.TotalMinutes} min";
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(List<AgentCapacityReport>))]
[JsonSerializable(typeof(AgentCapacityReport))]
internal partial class CapacityJsonContext : JsonSerializerContext;
