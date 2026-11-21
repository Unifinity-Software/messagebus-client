using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Cli.Runner.Sandbox;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Roles;
using MessageBus.Tests.Shared;
using Xunit;

namespace MessageBus.Cli.Tests;

/// <summary>Native opt-in: actual package, fake inference, independent cloud counter. Never downloads a package.</summary>
public sealed class OpenCodeRuntimeProofTests
{
    [Fact(Timeout = 240_000)]
    public async Task Pinned_package_confines_primary_subtask_compaction_and_failure_through_runner_isolation()
    {
        var executable = Environment.GetEnvironmentVariable("MBUS_TEST_OPENCODE_EXE");
        Assert.SkipWhen(InsideRun.Now || string.IsNullOrWhiteSpace(executable), "Native coordinator with verified package only; see eng/opencode-proof/README.md.");
        var evidenceRoot = Environment.GetEnvironmentVariable("MBUS_TEST_OPENCODE_EVIDENCE");
        Assert.False(string.IsNullOrWhiteSpace(evidenceRoot), "An evidence directory is required, including for failures.");
        var evidence = Directory.CreateDirectory(Path.Combine(evidenceRoot!, "fake-runtime-" + Guid.NewGuid().ToString("N"))).FullName;
        using var log = new StringWriter();
        try { await RunProof(executable!, evidence, log); }
        catch (Exception e)
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "FAIL.json"),
                JsonSerializer.Serialize(new { error = e.ToString() }), CancellationToken.None);
            throw;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "isolation.log"), log.ToString(), CancellationToken.None);
        }
    }

    private static async Task RunProof(string executable, string evidence, StringWriter log)
    {
        log.WriteLine("setup: loopback endpoints");
        await using var registry = new OpenCodeProofEndpoint();
        await using var local = new OpenCodeProofEndpoint();
        await using var cloud = new OpenCodeProofEndpoint();
        var owned = Directory.CreateDirectory(Path.Combine(evidence, "owned")).FullName;
        log.WriteLine("setup: workspace");
        using var workspace = RunWorkspace.Create(owned, "dlv_native_proof", 1);
        Directory.CreateDirectory(workspace.WorkTree);
        var home = Directory.CreateDirectory(Path.Combine(workspace.Root, "original-home")).FullName;
        var profile = PermissionProfiles.Defaults(PermissionProfiles.Worker) with { Network = NetworkAccess.Open };
        log.WriteLine("setup: isolation");
        var sandbox = RunIsolation.Create(new RunIsolationOptions
        {
            WorkRoot = owned, HomeDirectory = home, Requested = IsolationLevel.OsSandbox,
            // No owner git, credentials, personal config or service changes.
        }, log);
        // Positive controls intentionally retain hostile scopes, but still own/terminate every child.
        var controls = RunIsolation.Create(new RunIsolationOptions
        {
            WorkRoot = owned, HomeDirectory = home, Requested = IsolationLevel.Basic,
        }, log);
        var count = 0;
        try
        {
            var digest = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(executable!, TestContext.Current.CancellationToken)));
            Assert.Equal(Environment.GetEnvironmentVariable("MBUS_TEST_OPENCODE_SHA256")?.ToUpperInvariant(), digest);
            await Save("binary.json", new { executable, sha256 = digest, expectedVersion = OpenCodeLocalRuntime.Version });
            var env = OriginalEnvironment();
            // Config starts @opencode-ai/plugin installs even for an import-free file plugin;
            // Plugin.init joins those fibers before debug config can print anything. Keep npm
            // off the public registry, with a real HTTP 404 and no retry from an owned listener.
            // Upstream logs the optional dependency failure and still executes our file plugin.
            await Save("registry-policy.json", new { registry = registry.Url, status = 404, retries = 0 });
            var git = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []);
            Assert.NotNull(git);
            Assert.Equal(0, (await Execute("git-init", Raw(["init", "--quiet"], env) with { FileName = git! }, control: true)).Exit);
            // With no external plugin, pinned Plugin.init skips Config.waitForDependencies and
            // the CLI can exit with detached install fibers still holding locks. A file plugin
            // makes it join ALL config dependency fibers before invoking this import-free marker.
            // Keep it outside discovered plugin directories and in this launch's config only.
            var initializePlugin = Path.Combine(owned, "initialize-dependencies.mjs");
            Write(initializePlugin, "export const InitializeDependencies = async () => { console.log('INITIALIZE_DEPENDENCIES_JOINED'); return {} };\n");
            var initializeEnv = new Dictionary<string, string?>(env)
            {
                ["OPENCODE_CONFIG_CONTENT"] = new JsonObject
                {
                    ["plugin"] = new JsonArray(new Uri(initializePlugin).AbsoluteUri),
                }.ToJsonString(),
            };
            // Initialize the real schema in an empty owned DB before inserting a synthetic active account.
            var initialized = await Execute("initialize", Raw(["debug", "config"], initializeEnv), control: true);
            Assert.Equal(0, initialized.Exit);
            Assert.Contains("INITIALIZE_DEPENDENCIES_JOINED", initialized.Out);
            var python = Environment.GetEnvironmentVariable("MBUS_TEST_PROOF_PYTHON");
            Assert.False(string.IsNullOrWhiteSpace(python), "Native packet must supply Python for the owned SQLite account fixture.");
            var seed = Path.Combine(owned, "seed-account.py");
            await File.WriteAllTextAsync(seed, """
                import sqlite3, sys, time
                db = sqlite3.connect(sys.argv[1])
                now = int(time.time()*1000)
                db.execute('INSERT INTO account(id,email,url,access_token,refresh_token,token_expiry,time_created,time_updated) VALUES(?,?,?,?,?,?,?,?)', ('proof-account','proof@example.invalid',sys.argv[2],'proof-only','proof-only',now+3600000,now,now))
                db.execute('INSERT OR REPLACE INTO account_state(id,active_account_id,active_org_id) VALUES(1,?,?)', ('proof-account','proof-org'))
                db.commit()
                """, TestContext.Current.CancellationToken);
            Assert.Equal(0, (await Execute("seed-account", Raw([seed, env["OPENCODE_DB"]!, cloud.Url], env) with { FileName = python! }, control: true)).Exit);

            var hostile = HostileConfig();
            cloud.Config = hostile;
            Write(Path.Combine(home, ".config/opencode/opencode.json"), hostile.ToJsonString());
            Write(Path.Combine(home, ".opencode/opencode.json"), hostile.ToJsonString());
            Write(Path.Combine(home, ".local/share/opencode/auth.json"), new JsonObject
            {
                [cloud.Url] = new JsonObject { ["type"] = "wellknown", ["key"] = "PROOF_ACCOUNT_KEY", ["token"] = "proof-only" },
                ["proof-cloud"] = new JsonObject { ["type"] = "api", ["key"] = "proof-only" },
            }.ToJsonString());
            Write(Path.Combine(workspace.WorkTree, "opencode.jsonc"), hostile.ToJsonString());
            Write(Path.Combine(workspace.WorkTree, ".opencode/agents/proof-hostile.md"), "---\nmodel: proof-cloud/paid\nmode: subagent\ndescription: Owned hostile fixture\n---\nReply with HOSTILE_PROOF\n");
            Write(Path.Combine(home, ".opencode/agents/proof-home.md"), "---\nmodel: proof-cloud/paid\nmode: subagent\ndescription: Owned home fixture\n---\nHOSTILE_PROOF\n");
            // Each positive control runs only on fake loopback endpoints. The hostile fixture must really load.
            var positive = await Execute("positive-config", Raw(["debug", "config"], env), control: true);
            Assert.Equal(0, positive.Exit);
            Assert.Contains("proof-cloud/paid", positive.Out);
            Assert.Contains("proof-hostile", positive.Out);
            Assert.Contains("proof-home", positive.Out);
            Assert.Contains(cloud.Requests, r => JsonSerializer.Serialize(r).Contains("/.well-known/opencode", StringComparison.Ordinal));
            Assert.Contains(cloud.Requests, r => JsonSerializer.Serialize(r).Contains("/api/config", StringComparison.Ordinal));
            Assert.Contains("HOSTILE_PLUGIN", (await Execute("positive-plugin", Raw(["run", "--model", "proof-cloud/paid", "--format", "json", "Reply LOCAL_OK"], env), control: true)).Out);
            Assert.NotEmpty(cloud.Models); // Counter positive control, not merely a direct test-client request.
            Assert.Contains(registry.Requests, r => JsonSerializer.Serialize(r).Contains("opencode-ai", StringComparison.Ordinal));
            controls.Ended(workspace);
            await Save("positive-cloud-requests.json", cloud.Requests.ToArray());
            var baseline = cloud.Requests.Count;
            var modelBaseline = cloud.Models.Count;
            await Save("counter-baseline.json", new { requests = baseline, inference = modelBaseline });

            var selectionConfig = JsonNode.Parse(OpenCodeSettings.Parse(OpenCodeLocalTests.Config(v2: false)).Select(null).ConfigJson)!;
            selectionConfig["provider"]!["local"]!["options"]!["baseURL"] = local.Url + "/v1";
            selectionConfig["provider"]!["local"]!["models"]!["publisher/coder"]!["id"] = OpenCodeProofEndpoint.ApiId;
            selectionConfig["small_model"] = "proof-cloud/paid";
            env[OpenCodeAdapter.ConfigContentVariable] = selectionConfig.ToJsonString();
            var selected = await OpenCodeAdapter.ForRunner(OpenCodeLocalRuntime.Version, n => env.GetValueOrDefault(n), home)
                .SelectAsync(null, TestContext.Current.CancellationToken);
            Assert.Null(selected.Error);
            Write(workspace.PromptFile, "Reply LOCAL_OK. Do not use tools.");
            workspace.LocalModelEndpoint = new Uri(local.Url + "/v1");
            var adapterArgumentCount = 0;
            var launch = AgentRun.AgentProcessLaunch(selected.Adapter, sandbox, executable!, workspace, profile,
                new AgentLaunchContext("Reply LOCAL_OK", workspace.PromptFile, profile, workspace.WorkTree), env,
                l => { Assert.Null(l.CannotStart); adapterArgumentCount = l.Arguments.Count; });
            await Save("final-launch.json", new
            {
                launch.FileName, launch.Arguments, launch.WorkingDirectory, sandbox.IsolationEnabled, sandbox.Report,
                environment = launch.Environment.Where(p => p.Key.StartsWith("OPENCODE_", StringComparison.Ordinal)
                    || p.Key.StartsWith("XDG_", StringComparison.Ordinal) || p.Key is "HOME" or "USERPROFILE").ToDictionary(),
            });
            Assert.Equal(OpenCodeLocalRuntime.Version, (await Execute("version", Command(["--version"]))).Out.Trim());
            var effective = await Execute("effective-config", Command(["debug", "config"]));
            Assert.Equal(0, effective.Exit);
            var config = JsonNode.Parse(effective.Out)!;
            Assert.Equal(OpenCodeLocalRuntime.Model, config["model"]!.GetValue<string>());
            Assert.Equal(OpenCodeLocalRuntime.Model, config["small_model"]!.GetValue<string>());
            foreach (var agent in new[] { "build", "plan", "general", "explore", "title", "summary", "compaction" })
                Assert.Equal(OpenCodeLocalRuntime.Model, config["agent"]![agent]!["model"]!.GetValue<string>());
            Assert.DoesNotContain("proof-cloud", effective.Out);
            Assert.DoesNotContain("proof-hostile", effective.Out);
            Assert.DoesNotContain("proof-home", effective.Out);
            Assert.DoesNotContain("proof-plugin", effective.Out);
            Assert.Equal("[\"mbus-local\"]", config["enabled_providers"]!.ToJsonString());
            Assert.Equal(local.Url + "/v1", config["provider"]!["mbus-local"]!["options"]!["baseURL"]!.GetValue<string>().TrimEnd('/'));
            var provider = config["provider"]!["mbus-local"]!;
            Assert.Equal("[\"selected\"]", provider["whitelist"]!.ToJsonString());
            Assert.Equal(OpenCodeProofEndpoint.ApiId, provider["models"]!["selected"]!["id"]!.GetValue<string>());
            Assert.Equal("@ai-sdk/openai-compatible", provider["npm"]!.GetValue<string>());
            Assert.Equal(OpenCodeLocalRuntime.Model, (await Execute("models", Command(["models"]))).Out.Trim());
            var primary = await Execute("primary", launch);
            Assert.Equal(0, primary.Exit);
            var parser = selected.Adapter.CreateParser();
            foreach (var line in primary.Out.Split('\n')) parser.Observe(line);
            var result = parser.Complete();
            Assert.False(result.ReportedError, result.ErrorText);
            Assert.Contains("LOCAL_OK", result.FinalText);
            Assert.True(result.Usage!.TokensIn + result.Usage.TokensOut > 0);
            Assert.Equal(0m, result.Usage.EstimatedCostUsd);
            Assert.NotEmpty(local.Models);

            // Reuse the FINAL wrapped environment and executable; change only argv inside the OS wrapper.
            var port = FreePort();
            var serverLaunch = Command(["serve", "--hostname", "127.0.0.1", "--port", port.ToString()]);
            using var server = Start(serverLaunch);
            sandbox.Started(workspace, server.Id);
            var serverOut = Capture(server.StandardOutput, TestContext.Current.CancellationToken);
            var serverErr = Capture(server.StandardError, TestContext.Current.CancellationToken);
            try
            {
                using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(35) };
                http.DefaultRequestHeaders.Add("x-opencode-directory", workspace.WorkTree);
                using var readyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (true)
                {
                    try { using var ready = await http.GetAsync("/global/health", readyDeadline.Token); if (ready.IsSuccessStatusCode) break; }
                    catch (HttpRequestException) { }
                    await Task.Delay(100, readyDeadline.Token); // native integration readiness, never part of regular fast suite
                }
                var session = (await Api("create", "/session", new { title = "Owned proof" }))["id"]!.GetValue<string>();
                await Api("session-primary", $"/session/{session}/message", new { parts = new[] { new { type = "text", text = "Reply LOCAL_OK" } } });
                var before = local.Models.Count;
                var auxiliary = (await Api("create-auxiliary", "/session", new { title = "Owned auxiliary proof" }))["id"]!.GetValue<string>();
                var auxiliaryMessage = await Api("auxiliary-summary", $"/session/{auxiliary}/message", new { agent = "summary", parts = new[] { new { type = "text", text = "Reply LOCAL_OK" } } });
                Assert.Equal("summary", auxiliaryMessage["info"]!["agent"]!.GetValue<string>());
                Assert.True(local.Models.Count > before, "Auxiliary summary agent must perform inference");
                before = local.Models.Count;
                await Api("subtask", $"/session/{session}/message", new
                {
                    parts = new[] { new { type = "subtask", agent = "general", description = "Owned subagent proof", prompt = "Reply LOCAL_OK. Do not use tools." } },
                });
                Assert.True(local.Models.Count > before, "Subtask must perform inference");
                using (var children = await http.GetAsync($"/session/{session}/children"))
                {
                    var text = await children.Content.ReadAsStringAsync();
                    await Save("children.json", new { status = (int)children.StatusCode, body = text });
                    Assert.True(children.IsSuccessStatusCode);
                    Assert.NotEmpty(JsonNode.Parse(text)!.AsArray());
                }
                before = local.Models.Count;
                await Api("compaction", $"/session/{session}/summarize", new { providerID = "mbus-local", modelID = "selected", auto = false });
                Assert.True(local.Models.Count > before, "Explicit summarize must perform inference");
                using (var messages = await http.GetAsync($"/session/{session}/message"))
                {
                    var text = await messages.Content.ReadAsStringAsync();
                    await Save("compacted-messages.json", new { status = (int)messages.StatusCode, body = text });
                    Assert.True(messages.IsSuccessStatusCode);
                    Assert.Contains(JsonNode.Parse(text)!.AsArray(), m => m?["info"]?["agent"]?.GetValue<string>() == "compaction"
                        && m["info"]!["summary"]?.GetValue<bool>() == true);
                }

                async Task<JsonNode> Api(string name, string path, object body)
                {
                    await Save(name + "-request.json", new { path, request = body });
                    try
                    {
                        using var response = await http.PostAsJsonAsync(path, body, TestContext.Current.CancellationToken);
                        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                        await Save(name + ".json", new { path, request = body, status = (int)response.StatusCode, body = text });
                        Assert.True(response.IsSuccessStatusCode, text);
                        return JsonNode.Parse(text)!;
                    }
                    catch (Exception error) { await Save(name + "-failure.json", new { path, error = error.ToString() }); throw; }
                }
            }
            finally
            {
                if (!server.HasExited) server.Kill(entireProcessTree: true);
                await server.WaitForExitAsync(CancellationToken.None);
                await Save("server.json", new { stdout = await serverOut, stderr = await serverErr, exit = server.ExitCode });
            }
            local.FailInference = true;
            var failuresBefore = local.Models.Count;
            var failure = await Execute("primary-failure", launch);
            Assert.True(local.Models.Count > failuresBefore);
            Assert.True(failure.Exit != 0 || failure.Out.Contains("PROOF_PRIMARY_FAILURE", StringComparison.Ordinal));
            Assert.Equal(baseline, cloud.Requests.Count);
            Assert.Equal(modelBaseline, cloud.Models.Count);
            local.FailInference = false;
            var beforeUnknown = local.Models.Count;
            var unknown = await Execute("unknown-model", Command(["run", "--model", "proof-cloud/absent", "--format", "json", "Reply LOCAL_OK"]));
            Assert.True(unknown.Exit != 0 || unknown.Out.Contains("error", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain("LOCAL_OK", unknown.Out);
            Assert.Equal(beforeUnknown, local.Models.Count);
            Assert.Equal(baseline, cloud.Requests.Count);
            Assert.Equal(modelBaseline, cloud.Models.Count);
            Assert.All(local.Models, m => Assert.Equal(OpenCodeProofEndpoint.ApiId, m));
            Assert.Empty(local.Errors);
            Assert.Empty(cloud.Errors);
            Assert.Empty(registry.Errors);
            await Save("PASS.json", new { localInference = local.Models.Count, cloudRequestsAfterControl = cloud.Requests.Count - baseline, cloudInferenceAfterControl = cloud.Models.Count - modelBaseline });

            ProcessLaunch Command(IReadOnlyList<string> args)
            {
                // OS wrappers prefix the real executable. Preserve that prefix, replacing its original run argv.
                var prefix = launch.Arguments.Count - adapterArgumentCount;
                return launch with { Arguments = launch.Arguments.Take(prefix).Concat(args).ToArray() };
            }
        }
        finally
        {
            await Save("local-requests.json", local.Requests.ToArray());
            await Save("cloud-requests.json", cloud.Requests.ToArray());
            await Save("counters.json", new { localModels = local.Models.ToArray(), cloudModels = cloud.Models.ToArray(), localErrors = local.Errors.ToArray(), cloudErrors = cloud.Errors.ToArray() });
            sandbox.Ended(workspace);
            controls.Ended(workspace);
            await Save("registry-requests.json", registry.Requests.ToArray());
            await Save("registry-errors.json", registry.Errors.ToArray());
        }

        Dictionary<string, string?> OriginalEnvironment() => new()
        {
            ["HOME"] = home, ["USERPROFILE"] = home, ["OPENCODE_TEST_HOME"] = home,
            ["XDG_CONFIG_HOME"] = Path.Combine(home, ".config"), ["XDG_DATA_HOME"] = Path.Combine(home, ".local/share"),
            ["XDG_CACHE_HOME"] = Path.Combine(home, ".cache"), ["XDG_STATE_HOME"] = Path.Combine(home, ".state"),
            // Match upstream path.join(XDG_CONFIG_HOME, "opencode") exactly on Windows too:
            // ConfigPaths deduplicates strings, not physical directories or slash aliases.
            ["OPENCODE_CONFIG_DIR"] = Path.Combine(home, ".config", "opencode"), ["OPENCODE_CONFIG"] = "",
            ["OPENCODE_CONFIG_CONTENT"] = "{}", ["OPENCODE_AUTH_CONTENT"] = "", ["OPENCODE_SERVER_PASSWORD"] = "",
            ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_GLOBAL"] = Path.Combine(home, ".gitconfig"),
            ["OPENCODE_DB"] = Path.Combine(home, "accounts.db"),
            ["OPENCODE_PRINT_LOGS"] = "1", ["OPENCODE_LOG_LEVEL"] = "DEBUG",
            ["NPM_CONFIG_REGISTRY"] = registry.Url + "/",
            ["NPM_CONFIG_FETCH_RETRIES"] = "0", ["NPM_CONFIG_FETCH_TIMEOUT"] = "5000",
            ["NPM_CONFIG_AUDIT"] = "false", ["NPM_CONFIG_FUND"] = "false",
            ["NPM_CONFIG_USERCONFIG"] = Path.Combine(home, ".npmrc"),
            ["NPM_CONFIG_GLOBALCONFIG"] = Path.Combine(home, "global.npmrc"),
            ["NPM_CONFIG_CACHE"] = Path.Combine(home, ".npm-cache"),
            ["OPENCODE_DISABLE_MODELS_FETCH"] = "true", ["OPENCODE_DISABLE_AUTOUPDATE"] = "true",
            ["OPENCODE_DISABLE_DEFAULT_PLUGINS"] = "true", ["OPENCODE_DISABLE_PROJECT_CONFIG"] = "false", ["OPENCODE_PURE"] = "false",
        };
        JsonObject HostileConfig()
        {
            var plugin = Path.Combine(home, "proof-plugin.mjs");
            Write(plugin, "export const ProofPlugin = async () => { console.log('HOSTILE_PLUGIN'); return {} };\n");
            return new JsonObject
            {
                ["model"] = "proof-cloud/paid", ["small_model"] = "proof-cloud/paid",
                ["agent"] = new JsonObject { ["compaction"] = new JsonObject { ["model"] = "proof-cloud/paid" } },
                ["compaction"] = new JsonObject { ["auto"] = true, ["reserved"] = 1024 },
                ["plugin"] = new JsonArray(new Uri(plugin).AbsoluteUri),
                ["provider"] = new JsonObject { ["proof-cloud"] = new JsonObject
                {
                    ["npm"] = "@ai-sdk/openai-compatible", ["options"] = new JsonObject { ["baseURL"] = cloud.Url + "/v1", ["apiKey"] = "proof-only" },
                    ["models"] = new JsonObject { ["paid"] = new JsonObject { ["id"] = OpenCodeProofEndpoint.ApiId } },
                } },
            };
        }
        ProcessLaunch Raw(IReadOnlyList<string> args, Dictionary<string, string?> env) => new() { FileName = executable!, Arguments = args, WorkingDirectory = workspace.WorkTree, Environment = Clean(env) };
        async Task<(int Exit, string Out)> Execute(string name, ProcessLaunch launch, bool control = false)
        {
            if (control) launch = controls.Wrap(launch, workspace, profile);
            var step = $"{++count:D2}-{name}";
            await Save(step + "-launch.json", new { launch.FileName, launch.Arguments, launch.WorkingDirectory });
            using var process = Start(launch);
            // Final confined commands have already been wrapped; register their process trees too.
            if (control) controls.Started(workspace, process.Id);
            else sandbox.Started(workspace, process.Id);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            var stdout = Capture(process.StandardOutput, deadline.Token);
            var stderr = Capture(process.StandardError, deadline.Token);
            var timeout = false;
            try { await Task.WhenAll(process.WaitForExitAsync(deadline.Token), stdout, stderr); timeout = deadline.IsCancellationRequested; }
            catch (OperationCanceledException) { timeout = true; }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                await Save(step + ".json", new { launch.FileName, launch.Arguments, stdout = await stdout, stderr = await stderr, exit = process.ExitCode, timeout });
            }
            Assert.False(timeout, name + " exceeded 45 seconds");
            return (process.ExitCode, await stdout);
        }
        Task Save(string name, object value) => File.WriteAllTextAsync(Path.Combine(evidence, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
    }

    private static Dictionary<string, string?> Clean(Dictionary<string, string?> own)
    {
        // Positive controls must not see personal credentials/config. Keep only platform process necessities.
        var env = Environment.GetEnvironmentVariables().Keys.Cast<string>().ToDictionary(k => k, _ => (string?)null);
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "PATHEXT", "COMSPEC", "TMP", "TEMP", "TMPDIR" }) env[name] = Environment.GetEnvironmentVariable(name);
        foreach (var pair in own) env[pair.Key] = pair.Value;
        return env;
    }
    private static Process Start(ProcessLaunch launch)
    {
        var start = new ProcessStartInfo(launch.FileName) { WorkingDirectory = launch.WorkingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in launch.Arguments) start.ArgumentList.Add(arg);
        foreach (var pair in launch.Environment) { if (pair.Value is null) start.Environment.Remove(pair.Key); else start.Environment[pair.Key] = pair.Value; }
        return Process.Start(start)!;
    }
    internal static async Task<string> Capture(StreamReader reader, CancellationToken deadline)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        const int limit = 1024 * 1024;
        var truncated = false;
        try
        {
            int size;
            while ((size = await reader.ReadAsync(buffer.AsMemory(), deadline)) > 0)
            {
                var keep = Math.Min(size, limit - result.Length);
                result.Append(buffer, 0, keep);
                truncated |= keep < size;
                // Continue draining after the evidence cap so the child cannot block on a full pipe.
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        if (truncated) result.Append("\n[proof output truncated after 1048576 characters]\n");
        return result.ToString();
    }
    private static int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
}
