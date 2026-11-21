using System.Net;
using System.Text.Json.Nodes;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class OpenCodeLocalTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Real_scanner_reports_authless_local_models_and_keeps_authenticated_cloud_available(bool cloudAuth, bool localUp)
    {
        using var machine = new TestMachine();
        machine.InstallAgent("opencode", "2.3.0");
        machine.Env[OpenCodeAdapter.ConfigContentVariable] = Config();
        machine.LocalModelHandler = localUp ? () => GoodHandler([]) : DownHandler;
        if (cloudAuth) machine.WriteHomeFile(".local/share/opencode/auth.json", "{}");
        var agents = await AgentScanner.ScanAsync(machine.CreateHost(), TestContext.Current.CancellationToken);
        var found = Assert.Single(agents, a => a.Kind == MessageBus.Contracts.Machines.AgentKind.OpenCode);
        Assert.True(found.Found);
        Assert.Equal(cloudAuth || localUp, found.SignedIn);
        Assert.Equal(!cloudAuth && !localUp, found.NotReady is not null);
        Assert.Equal("local/publisher/coder", found.DefaultModel);
        Assert.Contains("local/publisher/coder", found.Models);
        Assert.True(found.ModelsPartial);
    }

    [Fact]
    public async Task Smoke_refuses_an_unavailable_local_model_before_creating_its_scratch_repository()
    {
        using var machine = new TestMachine();
        machine.Env[OpenCodeAdapter.ConfigContentVariable] = Config();
        var scratch = Path.Combine(machine.Home, "smoke-runs");
        var smoke = new SmokeTest(new SmokeTestOptions { WorkRoot = scratch, Git = "unused-git", Sandbox = new NoSandbox() });
        var adapter = OpenCodeAdapter.ForRunner("2.3.0", machine.Env.GetValueOrDefault, machine.Home, DownHandler);
        var result = await smoke.RunAsync(new SmokeAgent(MessageBus.Contracts.Machines.AgentKind.OpenCode, adapter, "unused-opencode"), TestContext.Current.CancellationToken);
        Assert.Contains("not reachable", result.NotReady);
        Assert.False(Directory.Exists(scratch));
    }

    [Fact]
    public async Task Disabled_and_ambiguous_or_wrong_generation_settings_are_refused_before_launch()
    {
        using var machine = new TestMachine();
        machine.Env[OpenCodeAdapter.ConfigContentVariable] = Config();
        var adapter = OpenCodeAdapter.ForRunner("2.3.0", machine.Env.GetValueOrDefault, machine.Home, () => throw new InvalidOperationException());
        Assert.Contains("disabled", (await adapter.SelectAsync("local/hidden", TestContext.Current.CancellationToken)).Error);
        Assert.NotNull(OpenCodeSettings.Read(machine.Env.GetValueOrDefault, machine.Home, "1.19.5").Problem);
        machine.Env[OpenCodeAdapter.ConfigContentVariable] = """{"provider":{},"providers":{}}""";
        Assert.NotNull(OpenCodeSettings.Read(machine.Env.GetValueOrDefault, machine.Home).Problem);
    }

    // OpenCode 1.19.5 (provider/options/npm/id), 2.3.0 (providers/settings/package/modelID),
    // reduced to configured fields; native LM Studio /api/v1/models loaded-instance shape.
    internal static string Config(bool v2 = true, bool cloudDefault = false) => $$$$"""
        {
          // A partial configured inventory, with a slashed model and a separate API alias.
          "model": "{{{{(cloudDefault ? "cloud/paid" : "local/publisher/coder")}}}}",
          "{{{{(v2 ? "providers" : "provider")}}}}": {
            "local": {
              "{{{{(v2 ? "package" : "npm")}}}}": "{{{{(v2 ? "@opencode/ai/providers/openai-compatible" : "@ai-sdk/openai-compatible")}}}}",
              "{{{{(v2 ? "settings" : "options")}}}}": {"baseURL":"http://localhost:12345/v1"},
              "models": {"publisher/coder":{"{{{{(v2 ? "modelID" : "id")}}}}":"api-coder"}, "hidden":{"disabled":true}}
            },
            "cloud": {"models":{"paid":{}}}
          },
          "mcp":{"other":{"type":"local","command":["safe-tool"]}},
        }
        """;

    internal const string Native = """{"models":[{"type":"llm","key":"publisher/coder","loaded_instances":[{"id":"api-coder"}]}]}""";
    internal const string Models = """{"data":[{"id":"api-coder"}]}""";
    private const string Step = """{"type":"step_finish","part":{"tokens":{"input":7,"output":4,"reasoning":2,"cache":{"read":3,"write":1}},"cost":0}}""";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Both_generations_preserve_selection_alias_config_overlay_and_local_usage(bool v2)
    {
        using var machine = new TestMachine();
        machine.WriteHomeFile(".config/opencode/opencode.jsonc", Config(v2));
        machine.Env[OpenCodeAdapter.ConfigContentVariable] = """{"description":"environment overlay","permission":{"edit":"allow"}}""";
        var requests = new List<string>();
        var adapter = OpenCodeAdapter.ForRunner(v2 ? "2.3.0" : "1.19.5", machine.Env.GetValueOrDefault, machine.Home,
            () => GoodHandler(requests));
        var selected = await adapter.SelectAsync(null, TestContext.Current.CancellationToken);
        Assert.Null(selected.Error);
        Assert.Equal(["/api/v1/models", "/v1/models"], requests);
        var launch = selected.Adapter.BuildLaunch(Context());
        Assert.Contains("local/publisher/coder", launch.Arguments);
        Assert.Equal(v2, launch.Arguments.Contains("--standalone"));
        var overlay = JsonNode.Parse(launch.Environment[OpenCodeAdapter.ConfigContentVariable]!)!;
        Assert.Equal("environment overlay", overlay["description"]!.GetValue<string>());
        Assert.Equal("deny", overlay["permission"]!["edit"]!.GetValue<string>());
        Assert.NotNull(overlay["mcp"]!["other"]);
        Assert.NotNull(v2 ? overlay["mcp"]!["servers"]!["mbus"] : overlay["mcp"]!["mbus"]);
        var shape = v2 ? "providers" : "provider";
        var options = v2 ? "settings" : "options";
        Assert.Equal("http://localhost:12345/v1", overlay[shape]!["local"]!["models"]!["publisher/coder"]![options]!["baseURL"]!.GetValue<string>());
        var parser = selected.Adapter.CreateParser();
        parser.Observe(Step);
        var usage = parser.Complete().Usage!;
        Assert.Equal(0m, usage.EstimatedCostUsd);
        Assert.Equal(0m, RunCosts.Estimate(usage, new TokenPrice { InputUsdPerMillion = 5, OutputUsdPerMillion = 20 }));
        Assert.Equal((11L, 6L, 3L, 1L), (usage.TokensIn, usage.TokensOut, usage.CachedTokensIn, usage.CacheWriteTokensIn));
        var inventory = OpenCodeSettings.Read(machine.Env.GetValueOrDefault, machine.Home).Inventory;
        Assert.True(inventory.Partial);
        Assert.Equal(["local/publisher/coder", "cloud/paid"], inventory.Models);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Explicit_overrides_in_both_directions_use_the_selected_model_for_launch_and_cost(bool cloudDefault)
    {
        using var machine = new TestMachine();
        machine.Env[OpenCodeAdapter.ConfigContentVariable] = Config(cloudDefault: cloudDefault);
        var calls = new List<string>();
        var adapter = OpenCodeAdapter.ForRunner("2.3.0", machine.Env.GetValueOrDefault, machine.Home, () => GoodHandler(calls));
        var id = cloudDefault ? "local/publisher/coder" : "cloud/paid";
        var selected = await adapter.SelectAsync(id, TestContext.Current.CancellationToken);
        Assert.Null(selected.Error);
        Assert.Contains(id, selected.Adapter.BuildLaunch(Context()).Arguments);
        var parser = selected.Adapter.CreateParser(id);
        parser.Observe(Step);
        Assert.Equal(cloudDefault ? 0m : (decimal?)null, parser.Complete().Usage!.EstimatedCostUsd);
        Assert.Equal(cloudDefault ? 2 : 0, calls.Count);
        parser.Observe(Step.Replace("\"cost\":0", "\"cost\":0.12", StringComparison.Ordinal));
        Assert.Equal(0.12m, parser.Complete().Usage!.EstimatedCostUsd);
    }

    [Theory]
    [InlineData("{\"models\":[]}", "no language models")]
    [InlineData("{\"models\":[{\"type\":\"embedding\",\"key\":\"api-coder\"}]}", "embedding")]
    [InlineData("{\"models\":[{\"type\":\"llm\",\"key\":\"api-coder\",\"loaded_instances\":[]}]}", "not loaded")]
    [InlineData("{\"models\":[{\"type\":\"llm\",\"key\":\"api-coder\"}]}", "unknown")]
    [InlineData("{\"models\":[{\"type\":\"llm\",\"key\":\"other\",\"loaded_instances\":[{\"id\":\"other\"}]}]}", "does not list")]
    [InlineData("{\"data\":[{\"id\":\"api-coder\"}]}", "unknown")]
    [InlineData("not json", "invalid")]
    public async Task Native_state_never_claims_an_unloaded_or_unknown_model_is_ready(string native, string error)
    {
        var requests = 0;
        var selected = OpenCodeSettings.Parse(Config()).Select(null);
        var problem = await OpenCodeReadiness.CheckAsync(selected, TestContext.Current.CancellationToken,
            () => new Handler((_, _) => { requests++; return Task.FromResult(Json(native)); }));
        Assert.Contains(error, problem);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(401, "authentication")]
    [InlineData(403, "authentication")]
    [InlineData(302, "redirects")]
    [InlineData(404, "native")]
    public async Task Http_errors_are_actionable_without_following_redirects(int status, string error)
    {
        var problem = await OpenCodeReadiness.CheckAsync(OpenCodeSettings.Parse(Config()).Select(null), TestContext.Current.CancellationToken,
            () => new Handler((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status))));
        Assert.Contains(error, problem);
        Assert.False(((SocketsHttpHandler)OpenCodeReadiness.Handler()).AllowAutoRedirect);
    }

    [Fact]
    public async Task One_deadline_covers_headers_and_bodies_and_caller_cancellation_propagates()
    {
        var time = new FakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selected = OpenCodeSettings.Parse(Config()).Select(null);
        Handler Block() => new(async (_, ct) => { entered.TrySetResult(); await new TaskCompletionSource().Task.WaitAsync(ct); return Json("{}"); });
        var check = OpenCodeReadiness.CheckAsync(selected, CancellationToken.None, Block, time);
        await entered.Task;
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Contains("five seconds", await check);
        using var caller = new CancellationTokenSource();
        var cancelled = OpenCodeReadiness.CheckAsync(selected, caller.Token, Block, time);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
    }

    [Fact]
    public async Task Reading_the_second_body_uses_the_remaining_total_budget()
    {
        var time = new FakeTimeProvider();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var check = OpenCodeReadiness.CheckAsync(OpenCodeSettings.Parse(Config()).Select(null), TestContext.Current.CancellationToken,
            () => new Handler((_, _) =>
            {
                calls++;
                if (calls == 1) { time.Advance(TimeSpan.FromSeconds(4)); return Task.FromResult(Json(Native)); }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BlockingBody(reading)) });
            }), time);
        await reading.Task.WaitAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Contains("five seconds", await check);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Bounded_inventory_keeps_the_local_default_even_when_other_providers_fill_the_catalog()
    {
        var root = JsonNode.Parse(Config(), documentOptions: new System.Text.Json.JsonDocumentOptions
            { AllowTrailingCommas = true, CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!.AsObject();
        var models = root["providers"]!["cloud"]!["models"]!.AsObject();
        for (var i = 0; i < 110; i++) models["model@" + i] = new JsonObject();
        var settings = OpenCodeSettings.Parse(root.ToJsonString());
        Assert.Equal(100, settings.Inventory.Models.Count);
        Assert.NotNull(settings.Select(null).Endpoint);
        Assert.Contains("cloud/model@1", settings.Inventory.Models);
        Assert.Contains("bounded", settings.SelectionProblem("cloud/model@109"));
    }

    [Fact]
    public async Task Bounded_bodies_alias_mismatch_and_connection_refusal_fail_safely()
    {
        var selected = OpenCodeSettings.Parse(Config()).Select(null);
        var large = await OpenCodeReadiness.CheckAsync(selected, TestContext.Current.CancellationToken,
            () => new Handler((_, _) => Task.FromResult(Json(new string(' ', OpenCodeSettings.MaxBytes + 1)))));
        Assert.Contains("oversized", large);
        var mismatch = await OpenCodeReadiness.CheckAsync(selected, TestContext.Current.CancellationToken,
            () => new Handler((r, _) => Task.FromResult(Json(r.RequestUri!.AbsolutePath == "/api/v1/models" ? Native : "{\"data\":[]}"))));
        Assert.Contains("API ID", mismatch);
        var refused = await OpenCodeReadiness.CheckAsync(selected, TestContext.Current.CancellationToken, DownHandler);
        Assert.Contains("not reachable", refused);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Mixed_provider_scan_preserves_cloud_availability_and_local_only_recovers_on_rescan(bool cloudDefault)
    {
        var settings = OpenCodeSettings.Parse(Config(cloudDefault: cloudDefault));
        var mixed = await OpenCodeReadiness.ScanAsync(settings, true, TestContext.Current.CancellationToken, DownHandler);
        Assert.True(mixed.SignedIn);
        Assert.Null(mixed.NotReady);
        Assert.Contains("local provider", mixed.Warning);
        var localOnly = await OpenCodeReadiness.ScanAsync(settings, false, TestContext.Current.CancellationToken, DownHandler);
        Assert.False(localOnly.SignedIn);
        Assert.NotNull(localOnly.NotReady);
        var recovered = await OpenCodeReadiness.ScanAsync(settings, false, TestContext.Current.CancellationToken, () => GoodHandler([]));
        Assert.True(recovered.SignedIn);
        Assert.Null(recovered.NotReady);
    }

    [Fact]
    public async Task Delivery_rereads_config_and_concurrent_selections_do_not_share_state()
    {
        using var machine = new TestMachine();
        machine.Env[OpenCodeAdapter.ConfigContentVariable] = Config();
        var adapter = OpenCodeAdapter.ForRunner("2.3.0", machine.Env.GetValueOrDefault, machine.Home, () => GoodHandler([]));
        var scanned = OpenCodeSettings.Read(machine.Env.GetValueOrDefault, machine.Home);
        machine.Env[OpenCodeAdapter.ConfigContentVariable] = Config().Replace("12345", "12346", StringComparison.Ordinal);
        var local = adapter.SelectAsync("local/publisher/coder", TestContext.Current.CancellationToken);
        var cloud = adapter.SelectAsync("cloud/paid", TestContext.Current.CancellationToken);
        await Task.WhenAll(local, cloud);
        Assert.Equal(12345, scanned.Select(null).Endpoint!.Port);
        Assert.Equal(12346, local.Result.Adapter.LocalEndpoint!.Port);
        Assert.Null(cloud.Result.Adapter.LocalEndpoint);
        var free = local.Result.Adapter.CreateParser();
        var unknown = cloud.Result.Adapter.CreateParser();
        free.Observe(Step);
        unknown.Observe(Step);
        Assert.Equal(0m, free.Complete().Usage!.EstimatedCostUsd);
        Assert.Null(unknown.Complete().Usage!.EstimatedCostUsd);
        Assert.Null(local.Result.Adapter.CreateParser().Complete().Usage!.EstimatedCostUsd);
    }

    [Fact]
    public async Task Readiness_and_launch_resolve_the_same_stored_runner_environment()
    {
        using var machine = new TestMachine();
        machine.Env[OpenCodeAdapter.ConfigContentVariable] = Config().Replace("\"baseURL\":\"http://localhost:12345/v1\"",
            "\"baseURL\":\"{env:LOCAL_ENDPOINT}\",\"apiKey\":\"{env:LOCAL_TEST_KEY}\"", StringComparison.Ordinal);
        var stored = new Dictionary<string, string> { ["LOCAL_ENDPOINT"] = "http://localhost:12345/v1", ["LOCAL_TEST_KEY"] = "test-key" };
        var adapter = OpenCodeAdapter.ForRunner("2.3.0", machine.Env.GetValueOrDefault, machine.Home,
            () => new Handler((r, _) =>
            {
                Assert.Equal("Bearer", r.Headers.Authorization!.Scheme);
                Assert.Equal("test-key", r.Headers.Authorization.Parameter);
                Assert.Equal(12346, r.RequestUri!.Port);
                return Task.FromResult(Json(r.RequestUri.AbsolutePath == "/api/v1/models" ? Native : Models));
            }), () => stored);
        stored["LOCAL_ENDPOINT"] = "http://localhost:12346/v1";
        var selected = await adapter.SelectAsync(null, TestContext.Current.CancellationToken);
        Assert.Null(selected.Error);
        var config = selected.Adapter.BuildLaunch(Context()).Environment[OpenCodeAdapter.ConfigContentVariable]!;
        Assert.Contains("12346", config);
        Assert.DoesNotContain("{env:", config);
    }

    [Fact]
    public async Task Names_and_arbitrary_loopback_alone_cannot_turn_unknown_usage_into_free_inference()
    {
        using var machine = new TestMachine();
        machine.Env[OpenCodeAdapter.ConfigContentVariable] = Config().Replace("@opencode/ai/providers/openai-compatible", "arbitrary-package", StringComparison.Ordinal)
            .Replace("local", "lmstudio", StringComparison.Ordinal);
        var adapter = OpenCodeAdapter.ForRunner("2.3.0", machine.Env.GetValueOrDefault, machine.Home,
            () => throw new InvalidOperationException("No probe for an unknown runtime"));
        var selected = await adapter.SelectAsync(null, TestContext.Current.CancellationToken);
        var parser = selected.Adapter.CreateParser();
        parser.Observe(Step);
        Assert.Null(parser.Complete().Usage!.EstimatedCostUsd);
    }

    [Fact]
    public void File_env_and_content_precedence_bounds_and_disabled_entries_are_respected()
    {
        using var machine = new TestMachine();
        machine.WriteHomeFile(".config/opencode/opencode.json", Config());
        var extra = machine.WriteHomeFile("extra.jsonc", """{"model":"cloud/paid"}""");
        machine.Env["OPENCODE_CONFIG"] = extra;
        machine.Env[OpenCodeAdapter.ConfigContentVariable] = """{"model":"local/publisher/coder","providers":{"local":{"settings":{"baseURL":"{env:LOCAL_URL}"}}}}""";
        machine.Env["LOCAL_URL"] = "http://[::1]:12344/prefix/v1";
        var settings = OpenCodeSettings.Read(machine.Env.GetValueOrDefault, machine.Home);
        Assert.Equal("http://[::1]:12344/prefix/v1", settings.Select(null).Endpoint!.AbsoluteUri);
        machine.Env[OpenCodeAdapter.ConfigContentVariable] = """{"disabled_providers":["local"]}""";
        Assert.Equal("cloud/paid", OpenCodeSettings.Read(machine.Env.GetValueOrDefault, machine.Home).Inventory.Default);
        machine.Env[OpenCodeAdapter.ConfigContentVariable] = "{";
        Assert.NotNull(OpenCodeSettings.Read(machine.Env.GetValueOrDefault, machine.Home).Problem);
        machine.Env[OpenCodeAdapter.ConfigContentVariable] = new string(' ', OpenCodeSettings.MaxBytes + 1);
        Assert.Empty(OpenCodeSettings.Read(machine.Env.GetValueOrDefault, machine.Home).Inventory.Models);
    }

    internal static Handler GoodHandler(List<string> requests) => new((request, _) =>
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        requests.Add(request.RequestUri!.AbsolutePath);
        return Task.FromResult(Json(request.RequestUri.AbsolutePath.EndsWith("/api/v1/models", StringComparison.Ordinal) ? Native : Models));
    });
    internal static HttpMessageHandler DownHandler() => new Handler((_, _) => throw new HttpRequestException("refused"));
    internal static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    internal sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class BlockingBody(TaskCompletionSource entered) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            return new(new TaskCompletionSource<int>().Task.WaitAsync(cancellationToken));
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private static AgentLaunchContext Context() => new("task", "prompt.md", PermissionProfiles.Defaults(PermissionProfiles.Orchestrator), ".",
        new Dictionary<string, string> { ["MBUS_RUN_DIR"] = "run" });
}
