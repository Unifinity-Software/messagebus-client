using System.CommandLine;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Scripts;
using MessageBus.Cli.Server;
using MessageBus.Contracts.Mcp;
using MessageBus.Crypto;
using MessageBus.Mcp;

namespace MessageBus.Cli.Commands;

internal static class WorkflowCommand
{
    private const int MaxFileBytes = 64 * 1024;
    private const string FilesUnavailable = "This server can't export or import workflow files yet.";

    internal static Command Create(CliHost host, CommonOptions options)
    {
        var command = new Command("workflow", "List, inspect, export, import and check workflows.");
        var json = ScriptRunner.JsonOption();
        var list = new Command("list", "List the workspace's workflows.") { json };
        list.SetAction((parse, ct) => WorkflowHttp.RunAsync(host, parse, options, parse.GetValue(json), async api =>
        {
            var workflows = await api.GetAsync("workflows", ct);
            api.Write(workflows, () =>
            {
                foreach (var workflow in workflows!.AsArray())
                    host.Out.WriteLine($"{workflow!["name"]} ({workflow["id"]})  {workflow["state"]}  {workflow["description"]}");
            });
            return 0;
        }, ct));
        command.Subcommands.Add(list);

        var name = new Argument<string>("name") { Description = "The workflow's name or id." };
        var showJson = ScriptRunner.JsonOption();
        var show = new Command("show", "Show a workflow's settings and published path.") { name, showJson };
        show.SetAction((parse, ct) => WorkflowHttp.RunAsync(host, parse, options, parse.GetValue(showJson), async api =>
        {
            var workflow = await api.ResolveWorkflowAsync(parse.GetValue(name)!, false, ct);
            var detail = await api.GetAsync($"workflows/{WorkflowHttp.Escape(workflow["id"]!.ToString())}", ct);
            api.Write(detail, () => WriteWorkflow(host.Out, detail!));
            return 0;
        }, ct));
        command.Subcommands.Add(show);

        command.Subcommands.Add(CreateExport(host, options));
        command.Subcommands.Add(CreateImport(host, options));

        var file = new Argument<string>("file") { Description = "A workflow path JSON file." };
        var checkJson = ScriptRunner.JsonOption();
        var check = new Command("check", "Check a workflow path for errors and warnings.") { file, checkJson };
        check.SetAction((parse, ct) => WorkflowHttp.RunAsync(host, parse, options, parse.GetValue(checkJson), async api =>
        {
            var path = Path.GetFullPath(parse.GetValue(file)!, host.CurrentDirectory);
            var definition = JsonNode.Parse(await File.ReadAllTextAsync(path, ct));
            if (definition is not JsonObject) throw new CliException("The workflow path file must hold a JSON object.", ScriptExitCodes.InvalidInput);
            var result = await api.SendAsync(HttpMethod.Post, "workflows/check", definition, ct);
            var problems = result?["problems"]?.AsArray() ?? [];
            var errors = problems.Any(p => p?["severity"]?.ToString() != "warning");
            api.Write(result, () =>
            {
                WriteProblems(host.Out, problems);
                if (problems.Count == 0) host.Out.WriteLine("The workflow path has no problems.");
            });
            return errors ? ScriptExitCodes.InvalidInput : 0;
        }, ct));
        command.Subcommands.Add(check);
        return command;
    }

    private static Command CreateExport(CliHost host, CommonOptions options)
    {
        var name = new Argument<string>("name") { Description = "The workflow's name or id." };
        var version = new Option<string?>("--version") { Description = "A published version number or draft." };
        version.Validators.Add(result =>
        {
            var value = result.GetValue(version);
            if (value != "draft" && (!int.TryParse(value, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var number) || number <= 0))
                result.AddError("--version must be a positive version number or draft.");
        });
        var output = new Option<string?>("--output") { Description = "Write the YAML to this file." };
        var command = new Command("export", "Export a workflow as YAML.") { name, version, output };
        command.SetAction((parse, ct) => WorkflowHttp.RunAsync(host, parse, options, false, async api =>
        {
            var workflow = await api.ResolveWorkflowAsync(parse.GetValue(name)!, false, ct);
            var route = $"workflows/{WorkflowHttp.Escape(workflow["id"]!.ToString())}/export";
            if (parse.GetValue(version) is { } selected) route += $"?version={WorkflowHttp.Escape(selected)}";
            using var response = await api.RequestAsync(HttpMethod.Get, route, null, ct, FilesUnavailable);
            var yaml = await response.Content.ReadAsStringAsync(ct);
            if (parse.GetValue(output) is { } file)
            {
                var path = Path.GetFullPath(file, host.CurrentDirectory);
                await File.WriteAllTextAsync(path, yaml, ct);
                host.Out.WriteLine(path);
            }
            else
            {
                await host.Out.WriteAsync(yaml.AsMemory(), ct);
            }
            return 0;
        }, ct));
        return command;
    }

    private static Command CreateImport(CliHost host, CommonOptions options)
    {
        var file = new Argument<string>("file") { Description = "A workflow YAML or repository Markdown file." };
        var into = new Option<string?>("--into") { Description = "Replace the draft of this workflow by name or id." };
        var name = new Option<string?>("--name") { Description = "Set the name of a new draft workflow." };
        var json = ScriptRunner.JsonOption();
        var command = new Command("import", "Import a workflow file as a draft.") { file, into, name, json };
        command.SetAction((parse, ct) => WorkflowHttp.RunAsync(host, parse, options, parse.GetValue(json), async api =>
        {
            var path = Path.GetFullPath(parse.GetValue(file)!, host.CurrentDirectory);
            var body = new JsonObject { ["content"] = await ReadImportFileAsync(path, ct) };
            if (parse.GetValue(name) is { } title) body["name"] = title;
            if (parse.GetValue(into) is { } target)
            {
                var existing = await api.ResolveWorkflowAsync(target, false, ct);
                body["workflowId"] = existing["id"]!.ToString();
            }

            var result = await api.SendAsync(HttpMethod.Post, "workflows/import", body, ct, FilesUnavailable);
            api.Write(result, () =>
            {
                var workflow = result!["workflow"]!;
                var link = api.Link("workflows", workflow["id"]!.ToString());
                host.Out.WriteLine($"Saved draft {workflow["name"]}: {link}/edit");
                WriteProblems(host.Out, result["problems"]?.AsArray() ?? []);
            });
            return 0;
        }, ct));
        return command;
    }

    private static async Task<string> ReadImportFileAsync(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var bytes = new byte[MaxFileBytes + 1];
        var length = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, ct);
        if (length > MaxFileBytes)
            throw new CliException("Workflow files must be 64 KiB or smaller.", ScriptExitCodes.InvalidInput);

        var offset = bytes.AsSpan(0, length).StartsWith(Encoding.UTF8.Preamble) ? Encoding.UTF8.Preamble.Length : 0;
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes, offset, length - offset);
        }
        catch (DecoderFallbackException)
        {
            throw new CliException("Save the workflow file as UTF-8 and try again.", ScriptExitCodes.InvalidInput);
        }
    }

    private static void WriteProblems(TextWriter output, JsonArray problems)
    {
        foreach (var problem in problems.OrderBy(p => p?["severity"]?.ToString() == "warning"))
            output.WriteLine($"{problem?["severity"] ?? JsonValue.Create("error")}: {problem?["stepId"] ?? JsonValue.Create("path")}: {problem?["message"]}");
    }

    private static void WriteWorkflow(TextWriter output, JsonNode workflow)
    {
        output.WriteLine($"{workflow["name"]} ({workflow["id"]})  {workflow["state"]}, published version {workflow["publishedVersion"]}");
        output.WriteLine(workflow["description"]);
        if (workflow["settings"] is { } settings)
        {
            var start = settings["startsWhen"];
            output.WriteLine(start?["kind"]?.ToString() == "github-label"
                ? $"Starts when the {start["label"]} label is added. Repositories: {string.Join(", ", start["repositories"]?.AsArray().Select(r => r!.ToString()) ?? [])}."
                : "Starts when someone starts a job.");
            output.WriteLine(settings["runsOn"]?.ToString() == "owner-runners" ? "Runs on the starter's runners." : "Runs on workspace runners.");
            output.WriteLine($"Spending limit: {settings["spendingLimitUsd"]?.ToString() ?? "none"} USD. Loop limit: {settings["loopLimit"]}.");
            output.WriteLine(settings["whoCanStart"]?.ToString() == "admins" ? "Only admins can start jobs." : "Members can start jobs.");
            output.WriteLine(settings["asks"]?.ToString() == "role-owners" ? "Asks role owners for approval." : "Asks the starter for approval.");
            output.WriteLine(settings["keepExhibitsLocked"]?.GetValue<bool>() == true ? "Exhibits stay locked." : "Exhibits can be viewed in the dashboard.");
        }
        if (workflow["published"] is not { } path)
        {
            output.WriteLine("No published path.");
            return;
        }
        foreach (var step in path["steps"]?.AsArray() ?? [])
            output.WriteLine($"  {step!["id"]}: {step["name"]} ({step["type"]})");
        foreach (var arrow in path["arrows"]?.AsArray() ?? [])
            output.WriteLine($"  {arrow!["from"]} --{arrow["on"]?.ToString().Replace('-', ' ')}--> {arrow["to"]}" +
                (arrow["limit"] is { } limit ? $" (limit {limit}, then {arrow["onLimit"]})" : ""));
    }
}

/// <summary>The CLI's workflow HTTP calls; kept local until these service shapes are used by other client libraries.</summary>
internal sealed class WorkflowHttp(CommandContext context, HttpClient http, bool json)
{
    internal const string Disabled = "Workflows are not switched on for this workspace.";
    internal CommandContext Context => context;
    internal HttpClient Http => http;
    private string Workspace => context.Settings.Stored.WorkspaceId!;
    internal static string Escape(string value) => Uri.EscapeDataString(value);

    internal string Link(string kind, string id) => $"{CommandContext.ServerText(context.Settings.Server!)}/{kind}/{Escape(id)}";
    internal string Route(string path) => $"api/w/{Escape(Workspace)}/{path}";

    internal Task<JsonNode?> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, ct);

    internal async Task<HttpResponseMessage> RequestAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct,
        string notFoundMessage = Disabled)
    {
        if (!await TargetCapabilities.SupportsWorkflowsAsync(http, ct)) throw new CliException(Disabled);
        using var request = new HttpRequestMessage(method, Route(path));
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // A file route that exists answers an unknown workflow or version with its own message; a missing route has none.
                if (notFoundMessage != Disabled && await ErrorMessageAsync(response, ct) is { } missing)
                    throw new CliException(missing, ScriptExitCodes.InvalidInput);
                throw new CliException(notFoundMessage);
            }
            await EnsureSuccessAsync(response, ct);
            return response;
        }
        catch { response.Dispose(); throw; }
    }

    internal async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct,
        string notFoundMessage = Disabled)
    {
        using var response = await RequestAsync(method, path, body, ct, notFoundMessage);
        var text = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private static async Task<string?> ErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            if (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) is JsonObject error)
                return error["message"]?.ToString() ?? (error["error"] switch
                {
                    JsonObject nested => nested["message"]?.ToString(),
                    JsonValue value when value.TryGetValue<string>(out var detail) => detail,
                    _ => null,
                });
        }
        catch (JsonException) { }
        return null;
    }

    internal static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var message = await ErrorMessageAsync(response, ct);
        throw new CliException(message ?? $"The server refused the workflow command (HTTP {(int)response.StatusCode}).", response.StatusCode switch
        {
            HttpStatusCode.BadRequest => ScriptExitCodes.InvalidInput,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ScriptExitCodes.Forbidden,
            HttpStatusCode.Conflict => ScriptExitCodes.Conflict,
            _ => ScriptExitCodes.Failed,
        });
    }

    internal async Task<JsonNode> ResolveWorkflowAsync(string name, bool publishedOnly, CancellationToken ct)
    {
        var workflows = (await GetAsync("workflows", ct))!.AsArray();
        var match = workflows.FirstOrDefault(w => string.Equals(w?["id"]?.ToString(), name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(w?["name"]?.ToString(), name, StringComparison.OrdinalIgnoreCase));
        if (match is not null && (!publishedOnly || match["state"]?.ToString() == "published" && match["publishedVersion"] is not null)) return match;
        throw new CliException($"Workflow '{name}' is unknown or unpublished. Available workflows: {string.Join(", ", workflows.Select(w => w!["name"]!.ToString()))}.", ScriptExitCodes.InvalidInput);
    }

    internal void Write(JsonNode? value, Action text)
    {
        if (json) context.Host.Out.WriteLine(value?.ToJsonString() ?? "{}");
        else text();
    }

    internal static async Task<int> RunAsync(CliHost host, ParseResult parse, CommonOptions options, bool json,
        Func<WorkflowHttp, Task<int>> action, CancellationToken ct)
    {
        try
        {
            var context = CommandContext.Load(host, parse, options);
            if (context.Settings.Stored.WorkspaceId is null || context.Settings.Server is null || !context.Settings.CanAuthenticate(context.Store))
                throw new CliException(ServerConnection.NotSignedIn().Message, ScriptExitCodes.Forbidden);
            using var http = ServerConnection.CreateAuthenticatedClient(host, context.Settings, context.Store, "cli");
            if (!await TargetCapabilities.SupportsWorkflowsAsync(http, ct)) throw new CliException(Disabled);
            return await action(new WorkflowHttp(context, http, json));
        }
        catch (Exception e) when (e is CliException or HttpRequestException or IOException or UnauthorizedAccessException or JsonException or CryptographicException or McpToolException)
        {
            var exitCode = e switch
            {
                CliException cli => cli.ExitCode,
                McpToolException tool => ScriptExitCodes.For(tool.Error.Code),
                MissingKeyException => ScriptExitCodes.Forbidden,
                HttpRequestException => ScriptExitCodes.Unavailable,
                _ => ScriptExitCodes.Failed,
            };
            host.Error.WriteLine(e.Message);
            if (json) host.Out.WriteLine(new JsonObject
            {
                ["error"] = new JsonObject
                {
                    ["code"] = e is McpToolException tool ? tool.Error.Code : exitCode switch
                    {
                        ScriptExitCodes.InvalidInput => McpErrorCodes.InvalidInput,
                        ScriptExitCodes.Forbidden => McpErrorCodes.Forbidden,
                        ScriptExitCodes.Conflict => McpErrorCodes.Conflict,
                        ScriptExitCodes.Unavailable => McpErrorCodes.Unavailable,
                        _ => McpErrorCodes.Internal,
                    },
                    ["message"] = e.Message,
                },
            }.ToJsonString());
            return exitCode;
        }
    }
}
