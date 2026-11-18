using System.Net;
using System.Text.Json;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Contracts.Runner;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

public sealed class AgentCapacityTests : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mbus-capacity-" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() => RunWorkspace.TryDelete(_folder);
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-16T23:51:55.398Z");

    [Fact]
    public void Claude_merges_recorded_events_without_restamping_and_accepts_status_only()
    {
        var time = new FakeTimeProvider(At);
        var parser = new ClaudeOutputParser(time);
        parser.Observe("""{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1795467600,"rateLimitType":"seven_day","overageStatus":"rejected","overageDisabledReason":"org_level_disabled","isUsingOverage":false,"unifiedWindows":{"seven_day":{"utilization":0.09,"resetsAt":1795467600,"status":"allowed"}}},"uuid":"<uuid>","session_id":"<sid>"}""");
        time.Advance(TimeSpan.FromHours(2));
        parser.Observe("""{"type":"rate_limit_event","rate_limit_info":{"unifiedWindows":{"five_hour":{"status":"allowed_warning","resetsAt":1795467600}}}}""");
        var windows = parser.Capacity!.Windows;
        var weekly = Assert.Single(windows, w => w.Window == "weekly");
        Assert.Equal(9, weekly.UsedPercent);
        Assert.Equal(At, weekly.MeasuredAt);
        Assert.Equal("ok", weekly.Status);
        var shortWindow = Assert.Single(windows, w => w.Window == "five-hour");
        Assert.Null(shortWindow.UsedPercent);
        Assert.Equal("warning", shortWindow.Status);
        Assert.Equal(time.GetUtcNow(), shortWindow.MeasuredAt);
        parser.Observe("""{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","rateLimitType":"five_hour","resetsAt":1795467600}}""");
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1795467600), parser.Capacity!.LimitHit!.ResetsAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Codex_reads_session_limits_even_with_explicit_model_and_anchors_relative_resets(bool relative)
    {
        var folder = _folder;
        var day = Path.Combine(folder, "sessions", "2026", "11", "16");
        Directory.CreateDirectory(day);
        var line = """{"timestamp":"2026-11-16T23:51:55.398Z","ordinal":16,"type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":23374,"cached_input_tokens":13568,"output_tokens":5,"total_tokens":23379},"model_context_window":475000},"rate_limits":{"limit_id":"codex","limit_name":null,"primary":{"used_percent":16.0,"window_minutes":10080,"resets_at":1795201039},"secondary":null,"credits":{"has_credits":false,"unlimited":false,"balance":"0"},"individual_limit":null,"spend_control_reached":null,"plan_type":"<never store>","rate_limit_reached_type":null}}}""";
        if (relative) line = line.Replace("\"resets_at\":1795201039", "\"resets_in_seconds\":3600");
        File.WriteAllLines(Path.Combine(day, "rollout-test-thread.jsonl"), [line, "{unfinished"]);
        var parser = new CodexOutputParser("configured", folder, new FakeTimeProvider(At));
        parser.Observe("""{"type":"thread.started","thread_id":"thread","model":"explicit"}""");
        Assert.Equal("explicit", parser.Complete().Usage!.Model);
        File.WriteAllText(Path.Combine(day, "rollout-test-thread.jsonl"), "");
        parser.Complete(); // The session log was read once, even when it held no model.
        var window = Assert.Single(parser.Capacity!.Windows);
        Assert.Equal("weekly", window.Window);
        Assert.Equal(16, window.UsedPercent);
        Assert.Equal(At, window.MeasuredAt);
        Assert.Equal(relative ? At.AddHours(1) : DateTimeOffset.FromUnixTimeSeconds(1795201039), window.ResetsAt);
    }

    [Fact]
    public void Limit_failure_never_extracts_reset_from_error_prose()
    {
        var parser = new CodexOutputParser(null, time: new FakeTimeProvider(At));
        parser.Observe("""{"type":"turn.failed","error":{"message":"You've hit your usage limit. Try again at 8 PM."}}""");
        Assert.Equal(At, parser.Capacity!.LimitHit!.At);
        Assert.Null(parser.Capacity.LimitHit.ResetsAt);
        Assert.Empty(parser.Capacity.Windows);
    }

    [Theory]
    [InlineData("primary", "five-hour")]
    [InlineData("five_hour", "five-hour")]
    [InlineData("secondary", "weekly")]
    [InlineData("weekly", "weekly")]
    [InlineData("unknown", null)]
    public void Structured_codex_limit_hit_uses_only_window_data(string reached, string? expectedWindow)
    {
        var parser = new CodexOutputParser(null, time: new FakeTimeProvider(At));
        parser.Observe("""{"timestamp":"2026-11-16T23:51:55.398Z","msg":{"type":"token_count","rate_limits":{"primary":{"used_percent":100,"window_minutes":300,"resets_in_seconds":3600},"secondary":{"used_percent":10,"window_minutes":10080,"resets_in_seconds":604800},"rate_limit_reached_type":"REACHED","plan_type":"never store"}}}""".Replace("REACHED", reached));
        Assert.Equal(expectedWindow, parser.Capacity!.LimitHit!.Window);
        Assert.Equal(expectedWindow is null ? null : expectedWindow == "weekly" ? At.AddDays(7) : At.AddHours(1), parser.Capacity.LimitHit.ResetsAt);
        foreach (var window in parser.Capacity.Windows)
            Assert.Equal(window.Window == expectedWindow ? "limited" : null, window.Status);
    }

    [Fact]
    public void Rate_limit_transport_error_is_not_an_account_usage_limit()
    {
        var parser = new CodexOutputParser(null, time: new FakeTimeProvider(At));
        parser.Observe("""{"type":"turn.failed","error":{"message":"rate limit exceeded"}}""");
        Assert.Null(parser.Capacity);
    }

    [Fact]
    public void Hand_edited_store_tolerates_null_and_duplicate_entries()
    {
        File.WriteAllText(Path.Combine(_folder, "agent-capacity.json"), """[null,{"agentKind":"codex","windows":[null,{"window":"weekly","usedPercent":5,"measuredAt":"2026-11-16T00:00:00Z"},{"window":"weekly","usedPercent":10,"measuredAt":"2026-11-17T00:00:00Z"}]},{"agentKind":"codex","windows":null}]""");
        var store = new ConfigStore(_folder, "file").AgentCapacity;
        var merged = store.Merge(new() { AgentKind = "codex" });
        Assert.Equal(10, Assert.Single(merged.Windows).UsedPercent);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Capacity_transport_uses_contract_route_and_only_ignores_404(HttpStatusCode status)
    {
        using var handler = new CapacityHandler(status);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var client = new HttpRunnerServiceClient(http);
        var report = new AgentCapacityReport { AgentKind = "codex", Windows = [new() { Window = "weekly", UsedPercent = 16, MeasuredAt = At }] };
        if (status == HttpStatusCode.InternalServerError)
            await Assert.ThrowsAsync<HttpRequestException>(() => client.CapacityAsync(report, TestContext.Current.CancellationToken));
        else
            await client.CapacityAsync(report, TestContext.Current.CancellationToken);
        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal(ServiceRoutes.AgentCapacity, handler.Path);
        using var doc = JsonDocument.Parse(handler.Body!);
        Assert.Equal("codex", doc.RootElement.GetProperty("agentKind").GetString());
        Assert.Equal(At, doc.RootElement.GetProperty("windows")[0].GetProperty("measuredAt").GetDateTimeOffset());
    }

    private sealed class CapacityHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status);
        }
    }

    [Fact]
    public void Stored_merge_preserves_old_windows_and_doctor_shows_known_and_unknown_offline()
    {
        var folder = _folder;
        var store = new ConfigStore(folder, "file").AgentCapacity;
        Assert.Contains("Claude Code: usage limits unknown (none reported yet)", store.Describe(At));
        store.Merge(new() { AgentKind = "codex", Windows = [new() { Window = "weekly", UsedPercent = 16, MeasuredAt = At, ResetsAt = At.AddDays(3) }] });
        store.Merge(new() { AgentKind = "codex", Windows = [new() { Window = "five-hour", UsedPercent = 20, MeasuredAt = At.AddHours(2) }] });
        store.Merge(new() { AgentKind = "codex", Windows = [new() { Window = "weekly", UsedPercent = 1, MeasuredAt = At.AddHours(-1) }] });
        var reloaded = new ConfigStore(folder, "file").AgentCapacity;
        Assert.Equal(At, Assert.Single(Assert.Single(reloaded.Load()).Windows, w => w.Window == "weekly").MeasuredAt);
        Assert.Contains("Codex: weekly 16% used, resets in 2 days (checked 2 h ago)", reloaded.Describe(At.AddHours(2)));
        var contents = File.ReadAllText(Path.Combine(folder, "agent-capacity.json"));
        Assert.DoesNotContain("plan_type", contents);
    }
}
