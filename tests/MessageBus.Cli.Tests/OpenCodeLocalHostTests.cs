using System.Diagnostics;
using System.Text.Json.Nodes;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Roles;
using MessageBus.Tests.Shared;
using Xunit;

namespace MessageBus.Cli.Tests;

/// <summary>Opt-in native acceptance: executes the actual pinned package and one loaded local model.</summary>
public sealed class OpenCodeLocalHostTests
{
    [Fact]
    public async Task Pinned_package_effective_config_and_loaded_local_inference()
    {
        var executable = Environment.GetEnvironmentVariable("MBUS_TEST_OPENCODE_EXE");
        var apiId = Environment.GetEnvironmentVariable("MBUS_TEST_LMSTUDIO_MODEL");
        Assert.SkipWhen(InsideRun.Now || string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(apiId),
            "Native coordinator only; opt in with MBUS_TEST_OPENCODE_EXE and MBUS_TEST_LMSTUDIO_MODEL.");
        using var machine = new TestMachine();
        var endpoint = Environment.GetEnvironmentVariable("MBUS_TEST_LMSTUDIO_URL") ?? "http://127.0.0.1:1234/v1";
        var root = JsonNode.Parse(OpenCodeSettings.Parse(OpenCodeLocalTests.Config(v2: false)).Select(null).ConfigJson)!;
        root["provider"]!["local"]!["options"]!["baseURL"] = endpoint;
        root["provider"]!["local"]!["models"]!["publisher/coder"]!["id"] = apiId;
        root["small_model"] = "cloud/paid";
        root["agent"] = new JsonObject { ["compaction"] = new JsonObject { ["model"] = "cloud/paid" } };
        machine.Env[OpenCodeAdapter.ConfigContentVariable] = root.ToJsonString();
        var repo = Path.Combine(machine.Home, "repository");
        machine.WriteHomeFile("repository/opencode.json", """{"small_model":"cloud/paid","agent":{"build":{"model":"cloud/paid"}}}""");
        machine.WriteHomeFile("repository/.opencode/agents/cloud.md", "---\nmodel: cloud/paid\n---\ncloud");
        var prompt = machine.WriteHomeFile("prompt.md", "Reply with LOCAL_OK. Do not use any tools.");
        var selected = await OpenCodeAdapter.ForRunner(OpenCodeLocalRuntime.Version, machine.Env.GetValueOrDefault, machine.Home)
            .SelectAsync(null, TestContext.Current.CancellationToken);
        Assert.Null(selected.Error);
        var launch = selected.Adapter.BuildLaunch(new AgentLaunchContext("Reply with LOCAL_OK", prompt,
            PermissionProfiles.Defaults(PermissionProfiles.Worker), repo, RunDirectory: Path.Combine(machine.Home, "run")));
        Assert.Null(launch.CannotStart);
        Assert.Equal(OpenCodeLocalRuntime.Version, (await Execute(["--version"])).Trim());
        var effectiveText = await Execute(["debug", "config"]);
        var effective = JsonNode.Parse(effectiveText)!;
        Assert.Equal(OpenCodeLocalRuntime.Model, effective["small_model"]!.GetValue<string>());
        Assert.All(effective["agent"]!.AsObject(), a => Assert.Equal(OpenCodeLocalRuntime.Model, a.Value!["model"]!.GetValue<string>()));
        Assert.DoesNotContain("cloud/paid", effectiveText);
        var models = await Execute(["models"]);
        Assert.Equal(OpenCodeLocalRuntime.Model, models.Trim());
        var output = await Execute(launch.Arguments);
        var parser = selected.Adapter.CreateParser();
        foreach (var line in output.Split('\n')) parser.Observe(line);
        var outcome = parser.Complete();
        Assert.False(outcome.ReportedError, outcome.ErrorText);
        Assert.Contains("LOCAL_OK", outcome.FinalText);
        Assert.True(outcome.Usage!.TokensIn + outcome.Usage.TokensOut > 0);
        Assert.Equal(0m, outcome.Usage.EstimatedCostUsd);
        if (Environment.GetEnvironmentVariable("MBUS_TEST_OPENCODE_EVIDENCE") is { Length: > 0 } evidence)
        {
            Directory.CreateDirectory(evidence);
            await File.WriteAllTextAsync(Path.Combine(evidence, "effective-config.json"), effectiveText, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(evidence, "models.txt"), models, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(evidence, "local-run.jsonl"), output, TestContext.Current.CancellationToken);
        }

        async Task<string> Execute(IReadOnlyList<string> arguments)
        {
            var start = new ProcessStartInfo(executable!) { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            foreach (var (key, value) in launch.Environment) start.Environment[key] = value;
            using var process = Process.Start(start)!;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromMinutes(2));
            var stdout = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var stderr = process.StandardError.ReadToEndAsync(deadline.Token);
            try { await process.WaitForExitAsync(deadline.Token); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(TestContext.Current.CancellationToken); } }
            Assert.True(process.ExitCode == 0, await stderr);
            return await stdout;
        }
    }
}
