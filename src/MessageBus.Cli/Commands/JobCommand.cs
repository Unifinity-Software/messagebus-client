using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Scripts;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Crypto;
using MessageBus.Mcp;

namespace MessageBus.Cli.Commands;

internal static class JobCommand
{
    internal static Command Create(CliHost host, CommonOptions options)
    {
        var command = ScriptCommands.Project(host, options, "job");
        command.Description = "Start and follow workflow jobs, or coordinate hands-on jobs.";
        command.Subcommands.Add(Start(host, options));
        command.Subcommands.Add(List(host, options));
        command.Subcommands.Add(Show(host, options));
        command.Subcommands.Add(Change(host, options, false));
        command.Subcommands.Add(Change(host, options, true));
        command.Subcommands.Add(Open(host, options));
        return command;
    }

    private static Command Start(CliHost host, CommonOptions options)
    {
        var item = new Argument<string>("work-item") { Description = "The work item, for example gh:owner/repo#42." };
        var workflow = new Option<string>("--workflow") { Required = true, Description = "The published workflow's name or id." };
        var instructions = new Option<string?>("--instructions") { Description = "Extra context for this job." };
        var limit = new Option<decimal?>("--limit") { Description = "The job's spending limit in USD." };
        var runsOn = new Option<string?>("--runs-on") { Description = "Use workspace or owner-runners." };
        runsOn.AcceptOnlyFromAmong("workspace", "owner-runners");
        var json = ScriptRunner.JsonOption();
        var command = new Command("start", "Start a job and print its dashboard link.") { item, workflow, instructions, limit, runsOn, json };
        command.SetAction((parse, ct) => WorkflowHttp.RunAsync(host, parse, options, parse.GetValue(json), async api =>
        {
            if (parse.GetValue(limit) is < 0) throw new CliException("The spending limit cannot be negative.", ScriptExitCodes.InvalidInput);
            var selected = await api.ResolveWorkflowAsync(parse.GetValue(workflow)!, true, ct);
            var issue = await GitHubWorkItem.ReadAsync(parse.GetValue(item)!, host.Time, host.RunWorkItemCommand, ct);
            var body = JsonNode.Parse(MessageBusJson.Serialize(new StartJobRequest
            {
                WorkItem = parse.GetValue(item)!, WorkflowId = selected["id"]!.ToString(),
                Instructions = parse.GetValue(instructions), LimitUsd = parse.GetValue(limit), RunsOn = parse.GetValue(runsOn),
                WorkItemTitle = issue?.Title, WorkItemBody = issue?.Body,
            }));
            var result = await api.SendAsync(HttpMethod.Post, "jobs", body, ct);
            api.Write(result, () => host.Out.WriteLine(api.Link("jobs", result!["id"]!.ToString())));
            return 0;
        }, ct));
        return command;
    }

    private static Command List(CliHost host, CommonOptions options)
    {
        var state = new Option<string?>("--state") { Description = "Only jobs in this state." };
        state.AcceptOnlyFromAmong("running", "needs-you", "succeeded", "failed", "cancelled");
        var workflow = new Option<string?>("--workflow") { Description = "Only jobs for this workflow name or id." };
        var mine = new Option<bool>("--mine") { Description = "Only jobs started by you." };
        var json = ScriptRunner.JsonOption();
        var command = new Command("list", "List jobs, with optional state, workflow and starter filters.") { state, workflow, mine, json };
        command.SetAction((parse, ct) => WorkflowHttp.RunAsync(host, parse, options, parse.GetValue(json), async api =>
        {
            var query = new List<string>();
            if (parse.GetValue(state) is { } filter) query.Add("state=" + WorkflowHttp.Escape(filter));
            if (parse.GetValue(workflow) is { } name)
            {
                var selected = await api.ResolveWorkflowAsync(name, false, ct);
                query.Add("workflowId=" + WorkflowHttp.Escape(selected["id"]!.ToString()));
            }
            if (parse.GetValue(mine))
            {
                if (!await TargetCapabilities.SupportsWorkflowsAsync(api.Http, ct)) throw new CliException(WorkflowHttp.Disabled);
                using var response = await api.Http.GetAsync("api/caller", ct);
                await WorkflowHttp.EnsureSuccessAsync(response, ct);
                var caller = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
                var subject = caller?["subjectId"]?.ToString();
                if (string.IsNullOrWhiteSpace(subject)) throw new CliException("The server did not identify who started your jobs.");
                query.Add("startedBy=" + WorkflowHttp.Escape(subject));
            }
            var jobs = await api.GetAsync("jobs" + (query.Count > 0 ? "?" + string.Join('&', query) : ""), ct);
            api.Write(jobs, () =>
            {
                foreach (var job in jobs!.AsArray())
                    host.Out.WriteLine($"{job!["id"]}  {job["state"]}  {job["title"]}  {job["workflow"]?["name"]}");
            });
            return 0;
        }, ct));
        return command;
    }

    private static Command Show(CliHost host, CommonOptions options)
    {
        var id = new Argument<string>("id") { Description = "The job id." };
        var json = ScriptRunner.JsonOption();
        var command = new Command("show", "Show a job and each step's progress.") { id, json };
        command.SetAction((parse, ct) => WorkflowHttp.RunAsync(host, parse, options, parse.GetValue(json), async api =>
        {
            var job = await api.GetAsync($"jobs/{WorkflowHttp.Escape(parse.GetValue(id)!)}", ct);
            api.Write(job, () => WriteJob(host, job!));
            return 0;
        }, ct));
        return command;
    }

    private static void WriteJob(CliHost host, JsonNode job)
    {
        host.Out.WriteLine($"{job["title"]} ({job["id"]})");
        host.Out.WriteLine($"Work item: {job["workItem"]?["key"]}");
        host.Out.WriteLine(job["workflow"] is { } workflow
            ? $"Workflow: {workflow["name"]}, version {workflow["version"]}" : "Hands-on job.");
        host.Out.WriteLine($"State: {job["state"]}. Spent: {job["spentUsd"]} / {job["limitUsd"]?.ToString() ?? "no limit"} USD.");
        foreach (var step in job["steps"]?.AsArray() ?? [])
        {
            var name = job["path"]?["steps"]?.AsArray().FirstOrDefault(s => s?["id"]?.ToString() == step!["stepId"]?.ToString())?["name"]
                ?? step!["stepId"];
            host.Out.WriteLine($"  {name} ({step!["stepId"]}), round {step["round"]}: {step["state"]}, outcome {step["outcome"]?.ToString().Replace('-', ' ') ?? "pending"}; " +
                $"{step["agent"]?.ToString() ?? "unassigned"} on {step["machineId"]?.ToString() ?? "unassigned"}; {Duration(host, step)}");
            if (step["waitingOn"] is { } waiting) host.Out.WriteLine("    Waiting on " + Waiting(waiting));
        }
    }

    private static string Duration(CliHost host, JsonNode step)
    {
        if (!DateTimeOffset.TryParse(step["startedAt"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)) return "not started";
        var end = DateTimeOffset.TryParse(step["endedAt"]?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var ended) ? ended : host.Time.GetUtcNow();
        return Math.Max(0, (end - start).TotalSeconds).ToString("0", CultureInfo.InvariantCulture) + "s";
    }

    private static string Waiting(JsonNode waiting) => waiting["kind"]?.ToString() switch
    {
        "checks" => $"checks for PR #{waiting["pullRequest"]}: {waiting["done"]}/{waiting["total"]} done.",
        "person" => $"{waiting["who"]?["name"] ?? waiting["who"]?["id"]}.",
        "agent" => $"an agent with role {waiting["role"]}.",
        "merge" => $"PR #{waiting["pullRequest"]} to merge.",
        "deployment" => $"deployment to {waiting["environment"]}.",
        "delay" => $"{waiting["until"]}.",
        _ => $"{waiting["kind"]}.",
    };

    private static Command Change(CliHost host, CommonOptions options, bool retry)
    {
        var id = new Argument<string>("id") { Description = "The job id." };
        var step = new Option<string>("--step") { Required = true, Description = "The step id to retry." };
        var json = ScriptRunner.JsonOption();
        var command = new Command(retry ? "retry" : "stop", retry ? "Retry a job step." : "Stop a job.") { id, json };
        if (retry) command.Options.Add(step);
        command.SetAction((parse, ct) => WorkflowHttp.RunAsync(host, parse, options, parse.GetValue(json), async api =>
        {
            var route = $"jobs/{WorkflowHttp.Escape(parse.GetValue(id)!)}" + (retry ? $"/steps/{WorkflowHttp.Escape(parse.GetValue(step)!)}/retry" : "/stop");
            var result = await api.SendAsync(HttpMethod.Post, route, null, ct);
            api.Write(result, () => host.Out.WriteLine(retry ? "Step retry requested." : "Job stop requested."));
            return 0;
        }, ct));
        return command;
    }

    private static Command Open(CliHost host, CommonOptions options)
    {
        var id = new Argument<string>("id") { Description = "The job id." };
        var output = new Option<string?>("--out") { Description = "Save opened exhibits here (default ./mbus-exhibits/<job id>)." };
        var json = ScriptRunner.JsonOption();
        var command = new Command("open", "Fetch and open locked exhibits on this machine.") { id, output, json };
        command.SetAction((parse, ct) => WorkflowHttp.RunAsync(host, parse, options, parse.GetValue(json), async api =>
        {
            var jobId = parse.GetValue(id)!;
            var route = $"jobs/{WorkflowHttp.Escape(jobId)}/exhibits";
            var exhibits = (await api.GetAsync(route, ct))!.AsArray();
            var folder = Path.GetFullPath(parse.GetValue(output) ?? Path.Combine("mbus-exhibits", SafeName(jobId)), host.CurrentDirectory);
            using var keys = new MachineKeys(api.Http, api.Context.Settings.Stored.WorkspaceId!, api.Context.Settings.Stored.MachineId,
                api.Context.Store.Directory, host.Error, host.Time, api.Context.Store.LoadKeyPem);
            var results = new JsonArray();
            foreach (var exhibit in exhibits)
            {
                var result = exhibit!.DeepClone();
                var link = api.Link("jobs", jobId);
                result["url"] = link;
                if (exhibit["locked"]?.GetValue<bool>() == true)
                {
                    DurableProfileFile.RejectDirectoryAliases(folder);
                    Directory.CreateDirectory(folder);
                    var name = SafeName(exhibit["id"]!.ToString()) + "-" + SafeName(exhibit["name"]!.ToString());
                    var destination = Path.Combine(folder, name);
                    await OpenExhibitAsync(api, keys, route + "/" + WorkflowHttp.Escape(exhibit["id"]!.ToString()), destination, ct);
                    result["path"] = destination;
                }
                results.Add(result);
            }
            api.Write(results, () =>
            {
                foreach (var exhibit in results)
                    host.Out.WriteLine($"{exhibit!["name"]}: {exhibit["path"]?.ToString() ?? exhibit["url"]!.ToString()}");
            });
            return 0;
        }, ct));
        return command;
    }

    private static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new CliException("The server sent an unsafe exhibit file name.");
        return name;
    }

    private static async Task OpenExhibitAsync(WorkflowHttp api, MachineKeys keys, string route, string destination, CancellationToken ct)
    {
        var scratch = destination + "." + Guid.NewGuid().ToString("N");
        var cipherPath = scratch + ".sealed";
        var plainPath = scratch + ".tmp";
        try
        {
            await using (var cipher = PrivateFile(cipherPath))
            {
                using var response = await api.RequestAsync(HttpMethod.Get, route, null, ct);
                await using var content = await response.Content.ReadAsStreamAsync(ct);
                if (await ReadLineAsync(content, 64, ct) != "mbus-sealed-exhibit/1")
                    throw new CliException("Unsupported locked exhibit format; expected mbus-sealed-exhibit/1.");
                var encryption = MessageBusJson.Deserialize<EncryptionInfo>(await ReadLineAsync(content, 16384, ct))
                    ?? throw new CliException("The locked exhibit has no encryption information.");
                await content.CopyToAsync(cipher, ct);
                await using var plaintext = PrivateFile(plainPath);
                await keys.WithRingAsync(async ring =>
                {
                    cipher.Position = 0;
                    plaintext.Position = 0;
                    plaintext.SetLength(0);
                    await MessageCrypto.OpenAsync(cipher, plaintext, encryption, ring, ct);
                    return true;
                }, ct);
            }
            File.Move(plainPath, destination); // Never replace an existing local file.
        }
        finally
        {
            File.Delete(cipherPath);
            File.Delete(plainPath);
        }
    }

    private static FileStream PrivateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, Share = FileShare.None, Options = FileOptions.Asynchronous };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static async Task<string> ReadLineAsync(Stream stream, int limit, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < limit)
        {
            if (await stream.ReadAsync(one, ct) == 0) break;
            if (one[0] == '\n') return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(one[0]);
        }
        throw new CliException("The locked exhibit header is missing, truncated or too long.");
    }
}
