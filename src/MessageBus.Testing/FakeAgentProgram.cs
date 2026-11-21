// A fake coding agent for the runner tests. It is started exactly the way the runner starts the real
// CLIs (same arguments, prompt on stdin) and answers in the same JSON shapes:
//   claude -p --output-format stream-json --verbose ...   (one JSON event per line)
//   claude -p --output-format json ...                    (one result object)
//   codex exec --json ... -                               (JSONL events)
// What it does is set by lines in the prompt (the tests put them in the role instructions):
//   fake-agent: mode=success|claim|settle-failed|hang
// It writes its delivery id, arguments and working directory to fake-agent.txt, fake-agent-args.txt and fake-agent-cwd.txt.
//   fake-agent: wait=<path>      wait until that file exists before finishing
//   fake-agent: settle=yes       call "settle" the way the local MCP server records it
//   fake-agent: settle=mcp-env   as codex: settle like the local MCP server would when Codex starts it, which sees only
//                                what `-c mcp_servers.mbus.env.NAME=value` gives it (written to mcp-env.txt), not the agent's env;
//                                the server's `-c mcp_servers.mbus.command=` / `.args=`, if given, go to mcp-server.txt
//   fake-agent: mode=signed-out  answer like Claude Code that can't use its sign-in ("Not logged in"), exit 1
//   fake-agent: mcp=<mbus>       with mbus-home=<dir>: start `<mbus> mcp`, read the task with inbox, settle (with a reply) through it
//   fake-agent: patch=<file>     with mcp: apply that patch to the run's checkout, commit it, and send the branch back with the
//                                reply as a git bundle (no base: it starts after the code the task came with) (the original issue)
// Probes for the run isolation tests (the original issue):
//   fake-agent: mode=orphan      start a background process that outlives the agent, then finish
//   fake-agent: env=A,B          write the values of those variables to env.txt
//   fake-agent: git-credential=<url>|<url>   ask git for each URL's password, write them to credentials.txt
//   fake-agent: read=<path>      read that file; fail the task (an error result, exit 1) if it can't
//   fake-agent: write=<path>     write that file; fail the task if it can't
// Probe for the network limit (the original issue):
//   fake-agent: fetch=<url>      GET it as a program that honours the proxy variables would; the status (or error) goes to fetch.txt
// Developer work for mbus doctor --smoke (the original issue), in this order:
//   fake-agent: replace=<file>|<old>|<new>   change that text in that file (in the working directory)
//   fake-agent: run=<command>                run it with the platform's shell; a failure fails the task (exit 1)
//   fake-agent: commit=<message>             commit every change with that message, as "Fake agent"
// Probe for a role's most cost per run (the original issue), stream-json only:
//   fake-agent: calls=<id>:<output tokens>,...   one assistant event per item, with that message id and usage (as Claude
//                                Code repeats a message's usage on each of its content blocks), before anything else
using System.Diagnostics;
using System.Text.Json;

namespace MessageBus.Testing;

/// <summary>A stand-in coding agent for runner and integration tests.</summary>
public static class FakeAgentProgram
{
    /// <summary>Runs the fake agent with the supplied command-line arguments and returns its exit code.</summary>
    public static int Run(string[] args)
    {
        if (args is ["--child"] or ["--daemon"])
        {
            // The grandchild in the process-tree test: does nothing until it is killed. As a daemon (macOS and Linux, the original issue) it
            // first leaves the agent's session and process group, the way a server started in the background does.
            if (args[0] == "--daemon" && !OperatingSystem.IsWindows())
            {
                _ = Native.setsid();
            }
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        var codex = args.Length > 0 && args[0] == "exec";
        var streamJson = args.Contains("stream-json");
        var prompt = Console.In.ReadToEnd();
        var directives = prompt.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("fake-agent:", StringComparison.Ordinal))
            .Select(l => l["fake-agent:".Length..].Trim().Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1]);
        var mode = directives.GetValueOrDefault("mode", "success");
        var cwd = Environment.CurrentDirectory;
        var stdout = Console.Out;

        // Where it ran and how it was started (a resumed session's --resume / exec resume, the original issue): in its working directory,
        // or, when that is read only (a resumed session's own folder in the Linux sandbox), in the run's folder.
        var marks = Writable(cwd) ? cwd : Environment.GetEnvironmentVariable("MBUS_RUN_DIR") ?? cwd;
        File.WriteAllText(Path.Combine(marks, "fake-agent.txt"), Environment.GetEnvironmentVariable("MBUS_DELIVERY_ID") ?? "");
        File.WriteAllLines(Path.Combine(marks, "fake-agent-args.txt"), args);
        File.WriteAllText(Path.Combine(marks, "fake-agent-cwd.txt"), cwd);

        if (streamJson && directives.TryGetValue("calls", out var calls))
        {
            foreach (var call in calls.Split(','))
            {
                var (id, tokens) = (call.Split(':')[0], long.Parse(call.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture));
                Line(new
                {
                    type = "assistant",
                    message = new
                    {
                        id, role = "assistant", model = "claude-sonnet-4-5",
                        content = new[] { new { type = "text", text = "Working on " + id } },
                        usage = new { input_tokens = 0, cache_read_input_tokens = 0, cache_creation_input_tokens = 0, output_tokens = tokens },
                    },
                });
            }
            stdout.Flush();
        }
        if (directives.TryGetValue("env", out var names))
        {
            File.WriteAllLines(Path.Combine(cwd, "env.txt"), names.Split(',').Select(n => $"{n}={Environment.GetEnvironmentVariable(n) ?? "<unset>"}"));
        }
        if (directives.TryGetValue("git-credential", out var urls))
        {
            File.WriteAllLines(Path.Combine(cwd, "credentials.txt"), urls.Split('|').Select(u => $"{u} {GitPassword(u) ?? "<none>"}"));
        }
        if (directives.TryGetValue("fetch", out var fetchUrl))
        {
            // HttpClient's default proxy comes from HTTP_PROXY / HTTPS_PROXY, like curl's or git's.
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            string answer;
            try
            {
                using var response = http.GetAsync(fetchUrl).GetAwaiter().GetResult();
                answer = ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture) + " " + response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim();
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                answer = "error " + e.Message;
            }
            File.WriteAllText(Path.Combine(cwd, "fetch.txt"), answer);
        }
        if (directives.TryGetValue("read", out var readPath) && Attempt(() => File.ReadAllText(readPath)) is { } readError)
        {
            return Fail($"Couldn't read {readPath}: {readError}");
        }
        if (directives.TryGetValue("write", out var writePath) && Attempt(() => File.WriteAllText(writePath, "from the agent")) is { } writeError)
        {
            return Fail($"Couldn't write {writePath}: {writeError}");
        }
        if (mode == "signed-out")
        {
            // What Claude Code prints in -p mode when it has no usable sign-in (the keychain over SSH on macOS).
            Line(new { type = "result", subtype = "success", is_error = true, duration_ms = 30, result = "Not logged in · Please run /login", total_cost_usd = 0,
                usage = new { input_tokens = 0, output_tokens = 0 } });
            stdout.Flush();
            return 1;
        }
        if (codex && mode is "codex-missing-auth" or "codex-ineligible")
        {
            var error = mode == "codex-missing-auth"
                ? "unexpected status 401 Unauthorized: Missing bearer or basic authentication"
                : "Codex cannot be used by an organization enrolled in the monthly API Credits benefit with ChatGPT subscriptions.";
            // Exercise both the JSON event and stderr paths with a deliberately echoed test value.
            var diagnostic = error + " Diagnostic: " + Environment.GetEnvironmentVariable("CODEX_API_KEY");
            if (mode == "codex-missing-auth") Console.Error.WriteLine(diagnostic);
            else Line(new { type = "turn.failed", error = new { message = diagnostic } });
            return 1;
        }
        if (mode == "orphan")
        {
            // Started the way a daemon escapes its parent (on Windows through the shell, so it doesn't hold
            // the runner's output pipe), and left running when the agent exits.
            // Elsewhere with its own output, so it doesn't hold the runner's either, and in a session of its own.
            var orphan = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--daemon")
            {
                UseShellExecute = OperatingSystem.IsWindows(),
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = !OperatingSystem.IsWindows(),
                RedirectStandardOutput = !OperatingSystem.IsWindows(),
                RedirectStandardError = !OperatingSystem.IsWindows(),
            })!;
            File.WriteAllText(Path.Combine(cwd, "pids.txt"), orphan.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (directives.TryGetValue("wait", out var waitFile))
        {
            while (!File.Exists(waitFile))
            {
                Thread.Sleep(20);
            }
        }

        if (directives.TryGetValue("replace", out var replace) && replace.Split('|') is [var file, var before, var after])
        {
            var path = Path.Combine(cwd, file);
            File.WriteAllText(path, File.ReadAllText(path).Replace(before, after, StringComparison.Ordinal));
        }
        if (directives.TryGetValue("run", out var command))
        {
            var shell = OperatingSystem.IsWindows() ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/s", "/c", command } } : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", command } };
            shell.UseShellExecute = false;
            shell.WorkingDirectory = cwd;
            shell.RedirectStandardOutput = true;
            using var run = Process.Start(shell)!;
            var said = run.StandardOutput.ReadToEnd();
            run.WaitForExit();
            if (run.ExitCode != 0)
            {
                return Fail($"{command} failed: {said.Trim()}");
            }
        }
        if (directives.TryGetValue("commit", out var commitMessage))
        {
            Git("add", "--all");
            Git("-c", "user.name=Fake agent", "-c", "user.email=fake-agent@messagebus.invalid", "-c", "commit.gpgsign=false",
                "commit", "--quiet", "--no-verify", "-m", commitMessage);
        }

        if (mode == "hang")
        {
            var self = Environment.ProcessPath!;
            var child = Process.Start(new ProcessStartInfo(self, "--child") { UseShellExecute = false })!;
            File.WriteAllText(Path.Combine(cwd, "pids.txt"), $"{Environment.ProcessId} {child.Id}");
            Progress("Building the solution");
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        if (directives.TryGetValue("mcp", out var mbus))
        {
            // Like a real agent: start the local MCP server the way its MCP settings say (`mbus mcp`), read the task
            // with `inbox` and finish it with `settle`, answering with what it read and which files the run had.
            var server = Process.Start(new ProcessStartInfo(mbus, "mcp")
            {
                UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                Environment = { ["MBUS_HOME"] = directives["mbus-home"] },
            })!;
            server.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
            server.BeginErrorReadLine();
            var nextId = 0;
            System.Text.Json.Nodes.JsonNode Rpc(string method, object parameters)
            {
                var id = ++nextId;
                server.StandardInput.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
                server.StandardInput.Flush();
                while (server.StandardOutput.ReadLine() is { } line)
                {
                    var message = System.Text.Json.Nodes.JsonNode.Parse(line)!;
                    if ((int?)message["id"] == id) return message["result"] ?? throw new InvalidOperationException(line);
                }
                throw new InvalidOperationException("mbus mcp ended.");
            }
            Rpc("initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "claude-code", version = "2.0.0" } });
            server.StandardInput.WriteLine(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }));
            var inbox = Rpc("tools/call", new { name = "inbox", arguments = new { } });
            var task = inbox["structuredContent"]!["messages"]![0]!;
            var read = (string?)task["body"]?["summary"] ?? "(nothing)";
            var files = string.Join(",", Directory.GetFiles(cwd).Select(Path.GetFileName).Where(f => f is not ("fake-agent.txt" or "fake-agent-args.txt" or "fake-agent-cwd.txt")).Order());
            object[]? attach = null;
            var details = (string?)null;
            if (directives.TryGetValue("patch", out var patch))
            {
                // The change, as an agent would make it: applied to the checkout of the task's branch and committed there.
                Git("apply", "--index", "--whitespace=nowarn", patch);
                Git("-c", "user.name=Fake agent", "-c", "user.email=fake-agent@messagebus.invalid", "-c", "commit.gpgsign=false",
                    "commit", "--quiet", "--no-verify", "-m", "Apply " + Path.GetFileName(patch));
                attach = [new { format = "git-bundle", branch = Git("rev-parse", "--abbrev-ref", "HEAD") }];
                details = "Changed: " + Git("diff", "--name-only", "HEAD~1", "HEAD").ReplaceLineEndings(", ");
            }
            var settled = Rpc("tools/call", new
            {
                name = "settle",
                arguments = new
                {
                    threadId = (string)task["delivery"]!["threadId"]!,
                    result = new { status = "succeeded", summary = $"Read: {read}; files: {files}" },
                    reply = new { summary = "Answer: " + read, details },
                    attach,
                },
            });
            if (settled["isError"]?.GetValue<bool>() == true) Console.Error.WriteLine("settle failed: " + settled.ToJsonString());
            server.StandardInput.Close();
            server.WaitForExit();
        }

        // Codex starts MCP servers with a few of its own variables plus the server's configured env, nothing else.
        var mcpEnv = new Dictionary<string, string>(StringComparer.Ordinal);
        var mcpServer = new List<string>();
        for (var i = 0; i + 1 < args.Length; i++)
        {
            const string prefix = "mcp_servers.mbus.env.";
            if (args[i] == "-c" && args[i + 1].StartsWith(prefix, StringComparison.Ordinal) && args[i + 1].IndexOf('=') is > 0 and var eq)
            {
                // The value is a TOML basic string; its escapes are JSON's.
                mcpEnv[args[i + 1][prefix.Length..eq]] = JsonSerializer.Deserialize<string>(args[i + 1][(eq + 1)..])!;
            }
            else if (args[i] == "-c" && (args[i + 1].StartsWith("mcp_servers.mbus.command=", StringComparison.Ordinal) || args[i + 1].StartsWith("mcp_servers.mbus.args=", StringComparison.Ordinal)))
            {
                // command = a TOML string, args = a TOML array of strings: both read as JSON.
                var setting = args[i + 1]["mcp_servers.mbus.".Length..];
                var value = JsonSerializer.Deserialize<JsonElement>(setting[(setting.IndexOf('=') + 1)..]);
                mcpServer.Add($"{setting[..setting.IndexOf('=')]}={(value.ValueKind == JsonValueKind.Array ? string.Join(" ", value.EnumerateArray().Select(e => e.GetString())) : value.GetString())}");
            }
        }
        if (codex)
        {
            File.WriteAllLines(Path.Combine(cwd, "mcp-env.txt"), mcpEnv.OrderBy(v => v.Key, StringComparer.Ordinal).Select(v => $"{v.Key}={v.Value}"));
            File.WriteAllLines(Path.Combine(cwd, "mcp-server.txt"), mcpServer);
        }
        string? SettleVariable(string name) =>
            directives.GetValueOrDefault("settle") == "mcp-env" ? mcpEnv.GetValueOrDefault(name) : Environment.GetEnvironmentVariable(name);

        if (directives.GetValueOrDefault("settle") is "yes" or "mcp-env" || mode == "settle-failed")
        {
            var status = mode == "settle-failed" ? "failed" : "succeeded";
            var settle = new
            {
                threadId = SettleVariable("MBUS_THREAD_ID"),
                deliveryId = SettleVariable("MBUS_DELIVERY_ID"),
                result = new
                {
                    status,
                    summary = $"Worked in {cwd}",
                    refs = new[] { new { kind = "git.commit", sha = "0123abc" } },
                },
            };
            if (SettleVariable("MBUS_SETTLE_FILE") is { } settleFile)
            {
                File.WriteAllText(settleFile, JsonSerializer.Serialize(settle));
            }
        }

        Progress("Editing files");
        var finalText = mode == "claim" ? "All tests pass. The task succeeded." : $"Done in {cwd}";
        if (codex)
        {
            Line(new { type = "item.completed", item = new { id = "item_2", type = "agent_message", text = finalText } });
            Line(new { type = "turn.completed", usage = new { input_tokens = 24763, cached_input_tokens = 24448, output_tokens = 122 } });
        }
        else
        {
            Line(new
            {
                type = "result",
                subtype = "success",
                is_error = false,
                duration_ms = 4200,
                result = finalText,
                total_cost_usd = 0.0421,
                usage = new { input_tokens = 12, cache_creation_input_tokens = 500, cache_read_input_tokens = 2000, output_tokens = 300 },
                modelUsage = new Dictionary<string, object>
                {
                    ["claude-sonnet-4-5"] = new { inputTokens = 12, outputTokens = 300, cacheReadInputTokens = 2000, cacheCreationInputTokens = 500, costUSD = 0.0421 },
                },
            });
        }
        stdout.Flush();
        return mode == "claim" ? 2 : 0;

        void Progress(string text)
        {
            if (codex)
            {
                Line(new { type = "item.started", item = new { id = "item_1", type = "command_execution", command = text, status = "in_progress" } });
            }
            else if (streamJson)
            {
                Line(new { type = "assistant", message = new { role = "assistant", content = new[] { new { type = "text", text } } } });
            }
            stdout.Flush();
        }

        void Line(object value) => stdout.Write(JsonSerializer.Serialize(value) + "\n");

        int Fail(string message)
        {
            Line(new { type = "result", subtype = "error_during_execution", is_error = true, result = message });
            stdout.Flush();
            return 1;
        }

        static string? Attempt(Action action)
        {
            try
            {
                action();
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }
        }

        static string Git(params string[] arguments)
        {
            var info = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var git = Process.Start(info)!;
            git.StandardInput.Close();
            var errors = git.StandardError.ReadToEndAsync();
            var output = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            return git.ExitCode == 0 ? output.Trim() : throw new InvalidOperationException($"git {string.Join(' ', arguments)}: {errors.Result}");
        }

        static string? GitPassword(string url)
        {
            var info = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add("credential");
            info.ArgumentList.Add("fill");
            using var git = Process.Start(info)!;
            git.StandardInput.Write($"url={url}\n\n");
            git.StandardInput.Close();
            var errors = git.StandardError.ReadToEndAsync();
            var output = git.StandardOutput.ReadToEnd();
            git.WaitForExit();
            _ = errors.Result;
            return git.ExitCode != 0 ? null
                : output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("password=", StringComparison.Ordinal))?["password=".Length..];
        }

        static bool Writable(string dir)
        {
            try
            {
                var probe = Path.Combine(dir, ".fake-agent-probe");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

    }
}

internal static class Native
{
    [System.Runtime.InteropServices.DllImport("libc")]
    internal static extern int setsid();
}
