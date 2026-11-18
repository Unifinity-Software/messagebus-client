using System.Text.Json;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner.Agents;

internal sealed class CapacityReadings(string kind, TimeProvider time)
{
    private readonly Dictionary<string, CapacityWindow> _windows = new();
    private CapacityLimitHit? _hit;
    public AgentCapacityReport? Report => _windows.Count == 0 && _hit is null ? null : new()
    {
        AgentKind = kind, Windows = _windows.Values.ToArray(), LimitHit = _hit,
    };

    public void Hit(DateTimeOffset? at = null, DateTimeOffset? reset = null, string? window = null) =>
        _hit = new() { At = at ?? time.GetUtcNow(), ResetsAt = reset, Window = window };

    public void Claude(JsonElement info)
    {
        var at = time.GetUtcNow();
        if (info.TryGetProperty("unifiedWindows", out var windows) && windows.ValueKind == JsonValueKind.Object)
        {
            foreach (var window in windows.EnumerateObject()) Read(window.Name, window.Value);
        }
        else if (ClaudeOutputParser.Str(info, "rateLimitType") is { } name) Read(name, info);

        void Read(string name, JsonElement value)
        {
            var window = name switch
            {
                "five_hour" => CapacityNames.FiveHour, "seven_day" => CapacityNames.Weekly,
                "seven_day_opus" => CapacityNames.WeeklyOpus, "seven_day_sonnet" => CapacityNames.WeeklySonnet,
                _ => null,
            };
            if (window is null || value.ValueKind != JsonValueKind.Object) return;
            var status = ClaudeOutputParser.Str(value, "status") switch
            {
                "allowed" => CapacityNames.Ok, "allowed_warning" => CapacityNames.Warning,
                "rejected" => CapacityNames.Limited, _ => null,
            };
            var reset = Unix(value, "resetsAt");
            _windows[window] = new() { Window = window, UsedPercent = Number(value, "utilization") is { } used ? Math.Clamp(used * 100, 0, 100) : null,
                Status = status, ResetsAt = reset, MeasuredAt = at };
            if (status == CapacityNames.Limited) Hit(at, reset, window);
        }
    }

    public void Codex(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        var payload = root.TryGetProperty("payload", out var p) ? p : root.TryGetProperty("msg", out var m) ? m : root;
        if (payload.ValueKind != JsonValueKind.Object || ClaudeOutputParser.Str(payload, "type") != "token_count"
            || !payload.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object) return;
        var at = DateTimeOffset.TryParse(ClaudeOutputParser.Str(root, "timestamp"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var stamp) ? stamp : time.GetUtcNow();
        var limited = limits.TryGetProperty("rate_limit_reached_type", out var reached) && reached.ValueKind != JsonValueKind.Null;
        var reachedName = limited && reached.ValueKind == JsonValueKind.String ? reached.GetString() : null;
        DateTimeOffset? reset = null;
        string? hitWindow = null;
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!limits.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) continue;
            var minutes = Number(value, "window_minutes") is { } n && n > 0 && n <= int.MaxValue ? (int?)n : null;
            var window = minutes switch { 300 => CapacityNames.FiveHour, 10080 => CapacityNames.Weekly, _ => name };
            var resets = Unix(value, "resets_at");
            if (resets is null && Number(value, "resets_in_seconds") is { } seconds && seconds is >= 0 and <= 315360000) resets = at.AddSeconds(seconds);
            var isHit = reachedName == name || reachedName == window || reachedName == (window == CapacityNames.FiveHour ? "five_hour" : window == CapacityNames.Weekly ? "seven_day" : window);
            if (isHit) { reset = resets; hitWindow = window; }
            var reading = new CapacityWindow { Window = window, WindowMinutes = minutes,
                UsedPercent = Number(value, "used_percent") is { } used ? Math.Clamp(used, 0, 100) : null,
                Status = isHit ? CapacityNames.Limited : null, ResetsAt = resets, MeasuredAt = at };
            if (!_windows.TryGetValue(window, out var old) || old.MeasuredAt <= at) _windows[window] = reading;
        }
        if (limited && (_hit is null || _hit.At <= at)) Hit(at, reset, hitWindow);
    }

    private static double? Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;
    private static DateTimeOffset? Unix(JsonElement e, string name) => Number(e, name) is { } n && n is >= -62135596800 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds((long)n) : null;
}
