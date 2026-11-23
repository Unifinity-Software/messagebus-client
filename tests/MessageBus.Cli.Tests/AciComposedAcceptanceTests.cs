using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using MessageBus.Attachments.Client;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Agents;
using MessageBus.Cli.Runner.Sandbox;
using MessageBus.Contracts.Attachments;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed partial class ComposedAttachmentCallerTests
{
    [Fact]
    public async Task Protected_Codex_uses_real_MCP_to_return_code_and_files_while_other_wrapped_tools_run()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("MBUS_TEST_ACI_SANDBOX") == "1",
            "Requires the non-root Linux image, official Codex, and published mbus MCP command.");
        await using var f = new Fixture(machineAuthentication: true);
        f.Machine.Env["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        var options = new RunIsolationOptions
        {
            RequireAciSandbox = true, HomeDirectory = f.Machine.Home,
            WorkRoot = Path.Combine(f.Machine.Home, "runs"), RunTools = true, LimitNetwork = true,
            AciIdentityHost = "runner-fixture.blob.core.windows.net",
            ProbeAgents = [new("codex", "/usr/local/bin/codex")],
        };
        var probe = new ProcDeniedProbe();
        var sandbox = AciSandbox.TryCreate(options, out var why, probe);
        Assert.True(sandbox is not null, why + "\n" + probe.Failures);
        Assert.NotNull(sandbox);
        Assert.True(sandbox.AgentProblem(AgentKind.Codex) is null, sandbox.AgentProblem(AgentKind.Codex) + "\n" + probe.Failures);
        var sockets = AciSandbox.CreateSocketDirectory();
        f.Run.PrivateSocketDirectory = sockets;
        f.Run.AgentKind = AgentKind.Codex;
        sandbox.Prepare(f.Run.Root, f.Run.AgentKind);
        using var network = sandbox.StartNetwork(sockets, new HostAllowList([]), TextWriter.Null, "acceptance");
        Process? agent = null;
        var output = new ConcurrentQueue<string>();
        Task<int>? exited = null;
        try
        {
            await Git.RunAsync(f.Run.WorkTree, ["init", "--quiet", "--initial-branch=main"], Ct);
            await Git.RunAsync(f.Run.WorkTree, ["add", "."], Ct);
            await Git.RunAsync(f.Run.WorkTree, ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.test",
                "-c", "commit.gpgsign=false", "commit", "--quiet", "-m", "Acceptance"], Ct);
            var head = await Git.CommitIdAsync(f.Run.WorkTree, "HEAD", Ct);
            var wrappedGit = 0;
            var profile = new PermissionProfile { Name = "worker", CanEditFiles = true };
            var request = new RunToolsRequest(f.Run, f.Delivery, profile, launch =>
            {
                Assert.NotNull(exited);
                Assert.False(exited.IsCompleted, "Code packaging must run while the actual Codex guest remains alive.");
                Interlocked.Increment(ref wrappedGit);
                return sandbox.Wrap(launch, f.Run.Root, sockets, canEditFiles: profile.CanEditFiles, useUserspace: true);
            });
            await using var tools = await RunToolsServer.Create(f.Context)!.StartAsync(request, Ct);
            var input = f.SettleWithFile() with
            {
                Attach = [new AttachSpec { Format = AttachmentFormat.GitBundle, Path = f.Run.WorkTree, Branch = "main", Base = "" },
                    new AttachSpec { Format = AttachmentFormat.File, Path = Path.Combine(f.Run.WorkTree, "file.txt") }],
            };
            var home = Directory.CreateDirectory(Path.Combine(f.Run.Root, "home")).FullName;
            var codex = new CodexAdapter("fixture", mbus: new("/usr/local/bin/mbus", ["mcp"]), codexHome: Path.Combine(home, ".codex"))
                .BuildLaunch(new AgentLaunchContext("Return the fixture through MCP.", f.Run.PromptFile, profile, f.Run.WorkTree,
                    McpEnvironment: tools.Variables, RunDirectory: f.Run.Root)
                { ExternalFilePolicyEnforced = sandbox.EnforcesFilePolicy(f.Run.Root) });
            Assert.Null(codex.CannotStart);
            var script = Path.Combine(home, "acceptance.mjs");
            await File.WriteAllTextAsync(script, CodexMcpAcceptance, Ct);
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var launch = sandbox.Wrap(new ProcessLaunch
            {
                FileName = "/usr/local/bin/node", WorkingDirectory = f.Run.WorkTree,
                Arguments = [script, f.Run.Root, JsonSerializer.Serialize(input, MessageBusJson.Options),
                    JsonSerializer.Serialize(codex.Arguments), codex.StandardInput ?? ""],
                Environment = tools.Variables.ToDictionary(p => p.Key, p => (string?)p.Value),
            }, f.Run.Root, sockets, canEditFiles: profile.CanEditFiles, useUserspace: true);
            var start = new ProcessStartInfo(launch.FileName)
            {
                WorkingDirectory = launch.WorkingDirectory, UseShellExecute = false,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
            start.Environment.Clear();
            foreach (var (name, value) in launch.Environment) if (value is not null) start.Environment[name] = value;
            agent = new Process { StartInfo = start };
            void Capture(string? line)
            {
                if (line is null) return;
                output.Enqueue(line.Length <= 16_384 ? line : line[..16_384]);
                while (output.Count > 200) output.TryDequeue(out _);
                if (line == "codex-mcp-returned") ready.TrySetResult();
            }
            agent.OutputDataReceived += (_, e) => Capture(e.Data);
            agent.ErrorDataReceived += (_, e) => Capture(e.Data);
            Assert.True(agent.Start());
            agent.BeginOutputReadLine();
            agent.BeginErrorReadLine();
            exited = ExitAsync(agent);
            await Task.WhenAny(ready.Task, exited).WaitAsync(TimeSpan.FromSeconds(90), Ct);
            Assert.True(ready.Task.IsCompletedSuccessfully, string.Join('\n', output) + f.Diagnostics);
            Assert.False(exited.IsCompleted);
            Assert.True(wrappedGit > 0, "The MCP code return must execute production sandbox-wrapped git.");
            var sent = Assert.Single(f.Settles);
            var code = Assert.Single(sent.Attachments, a => a.Format == AttachmentFormat.GitBundle);
            var file = Assert.Single(sent.Attachments, a => a.Format == AttachmentFormat.File);
            f.AssertCiphertext(code);
            f.AssertCiphertext(file);
            Assert.Equal("attachment plaintext", await f.Open(file));
            var bundle = Path.Combine(f.Run.Root, "returned.bundle");
            await File.WriteAllBytesAsync(bundle, await f.OpenBytes(code), Ct);
            Assert.Equal(head + " refs/heads/main", (await Git.RunAsync(f.Run.WorkTree, ["bundle", "list-heads", bundle], Ct)).Trim());
            Assert.Equal("kept answer payload", f.OpenReply(sent).Summary);
            Assert.All(f.Blobs, f.AssertBlob);
            using (var concurrent = AgentProcess.Start(sandbox.Wrap(new ProcessLaunch
            {
                FileName = "/usr/bin/git", Arguments = ["rev-parse", "HEAD"], WorkingDirectory = f.Run.WorkTree,
            }, f.Run.Root, sockets, canEditFiles: profile.CanEditFiles, useUserspace: true)))
            {
                Assert.Equal(0, await concurrent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct));
                Assert.Contains(head, concurrent.Tail());
                Assert.False(exited.IsCompleted);
            }
            await agent.StandardInput.WriteLineAsync("finish".AsMemory(), Ct);
            await agent.StandardInput.FlushAsync(Ct);
            agent.StandardInput.Close();
            try { Assert.Equal(0, await exited.WaitAsync(TimeSpan.FromSeconds(30), Ct)); }
            catch (TimeoutException ex) { throw new TimeoutException(string.Join('\n', output), ex); }
            Assert.Contains("codex-alive-after-concurrent-tool", output);
            Assert.Contains(output, line => line.Contains("mcp_tool_call", StringComparison.Ordinal));
            Assert.Contains(output, line => line.Contains("acceptance-complete", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                try
                {
                    if (agent is not null)
                    {
                        try { if (!agent.HasExited) agent.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) { /* The process exited while stopping it. */ }
                        if (exited is not null) await exited.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
                    }
                }
                finally { agent?.Dispose(); }
            }
            finally
            {
                // Test cancellation or a failed process wait cannot skip the runtime ownership cleanup.
                // Keep sockets/journals for diagnosis if that cleanup cannot be verified.
                await sandbox.EndAsync(f.Run.Root).WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
                Directory.Delete(sockets, recursive: true);
            }
        }

        static async Task<int> ExitAsync(Process process)
        {
            await process.WaitForExitAsync(CancellationToken.None);
            return process.ExitCode;
        }
    }

    private sealed class ProcDeniedProbe : IProbeRunner
    {
        private bool _denied;
        internal string Failures { get; private set; } = "";
        public ProbeOutcome Run(ProbeCommand command)
        {
            if (!_denied)
            {
                Assert.Contains("--proc", command.Launch.Arguments);
                _denied = true;
                return new(1, "bwrap: Can't mount proc on /proc: Operation not permitted\n");
            }
            var result = new ProcessProbeRunner().Run(command);
            if (result.ExitCode != 0) Failures += command.Step + ": " + result.Output + "\n";
            return result;
        }
    }

    // A local Responses endpoint drives the unchanged Codex binary through its real MCP client.
    // No provider credentials/model calls. The final response waits on an event, keeping Codex alive
    // while the host verifies its returned artifacts and starts another production OCI launch.
    private const string CodexMcpAcceptance = """
        import fs from 'node:fs';
        import http from 'node:http';
        import { spawn } from 'node:child_process';
        const [run, args, codexArgs, prompt] = process.argv.slice(2);
        const home = run + '/home';
        fs.mkdirSync(home + '/.codex', {recursive:true});
        let calls = 0;
        let release;
        const finished = new Promise(resolve => release = resolve);
        process.stdin.once('data', release);
        const server = http.createServer(async (request, response) => {
          let raw = ''; for await (const chunk of request) raw += chunk;
          const body = JSON.parse(raw);
          const tools = (body.tools ?? []).flatMap(tool => tool.type === 'namespace'
            ? (tool.tools ?? []).map(child => ({...child, namespace:tool.name})) : [tool]);
          let item;
          if (++calls === 1) {
            const tool = tools.find(tool => /(?:__|^)settle$/.test(tool.name));
            if (!tool) { console.error(JSON.stringify(tools.map(t=>({type:t.type,name:t.name})))); response.writeHead(500); response.end(); return; }
            item = {type:'function_call',id:'fc_mcp',call_id:'call_mcp',name:tool.name,arguments:args,
              ...(tool.namespace ? {namespace:tool.namespace} : {})};
          } else {
            console.log('codex-mcp-returned'); await finished;
            if (child.exitCode !== null || child.signalCode !== null) { response.destroy(); return; }
            console.log('codex-alive-after-concurrent-tool');
            item = {type:'message',id:'msg_done',role:'assistant',status:'completed',content:[{type:'output_text',text:'acceptance-complete',annotations:[]}]};
          }
          const result = {id:'resp_'+calls,object:'response',created_at:1,model:'fixture',status:'completed',output:[item],usage:{input_tokens:1,output_tokens:1,total_tokens:2}};
          response.writeHead(200, {'Content-Type':'text/event-stream'});
          const emit = (type,data) => response.write(`event: ${type}\ndata: ${JSON.stringify({type,...data})}\n\n`);
          emit('response.created',{response:{...result,status:'in_progress',output:[]}});
          emit('response.output_item.added',{output_index:0,item:item.type === 'function_call' ? {...item,arguments:''} : item});
          if (item.type === 'function_call') emit('response.function_call_arguments.delta',{output_index:0,item_id:item.id,delta:item.arguments});
          emit('response.output_item.done',{output_index:0,item}); emit('response.completed',{response:result}); response.end();
        });
        await new Promise(resolve => server.listen(0,'127.0.0.1',resolve));
        fs.writeFileSync(home+'/.codex/config.toml',`model_provider="fixture"\nmodel="fixture"\n[model_providers.fixture]\nname="Offline acceptance"\nbase_url="http://127.0.0.1:${server.address().port}/v1"\nwire_api="responses"\nrequires_openai_auth=false\nrequest_max_retries=0\nstream_max_retries=0\n`);
        const child = spawn('/usr/local/bin/codex',JSON.parse(codexArgs),
          {cwd:run+'/work',env:{...process.env,HOME:home,PATH:'/usr/local/bin:/usr/bin:/bin'},stdio:['pipe','pipe','inherit']});
        child.stdin.end(prompt);
        child.stdout.pipe(process.stdout);
        const code = await new Promise(resolve => { child.once('error',()=>resolve(1)); child.once('exit',code=>resolve(code??1)); });
        await new Promise(resolve=>server.close(resolve)); process.exitCode = code;
        """;
}
