using System.CommandLine;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Scripts;
using MessageBus.Contracts.Mcp;

namespace MessageBus.Cli.Approvals;

/// <summary>
/// <c>mbus approvals</c>, <c>mbus approve</c> and <c>mbus decline</c> (the original issue): the approvals waiting for the signed-in
/// person's answer (a run waiting on this machine, or anything else an agent asked them), answered from the terminal
/// as that person. The list is numbered by name; <c>approve 2</c> means the second one in the list last shown, so a new
/// request arriving in between can't change which one is answered. A name or the approval's id works too (ids for
/// scripts, with <c>--json</c>). Exit codes are the script commands' (<see cref="ScriptExitCodes"/>).
/// </summary>
internal static class ApprovalsCommand
{
    /// <summary>The numbered list last shown, in the settings folder.</summary>
    internal const string ShownFile = "approvals-shown.json";

    public static void AddTo(RootCommand root, CliHost host, CommonOptions options)
    {
        var json = ScriptRunner.JsonOption();
        var list = new Command("approvals", "List the approvals waiting for your answer, numbered, newest last.") { json };
        list.SetAction((parse, ct) => ListAsync(host, parse, options, parse.GetValue(json), ct));
        var listJson = ScriptRunner.JsonOption();
        var listAlias = new Command("list", "List the approvals waiting for your answer (the same as mbus approvals).") { listJson };
        listAlias.SetAction((parse, ct) => ListAsync(host, parse, options, parse.GetValue(listJson), ct));
        list.Subcommands.Add(listAlias);
        root.Subcommands.Add(list);
        root.Subcommands.Add(Decide(host, options, approve: true));
        root.Subcommands.Add(Decide(host, options, approve: false));
    }

    private static async Task<int> ListAsync(CliHost host, ParseResult parse, CommonOptions options, bool json, CancellationToken ct)
    {
        var output = new Output(host, "approvals", json);
        var context = CommandContext.Load(host, parse, options);
        if (ApprovalsClient.For(context) is not { } client) return await output.FailAsync(McpErrorCodes.Forbidden, ApprovalsClient.NotSignedIn);
        var pending = await client.PendingAsync(ct);
        if (!pending.Succeeded) return await output.FailAsync(pending);
        var items = pending.Value!;
        Remember(context, items);

        if (json)
        {
            var array = new JsonArray();
            for (var i = 0; i < items.Count; i++)
            {
                var copy = (JsonObject)items[i].Raw.DeepClone();
                copy["number"] = i + 1;
                array.Add(copy);
            }
            await output.WriteJsonAsync(new JsonObject { ["approvals"] = array });
            return ScriptExitCodes.Ok;
        }
        if (items.Count == 0)
        {
            output.Line("Nothing is waiting for your answer.");
            return ScriptExitCodes.Ok;
        }
        output.Line(items.Count == 1 ? "Waiting for your answer:" : $"Waiting for your answer ({items.Count}):");
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            output.Line($"  {i + 1}. {item.Title}{(item.HighRisk ? "  (high risk: answer it in the dashboard)" : "")}");
            var asked = item.RequestedBy is { } who ? $"Asked by {who}" : "Asked";
            asked += item.CreatedAt == default ? "." : $" {Ago(host.Time.GetUtcNow() - item.CreatedAt)}.";
            output.Line($"     {asked}{(item.Rule is { } rule ? " " + rule + "." : "")}");
        }
        output.Line("Answer one with mbus approve 1, or mbus decline 1 --comment \"why\".");
        return ScriptExitCodes.Ok;
    }

    private static Command Decide(CliHost host, CommonOptions options, bool approve)
    {
        var name = approve ? "approve" : "decline";
        var which = new Argument<string>("approval")
        {
            Description = "Which one: its number in the list mbus approvals showed, words from its title, or its id.",
        };
        var comment = new Option<string?>("--comment") { Description = approve ? "A note for the agent that asked." : "Why, for the agent that asked." };
        var json = ScriptRunner.JsonOption();
        var command = new Command(name, approve ? "Approve a request waiting for your answer." : "Decline a request waiting for your answer.") { which, comment, json };
        if (!approve) command.Aliases.Add("reject");
        command.SetAction(async (parse, ct) =>
        {
            var output = new Output(host, name, parse.GetValue(json));
            var context = CommandContext.Load(host, parse, options);
            if (ApprovalsClient.For(context) is not { } client) return await output.FailAsync(McpErrorCodes.Forbidden, ApprovalsClient.NotSignedIn);

            var (id, problem) = await ResolveAsync(context, client, parse.GetValue(which)!.Trim(), name, ct);
            if (problem is not null) return await output.FailAsync(problem);
            var decided = await client.DecideAsync(id!, approve, parse.GetValue(comment), ct);
            if (decided.Code == ApprovalCodes.SignInAgain && decided.DashboardUrl is { } url && !output.Json)
            {
                // the original issue: a high-risk request needs a fresh sign-in, which only the dashboard can ask for.
                output.Line("This is a high-risk request: it needs you to sign in again, so answer it in the dashboard.");
                output.Line("  " + url);
                if (Uri.TryCreate(url, UriKind.Absolute, out var link)) await host.Browser.OpenAsync(link, ct);
                await host.Error.WriteLineAsync($"mbus {name}: {decided.Message}");
                return ScriptExitCodes.Forbidden;
            }
            if (!decided.Succeeded) return await output.FailAsync(decided);
            if (output.Json) await output.WriteJsonAsync(decided.Value!.Raw);
            else output.Line($"{(approve ? "Approved" : "Declined")}: {decided.Value!.Title}");
            return ScriptExitCodes.Ok;
        });
        return command;
    }

    /// <summary>The approval a number, an id or words from a title mean; or why it can't be told.</summary>
    private static async Task<(string? Id, ApprovalsResult<string>? Problem)> ResolveAsync(CommandContext context, ApprovalsClient client, string which,
        string command, CancellationToken ct)
    {
        if (which.Length == 0) return (null, new(null, McpErrorCodes.InvalidInput, "Say which one: its number from mbus approvals, or words from its title."));
        if (int.TryParse(which, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            var shown = Shown(context);
            if (shown is null)
                return (null, new(null, McpErrorCodes.InvalidInput, "Run mbus approvals first: the numbers come from the list it shows."));
            if (number < 1 || number > shown.Count)
                return (null, new(null, McpErrorCodes.NotFound, $"The list mbus approvals showed has {(shown.Count == 0 ? "nothing in it" : $"{shown.Count} in it")}. Run it again to see what is waiting."));
            return (shown[number - 1], null);
        }
        if (which.StartsWith("apr_", StringComparison.Ordinal)) return (which, null);

        var pending = await client.PendingAsync(ct);
        if (!pending.Succeeded) return (null, new(null, pending.Code, pending.Message, pending.DashboardUrl));
        var matches = pending.Value!.Where(a => a.Title.Contains(which, StringComparison.OrdinalIgnoreCase)).ToList();
        switch (matches.Count)
        {
            case 1:
                return (matches[0].Id, null);
            case 0:
                return (null, new(null, McpErrorCodes.NotFound, $"Nothing waiting for your answer matches \"{which}\". See what is waiting with mbus approvals."));
            default:
                Remember(context, matches);
                var choices = string.Join(Environment.NewLine, matches.Select((a, i) => $"  {i + 1}. {a.Title}"));
                return (null, new(null, McpErrorCodes.InvalidInput,
                    $"More than one matches \"{which}\":{Environment.NewLine}{choices}{Environment.NewLine}Pick one by number, for example mbus {command} 1."));
        }
    }

    // ---- the list last shown ----

    private static void Remember(CommandContext context, IReadOnlyList<PendingApproval> items)
    {
        try
        {
            Directory.CreateDirectory(context.Store.Directory);
            var file = new JsonObject
            {
                ["server"] = context.Settings.Server?.AbsoluteUri,
                ["workspaceId"] = context.Settings.Stored.WorkspaceId,
                ["ids"] = new JsonArray([.. items.Select(i => (JsonNode)i.Id)]),
            };
            File.WriteAllText(Path.Combine(context.Store.Directory, ShownFile), file.ToJsonString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Numbers then need mbus approvals again; names and ids still work.
        }
    }

    private static List<string>? Shown(CommandContext context)
    {
        try
        {
            var path = Path.Combine(context.Store.Directory, ShownFile);
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject file) return null;
            if (PendingApproval.Text(file, "server") != context.Settings.Server?.AbsoluteUri
                || PendingApproval.Text(file, "workspaceId") != context.Settings.Stored.WorkspaceId)
                return null;
            return [.. (file["ids"] as JsonArray ?? []).Select(n => n?.GetValue<string>()).OfType<string>()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static string Ago(TimeSpan age) => age.TotalMinutes switch
    {
        < 1 => "just now",
        < 60 => $"{(int)age.TotalMinutes} min ago",
        < 48 * 60 => $"{(int)age.TotalHours} h ago",
        _ => $"{(int)age.TotalDays} days ago",
    };

    /// <summary>Text or <c>--json</c> output, and errors the way the script commands write them.</summary>
    private sealed class Output(CliHost host, string command, bool json)
    {
        public bool Json => json;

        public void Line(string text) => host.Out.WriteLine(text);

        public Task WriteJsonAsync(JsonNode value) =>
            host.Out.WriteLineAsync(Indented(value));

        private static string Indented(JsonNode value)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                value.WriteTo(writer);
            }
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }

        public Task<int> FailAsync<T>(ApprovalsResult<T> result) => FailAsync(result.Code ?? McpErrorCodes.Internal, result.Message ?? "Something went wrong.", result.DashboardUrl);

        public async Task<int> FailAsync(string code, string message, string? dashboardUrl = null)
        {
            await host.Error.WriteLineAsync($"mbus {command}: {message}");
            if (json)
            {
                var error = new JsonObject { ["code"] = code, ["message"] = message };
                if (dashboardUrl is not null) error["dashboardUrl"] = dashboardUrl;
                await WriteJsonAsync(new JsonObject { ["error"] = error });
            }
            return code == ApprovalCodes.SignInAgain ? ScriptExitCodes.Forbidden : ScriptExitCodes.For(code);
        }

    }
}
