using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Attachments.Client;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Hosting;
using MessageBus.Contracts;
using MessageBus.Contracts.Attachments;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Mcp;

namespace MessageBus.Cli.Scripts;

/// <summary>
/// <c>mbus send</c>, <c>status</c>, <c>roles</c>, <c>threads</c> and <c>attach</c> (the original issue): what the MCP tools do, for
/// scripts and people. Each runs the local MCP server's own tool code in-process (<see cref="MessageBus.Mcp.ScriptSession"/>),
/// so bodies and attachments are sealed and opened on this machine exactly as for an agent. <c>--json</c> prints the
/// tool's result as JSON; errors exit with <see cref="ScriptExitCodes"/>.
/// </summary>
internal static class ScriptCommands
{
    public static void AddTo(RootCommand root, CliHost host, CommonOptions options)
    {
        root.Subcommands.Add(Send(host, options));
        root.Subcommands.Add(Status(host, options));
        root.Subcommands.Add(Project(host, options));
        root.Subcommands.Add(Move(host, options));
        root.Subcommands.Add(Retarget(host, options));
        root.Subcommands.Add(Roles(host, options));
        root.Subcommands.Add(Threads(host, options));
        root.Subcommands.Add(Attach(host, options));
    }

    private static Command Retarget(CliHost host, CommonOptions options)
    {
        var thread = new Argument<string>("thread") { Description = "The queued task's thread." };
        var to = new Option<string>("--to") { Required = true, Description = "role:<name>, agent:<agt_id> or capabilities:<cap>,<cap>." };
        var reason = new Option<string?>("--reason") { Description = "Why the task is being retargeted (at most 500 characters)." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Preview without changing anything or uploading content." };
        var expected = new Option<string?>("--expected-task-message-id") { Description = "The previousMessageId from the preview; required unless --dry-run." };
        var operation = new Option<string?>("--operation-id") { Description = "A unique id to keep for retries; required unless --dry-run." };
        var json = ScriptRunner.JsonOption();
        var command = new Command("retarget", "Change the recipient of a queued task on the same thread and project.")
        { thread, to, reason, dryRun, expected, operation, json };
        command.SetAction((parse, ct) => ScriptRunner.RunAsync(host, parse, options, "retarget", parse.GetValue(json), async (script, ct) =>
        {
            var args = new JsonObject
            {
                ["threadId"] = parse.GetValue(thread), ["to"] = parse.GetValue(to),
                ["reason"] = parse.GetValue(reason), ["dryRun"] = parse.GetValue(dryRun),
                ["expectedTaskMessageId"] = parse.GetValue(expected), ["operationId"] = parse.GetValue(operation),
            };
            return await script.ReportAsync(await script.Session.CallAsync(McpToolNames.Retarget, args, ct), value =>
            {
                script.Line($"{(parse.GetValue(dryRun) ? "Would retarget" : "Retargeted")} {Script.Text(value, "threadId")} to {Script.Target(value, "to")} ({Script.Text(value, "status")}).");
                script.Line($"Task message: {Script.Text(value, "previousMessageId")}");
                if (Script.Text(value, "notice") is { } notice) script.Line(notice);
            });
        }, ct));
        return command;
    }

    private static Command Move(CliHost host, CommonOptions options)
    {
        var thread = new Argument<string>("thread") { Description = "The task to move." };
        var project = new Option<string>("--project") { Required = true, Description = "The destination project id, or none." };
        var noSubtasks = new Option<bool>("--no-subtasks") { Description = "Leave sub-tasks where they are." };
        var reason = new Option<string?>("--reason") { Description = "Why the task is moving." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Show what would move without changing anything." };
        var json = ScriptRunner.JsonOption();
        var command = new Command("move", "Move a task sent from this machine/session’s agent under the wrong project.") { thread, project, noSubtasks, reason, dryRun, json };
        command.SetAction((parse, ct) => ScriptRunner.RunAsync(host, parse, options, "move", parse.GetValue(json), async (script, ct) =>
        {
            var destination = parse.GetValue(project)!;
            var args = new JsonObject
            {
                ["threadId"] = parse.GetValue(thread),
                ["projectId"] = destination == "none" ? null : destination,
                ["includeSubtasks"] = !parse.GetValue(noSubtasks),
                ["reason"] = parse.GetValue(reason),
                ["dryRun"] = parse.GetValue(dryRun),
            };
            return await script.ReportAsync(await script.Session.CallAsync(McpToolNames.Move, args, ct), value =>
            {
                var target = Script.Text(value, "toTitle") ?? Script.Text(value, "toProjectId") ?? "no project";
                script.Line($"{(parse.GetValue(dryRun) ? "Would move" : "Moved")} to {target}:");
                foreach (var source in value.GetProperty("from").EnumerateArray())
                    script.Line($"  From {Script.Text(source, "title") ?? Script.Text(source, "projectId") ?? "no project"}");
                foreach (var id in value.GetProperty("threadIds").EnumerateArray()) script.Line($"  {id.GetString()}");
            });
        }, ct));
        return command;
    }

    // ---- send ----

    private static Command Send(CliHost host, CommonOptions options)
    {
        var to = new Option<string?>("--to") { Description = "Who it's for: role:<name>, agent:<agt_id>, capabilities:<cap>,<cap> or inbox:<inb_id>." };
        var type = new Option<string?>("--type") { Description = "The message type, for example task.implement, task.review or question.design." };
        var title = new Option<string?>("--title") { Description = "A short plain title for the dashboard. It isn't encrypted." };
        var summary = new Option<string?>("--summary") { Description = "What to do, written for an agent. Encrypted." };
        var details = new Option<string?>("--details") { Description = "Longer text. Encrypted." };
        var detailsFile = new Option<string?>("--details-file") { Description = "Read the details from a file (- for stdin)." };
        var input = new Option<string?>("--input")
        {
            Description = "A JSON file (- for stdin) with the send tool's arguments: {to, type, title, body: {summary, details?, data?}, refs, labels, …}. Options given as well win.",
        };
        var project = new Option<string?>("--project") { Description = "The project. Without it the message is sent without a project." };
        var thread = new Option<string?>("--thread") { Description = "Post into this thread instead of starting one." };
        var parent = new Option<string?>("--parent-thread") { Description = "Start the new thread under this one." };
        var expect = new Option<string?>("--expect") { Description = "reply for a question that needs an answer, outcome for a task (the default)." };
        expect.AcceptOnlyFromAmong("reply", "outcome");
        var replyTo = new Option<string?>("--reply-to") { Description = "Where answers go (default: they stay on the thread)." };
        var priority = new Option<string?>("--priority") { Description = "low, normal or high (default: the role's)." };
        priority.AcceptOnlyFromAmong("low", "normal", "high");
        var dedupe = new Option<string?>("--dedupe-key") { Description = "Send it only once: a second send with this key returns the first." };
        var labels = new Option<string[]>("--label") { Description = "A label, key=value. Repeat for more.", AllowMultipleArgumentsPerToken = false };
        var bundle = new Option<string?>("--attach-bundle") { Description = "Attach a branch of this repository as a git bundle (encrypted)." };
        var patch = new Option<string?>("--attach-patch") { Description = "Attach a branch of this repository as a patch after --base (encrypted)." };
        var files = new Option<string[]>("--attach-file") { Description = "Attach a file (encrypted). Repeat for more." };
        var branch = new Option<string?>("--branch") { Description = "With --attach-bundle or --attach-patch: the branch (default: the current one)." };
        var baseCommit = new Option<string?>("--base") { Description = "With --attach-bundle or --attach-patch: the commit the receiver already has." };
        var attachments = new Option<string[]>("--attachment") { Description = "An attachment uploaded with mbus attach bundle: the JSON file it wrote. Repeat for more." };
        var excludeAuthors = new Option<bool>("--exclude-authors") { Description = "For reviews: keep the agents that wrote the code from taking it." };
        var model = new Option<string?>("--model") { Description = "The model to run the task on, for example claude-fable-5-2. Only an agent that reports it takes it." };
        var wait = new Option<int?>("--wait") { Description = "Wait up to this many seconds for the task to finish, and print its result and answers." };
        var json = ScriptRunner.JsonOption();
        var command = new Command("send", "Send a message to a role or agent, encrypted on this machine.")
        {
            to, type, title, summary, details, detailsFile, input, project, thread, parent, expect, replyTo, priority, dedupe, labels,
            bundle, patch, files, branch, baseCommit, attachments, excludeAuthors, model, wait, json,
        };
        command.SetAction((parse, ct) => ScriptRunner.RunAsync(host, parse, options, "send", parse.GetValue(json), async (script, ct) =>
        {
            var args = parse.GetValue(input) is { } inputFile ? ReadObject(script, inputFile, "--input") : [];
            Set(args, "to", parse.GetValue(to));
            Set(args, "type", parse.GetValue(type));
            Set(args, "title", parse.GetValue(title));
            Set(args, "projectId", parse.GetValue(project));
            Set(args, "threadId", parse.GetValue(thread));
            Set(args, "parentThreadId", parse.GetValue(parent));
            Set(args, "expect", parse.GetValue(expect));
            Set(args, "replyTo", parse.GetValue(replyTo));
            Set(args, "priority", parse.GetValue(priority));
            Set(args, "dedupeKey", parse.GetValue(dedupe));
            if (parse.GetValue(excludeAuthors)) args["excludeAuthors"] = true;
            Set(args, "model", parse.GetValue(model));

            var body = args["body"] as JsonObject ?? [];
            Set(body, "summary", parse.GetValue(summary));
            Set(body, "details", parse.GetValue(detailsFile) is { } file ? ReadText(script, file) : parse.GetValue(details));
            if (body.Count > 0) args["body"] = body;

            if (parse.GetValue(labels) is { Length: > 0 } pairs)
            {
                var labelObject = args["labels"] as JsonObject ?? [];
                foreach (var pair in pairs)
                {
                    var eq = pair.IndexOf('=');
                    if (eq <= 0) return await script.FailAsync(Invalid("labels", $"'{pair}' isn't a label. Write key=value."));
                    labelObject[pair[..eq]] = pair[(eq + 1)..];
                }
                args["labels"] = labelObject;
            }

            var attach = args["attach"] as JsonArray ?? [];
            if (parse.GetValue(bundle) is { } bundleRepo) attach.Add(Spec("git-bundle", script.FullPath(bundleRepo), parse.GetValue(branch), parse.GetValue(baseCommit)));
            if (parse.GetValue(patch) is { } patchRepo) attach.Add(Spec("patch", script.FullPath(patchRepo), parse.GetValue(branch), parse.GetValue(baseCommit)));
            foreach (var path in parse.GetValue(files) ?? []) attach.Add(Spec("file", script.FullPath(path), null, null));
            if (attach.Count > 0) args["attach"] = attach;
            var uploaded = args["attachments"] as JsonArray ?? [];
            foreach (var refFile in parse.GetValue(attachments) ?? []) uploaded.Add(ReadObject(script, refFile, "--attachment"));
            if (uploaded.Count > 0) args["attachments"] = uploaded;

            if (args["to"] is null) return await script.FailAsync(Invalid("to", "Say who it's for with --to, for example --to role:developer."));
            if (args["type"] is null) return await script.FailAsync(Invalid("type", "Give the message type with --type, for example --type task.implement."));
            if (args["body"] is null) return await script.FailAsync(Invalid("body", "Say what to do with --summary (or a body in --input)."));

            var sent = await script.Session.CallAsync(McpToolNames.Send, args, ct);
            if (sent.Succeeded && Script.Text(sent.Value, "projectId") is null && Script.Text(sent.Value, "notice") is { } notice)
            {
                var value = Node(sent.Value)!.AsObject();
                value["notice"] = notice.Replace(
                    "This task has no project. To put it in one, call move with its threadId and a projectId. For later sends, pass projectId or call ensure_project first.",
                    "Sent without a project. Pass --project <id>, or move it with: mbus move <thread> --project <id>.");
                sent = sent with { Value = JsonSerializer.SerializeToElement(value) };
            }
            if (parse.GetValue(wait) is not { } seconds || !sent.Succeeded)
                return await script.ReportAsync(sent, value => script.Line(SentLine(value)));

            var threadId = Script.Text(sent.Value, "threadId")!;
            var waited = await script.Session.CallAsync(McpToolNames.Wait,
                new JsonObject { ["threadIds"] = new JsonArray(threadId), ["mode"] = "all", ["timeoutSeconds"] = Math.Max(0, seconds) }, ct);
            if (!waited.Succeeded) return await script.FailAsync(waited.Error!);
            if (script.Json)
                await script.WriteJsonAsync(new JsonObject { ["sent"] = Node(sent.Value), ["result"] = Node(waited.Value) });
            else
            {
                script.Line(SentLine(sent.Value));
                WriteWait(script, waited.Value);
            }
            return WaitExitCode(waited.Value);
        }, ct));
        return command;
    }

    private static string SentLine(JsonElement sent)
    {
        var line = $"Sent: thread {Script.Text(sent, "threadId")} ({Script.Text(sent, "status")}";
        if (sent.TryGetProperty("agentsOnline", out _))
            line += $"; {Script.Number(sent, "agentsOnline")} agent(s) online, {Script.Number(sent, "agentsFree")} free";
        if (Script.Number(sent, "queuePosition") is > 0 and var position) line += $"; place {position} in the queue";
        line += ")";
        if (Script.Text(sent, "notice") is { } notice) line += Environment.NewLine + notice;
        return line;
    }

    private static void WriteWait(Script script, JsonElement waited)
    {
        foreach (var thread in Script.Items(waited, "finished"))
        {
            var result = Script.Child(thread, "result");
            script.Line($"Finished: {Script.Text(thread, "status")}{(result is { } r && Script.Text(r, "summary") is { } s ? " - " + s : "")}");
        }
        foreach (var reply in Script.Items(waited, "replies")) WriteBody(script, reply);
        if (waited.TryGetProperty("timedOut", out var timedOut) && timedOut.ValueKind == JsonValueKind.True)
            script.Line("Still open when the time ran out. See it with: mbus status " + string.Join(' ', Script.Items(waited, "pending").Select(p => p.GetString())));
    }

    /// <summary>0 when every waited thread succeeded, <see cref="ScriptExitCodes.TimedOut"/> when one is still open, else <see cref="ScriptExitCodes.TaskDidNotSucceed"/>.</summary>
    internal static int WaitExitCode(JsonElement waited)
    {
        if (waited.TryGetProperty("timedOut", out var t) && t.ValueKind == JsonValueKind.True) return ScriptExitCodes.TimedOut;
        return Script.Items(waited, "finished").All(Succeeded) ? ScriptExitCodes.Ok : ScriptExitCodes.TaskDidNotSucceed;

        static bool Succeeded(JsonElement thread) =>
            Script.Text(thread, "status") is "settled" or "replied"
            && (Script.Child(thread, "result") is not { } result || Script.Text(result, "status") is null or "succeeded");
    }

    private static Command Project(CliHost host, CommonOptions options)
    {
        var command = new Command("project", "Find, coordinate or complete a project.");

        var findKey = new Argument<string>("key") { Description = "A project key or claimed work item." };
        var findJson = ScriptRunner.JsonOption();
        var find = new Command("find", "Look up a visible project without creating one.") { findKey, findJson };
        find.SetAction((parse, ct) => ScriptRunner.RunAsync(host, parse, options, "project find", parse.GetValue(findJson), async (script, ct) =>
            await script.ReportAsync(await FindProjectAsync(script, parse.GetValue(findKey)!, ct), value =>
            {
                if (Script.Child(value, "project") is { } project) WriteProject(script, project);
                else script.Line("No visible project found.");
            }), ct));
        command.Subcommands.Add(find);

        var key = new Argument<string>("key") { Description = "A project key, for example gh:owner/repo#42 or file:plan.md." };
        var title = new Option<string>("--title") { Required = true, Description = "Title for a new project." };
        var claim = new Option<string[]>("--claim") { Description = "Claim an explicit tracker work item. Repeat for each item." };
        var inbox = new Option<string?>("--coordinator-inbox")
        {
            Description = "Resume a locally saved coordinator inbox with the same complete --claim list.",
        };
        var scope = new Option<string?>("--execution-scope") { Description = "Choose workspace or owner-runners when creating a project. Omit to preserve the existing scope." };
        scope.AcceptOnlyFromAmong("workspace", "owner-runners");
        var ensureJson = ScriptRunner.JsonOption();
        var ensure = new Command("ensure", "Find or create a project and acquire any requested claims.") { key, title, claim, inbox, scope, ensureJson };
        ensure.SetAction((parse, ct) => ScriptRunner.RunAsync(host, parse, options, "project ensure", parse.GetValue(ensureJson), async (script, ct) =>
        {
            var claims = parse.GetValue(claim) ?? [];
            var coordinator = parse.GetValue(inbox);
            if (coordinator is not null && claims.Length == 0)
                return await script.FailAsync(Invalid("claimedWorkItems", "To resume a coordinator inbox, repeat every original --claim."));

            var args = new JsonObject
            {
                ["key"] = parse.GetValue(key), ["title"] = parse.GetValue(title),
                ["claimedWorkItems"] = new JsonArray([.. claims.Select(item => (JsonNode)item)]),
            };
            Set(args, "coordinatorInboxId", coordinator);
            Set(args, "executionScope", parse.GetValue(scope));
            return await script.ReportAsync(await script.Session.CallAsync(McpToolNames.EnsureProject, args, ct), value =>
            {
                if (Script.Child(value, "project") is { } project) WriteProject(script, project);
                if (claims.Length > 0 && Script.Text(value, "coordinatorInboxId") is { } id)
                    script.Line($"Claims acquired. To resume, repeat the claims with --coordinator-inbox {id}.");
            });
        }, ct));
        command.Subcommands.Add(ensure);

        var target = new Argument<string>("id") { Description = "The project ID or key." };
        var summary = new Option<string>("--summary") { Required = true, Description = "What was done, for the dashboard." };
        var status = new Option<string?>("--status") { Description = "Complete as completed (default) or failed." };
        status.AcceptOnlyFromAmong("completed", "failed");
        var completeJson = ScriptRunner.JsonOption();
        var complete = new Command("complete", "Mark a project completed or failed.") { target, summary, status, completeJson };
        complete.SetAction((parse, ct) => ScriptRunner.RunAsync(host, parse, options, "project complete", parse.GetValue(completeJson), async (script, ct) =>
        {
            var (id, error) = await ResolveProjectAsync(script, parse.GetValue(target)!, ct);
            if (error is not null) return await script.FailAsync(error);
            var args = new JsonObject { ["projectId"] = id, ["summary"] = parse.GetValue(summary) };
            Set(args, "status", parse.GetValue(status));
            return await script.ReportAsync(await script.Session.CallAsync(McpToolNames.CompleteProject, args, ct), value =>
            {
                if (Script.Child(value, "project") is { } project) WriteProject(script, project);
            });
        }, ct));
        command.Subcommands.Add(complete);

        return command;
    }

    private static Task<ScriptResult> FindProjectAsync(Script script, string key, CancellationToken ct) =>
        script.Session.CallAsync(McpToolNames.FindProject, new JsonObject { ["key"] = key }, ct);

    private static async Task<(string? Id, McpError? Error)> ResolveProjectAsync(Script script, string target, CancellationToken ct)
    {
        if (ContractIds.IsValid(target, ContractIds.Project)) return (target, null);
        if (!target.Contains(':'))
            return (null, Invalid("projectId", "Use a project ID (prj_…) or a project key such as gh:owner/repo#42."));

        var found = await FindProjectAsync(script, target, ct);
        if (found.Error is { } error) return (null, error);
        if (Script.Child(found.Value, "project") is { } project && Script.Text(project, "projectId") is { } id)
            return (id, null);
        return (null, new McpError { Code = McpErrorCodes.NotFound, Message = $"No visible project matches '{target}'." });
    }

    private static void WriteProject(Script script, JsonElement project) =>
        script.Line($"{Script.Text(project, "title")} ({Script.Text(project, "projectId")})  {Script.Text(project, "status")}  {Script.Text(project, "executionScope")}");

    // ---- status and threads ----

    private static Command Status(CliHost host, CommonOptions options)
    {
        var threads = new Argument<string[]>("thread") { Description = "Threads to look up (thr_…). None: your recent threads.", Arity = ArgumentArity.ZeroOrMore };
        var project = new Option<string?>("--project") { Description = "Look up a project by ID or key for its threads and totals." };
        var limit = new Option<int?>("--limit") { Description = "Most threads to list (default 50)." };
        var json = ScriptRunner.JsonOption();
        var command = new Command("status", "Show where threads or a project stand.") { threads, project, limit, json };
        command.SetAction((parse, ct) => ScriptRunner.RunAsync(host, parse, options, "status", parse.GetValue(json), async (script, ct) =>
        {
            var args = new JsonObject();
            if (parse.GetValue(threads) is { Length: > 0 } ids) args["threadIds"] = new JsonArray([.. ids.Select(i => (JsonNode)i)]);
            if (parse.GetValue(project) is { } target)
            {
                var (id, error) = await ResolveProjectAsync(script, target, ct);
                if (error is not null) return await script.FailAsync(error);
                args["projectId"] = id;
            }
            if (parse.GetValue(limit) is { } most) args["limit"] = most;
            return await script.ReportAsync(await script.Session.CallAsync(McpToolNames.Status, args, ct), value => WriteStatus(script, value));
        }, ct));
        return command;
    }

    private static Command Threads(CliHost host, CommonOptions options)
    {
        var project = new Option<string?>("--project") { Description = "Only threads for this project ID or key." };
        var limit = new Option<int?>("--limit") { Description = "Most threads to list (default 50)." };
        var json = ScriptRunner.JsonOption();
        var command = new Command("threads", "List recent threads, one line each.") { project, limit, json };
        command.SetAction((parse, ct) => ScriptRunner.RunAsync(host, parse, options, "threads", parse.GetValue(json), async (script, ct) =>
        {
            var args = new JsonObject { ["compact"] = true };
            if (parse.GetValue(project) is { } target)
            {
                var (id, error) = await ResolveProjectAsync(script, target, ct);
                if (error is not null) return await script.FailAsync(error);
                args["projectId"] = id;
            }
            if (parse.GetValue(limit) is { } most) args["limit"] = most;
            return await script.ReportAsync(await script.Session.CallAsync(McpToolNames.Status, args, ct), value => WriteStatus(script, value));
        }, ct));

        var thread = new Argument<string>("thread") { Description = "The thread (thr_…)." };
        var showJson = ScriptRunner.JsonOption();
        var show = new Command("show", "Show a thread's messages, decrypted on this machine.") { thread, showJson };
        show.SetAction((parse, ct) => ScriptRunner.RunAsync(host, parse, options, "threads show", parse.GetValue(showJson), async (script, ct) =>
            await script.ReportAsync(await script.Session.ThreadAsync(parse.GetValue(thread)!, ct), value =>
            {
                foreach (var message in Script.Items(value, "messages")) WriteBody(script, message);
            }), ct));
        command.Subcommands.Add(show);
        return command;
    }

    private static void WriteStatus(Script script, JsonElement status)
    {
        if (Script.Child(status, "project") is { } project)
        {
            var totals = Script.Child(project, "totals");
            script.Line($"{Script.Text(project, "title")} ({Script.Text(project, "projectId")}): {Script.Text(project, "status")}" +
                        (totals is { } t ? $", {Script.Number(t, "tasks")} task(s)" : ""));
        }
        foreach (var thread in Script.Items(status, "threads"))
        {
            var result = Script.Child(thread, "result");
            script.Line($"{Script.Text(thread, "threadId")}  {Script.Text(thread, "status"),-13} {Script.Text(thread, "title") ?? Script.Text(thread, "type")}" +
                        (result is { } r && Script.Text(r, "summary") is { } s ? " - " + s : ""));
        }
        foreach (var task in Script.Items(status, "tasks"))
        {
            script.Line($"{Script.Text(task, "threadId")}  {Script.Text(task, "status"),-13} {Script.Text(task, "to"),-20} {Script.Text(task, "title") ?? Script.Text(task, "type")}" +
                        (Script.Text(task, "summary") is { } s ? " - " + s : ""));
        }
        if (status.TryGetProperty("more", out var more) && more.ValueKind == JsonValueKind.True) script.Line("(more: use --limit)");
    }

    /// <summary>One message: who it's from, its type and decrypted body, and its attachments.</summary>
    private static void WriteBody(Script script, JsonElement received)
    {
        var envelope = Script.Child(received, "envelope") ?? default;
        var sender = Script.Child(envelope, "sender");
        script.Line($"{Script.Text(envelope, "time")}  {Script.Text(envelope, "type")}  from {(sender is { } s ? Script.Text(s, "displayName") ?? Script.Text(s, "agentId") ?? Script.Text(s, "kind") : "?")}");
        if (Script.Child(received, "body") is { } body)
        {
            script.Line("  " + Script.Text(body, "summary"));
            if (Script.Text(body, "details") is { Length: > 0 } details)
                foreach (var line in details.Split('\n')) script.Line("    " + line.TrimEnd('\r'));
        }
        else
        {
            script.Line("  (encrypted for a key this machine doesn't hold)");
        }
        foreach (var attachment in Script.Items(envelope, "attachments"))
            script.Line($"  attachment {Script.Text(attachment, "id")}: {Script.Text(attachment, "format")} {Script.Text(attachment, "name")}" +
                        (Script.Text(attachment, "branch") is { } b ? $" (branch {b})" : ""));
    }

    // ---- roles ----

    private static Command Roles(CliHost host, CommonOptions options)
    {
        var role = new Option<string?>("--role") { Description = "Only this role." };
        var capabilities = new Option<string[]>("--capability") { Description = "Only agents whose machines have this capability. Repeat for more." };
        var online = new Option<bool>("--online") { Description = "Only agents online now." };
        var json = ScriptRunner.JsonOption();
        var command = new Command("roles", "List the roles, who holds them and how busy they are.") { role, capabilities, online, json };
        command.SetAction((parse, ct) => ScriptRunner.RunAsync(host, parse, options, "roles", parse.GetValue(json), async (script, ct) =>
        {
            var args = new JsonObject();
            Set(args, "role", parse.GetValue(role));
            if (parse.GetValue(capabilities) is { Length: > 0 } caps) args["capabilities"] = new JsonArray([.. caps.Select(c => (JsonNode)c)]);
            if (parse.GetValue(online)) args["onlineOnly"] = true;
            return await script.ReportAsync(await script.Session.CallAsync(McpToolNames.Directory, args, ct), value =>
            {
                foreach (var r in Script.Items(value, "roles"))
                {
                    script.Line($"{Script.Text(r, "name"),-20} {Script.Number(r, "agentsOnline")} online, {Script.Number(r, "agentsFree")} free, {Script.Number(r, "queueDepth")} waiting" +
                                (Script.Text(r, "description") is { Length: > 0 } d ? " - " + d : ""));
                    foreach (var agent in Script.Items(r, "agents"))
                        script.Line($"    {Script.Text(agent, "agentId")}  {Script.Text(agent, "kind")} on {Script.Text(agent, "machineName") ?? Script.Text(agent, "machineId")}");
                }
            });
        }, ct));

        var name = new Argument<string>("role") { Description = "The role's name." };
        var describeJson = ScriptRunner.JsonOption();
        var describe = new Command("describe", "Show a role's settings and its instructions for agents.") { name, describeJson };
        describe.SetAction((parse, ct) => ScriptRunner.RunAsync(host, parse, options, "roles describe", parse.GetValue(describeJson), async (script, ct) =>
            await script.ReportAsync(await script.Session.CallAsync(McpToolNames.DescribeRole, new JsonObject { ["role"] = parse.GetValue(name) }, ct), value =>
            {
                var r = Script.Child(value, "role") ?? default;
                script.Line($"{Script.Text(r, "displayName")} ({Script.Text(r, "name")}): {Script.Number(value, "agentsOnline")} agent(s) online");
                if (Script.Text(r, "description") is { Length: > 0 } d) script.Line(d);
                if (Script.Child(value, "instructions") is { } instructions && Script.Text(instructions, "content") is { } content)
                {
                    script.Line("");
                    script.Line(content);
                }
            }), ct));
        command.Subcommands.Add(describe);
        return command;
    }

    // ---- attach ----

    private static Command Attach(CliHost host, CommonOptions options)
    {
        var command = new Command("attach", "Send code as an encrypted attachment, or bring one onto this machine.");

        var to = new Option<string>("--to") { Description = "Who it's for, as for mbus send. It is encrypted for them.", Required = true };
        var repo = new Option<string?>("--repo") { Description = "The repository (default: the current directory)." };
        var branch = new Option<string?>("--branch") { Description = "The branch (default: the current one)." };
        var baseCommit = new Option<string?>("--base") { Description = "Only the commits after this one, which the receiver already has." };
        var thread = new Option<string?>("--thread") { Description = "The thread it is for, if it already exists." };
        var output = new Option<string?>("--out") { Description = "Save the attachment's reference to this file, for mbus send --attachment." };
        var json = ScriptRunner.JsonOption();
        var bundle = new Command("bundle", "Package a branch as a git bundle, encrypt it and upload it.") { to, repo, branch, baseCommit, thread, output, json };
        bundle.SetAction((parse, ct) => ScriptRunner.RunAsync(host, parse, options, "attach bundle", parse.GetValue(json), async (script, ct) =>
        {
            if (!MessageTarget.TryParse(parse.GetValue(to)!, out var target))
                return await script.FailAsync(Invalid("to", $"'{parse.GetValue(to)}' isn't a target. Write role:<name>, agent:<agt_id>, capabilities:<cap>,<cap> or inbox:<inb_id>."));
            var spec = new AttachSpec
            {
                Format = AttachmentFormat.GitBundle, Path = script.FullPath(parse.GetValue(repo) ?? "."), Branch = parse.GetValue(branch), Base = parse.GetValue(baseCommit),
            };
            var result = await script.Session.AttachAsync(target!, spec, parse.GetValue(thread), ct);
            if (result.Succeeded && parse.GetValue(output) is { } file)
                await File.WriteAllTextAsync(script.FullPath(file), result.Value.GetRawText(), ct);
            return await script.ReportAsync(result, value => script.Line(
                $"Uploaded {Script.Text(value, "name")} ({Script.Text(value, "id")}, {Script.Number(value, "sizeBytes")} bytes), encrypted for {target}." +
                (parse.GetValue(output) is { } saved ? $" Send it with: mbus send --attachment {saved} …" : " Save its reference with --out to send it.")));
        }, ct));
        command.Subcommands.Add(bundle);

        var applyThread = new Argument<string>("thread") { Description = "The thread the attachment is on (thr_…)." };
        var attachment = new Argument<string>("attachment") { Description = "The attachment (att_…), from mbus threads show." };
        var applyRepo = new Option<string?>("--repo") { Description = "The repository to bring it into (default: the current directory)." };
        var into = new Option<string?>("--into") { Description = "For a git bundle: the ref to fetch into (default refs/remotes/mbus/<branch>)." };
        var path = new Option<string?>("--path") { Description = "For a file: where to save it. For a patch: save it here instead of applying it." };
        var delivery = new Option<string?>("--delivery") { Description = "The delivery, for an attachment that came with a task you hold." };
        var applyJson = ScriptRunner.JsonOption();
        var apply = new Command("apply", "Bring an attachment here, decrypted: a git bundle is fetched into the repository, a patch is applied to it, a file is saved.")
        {
            applyThread, attachment, applyRepo, into, path, delivery, applyJson,
        };
        apply.SetAction((parse, ct) => ScriptRunner.RunAsync(host, parse, options, "attach apply", parse.GetValue(applyJson), async (script, ct) =>
        {
            var repository = script.FullPath(parse.GetValue(applyRepo) ?? ".");
            var args = new JsonObject
            {
                ["threadId"] = parse.GetValue(applyThread), ["attachmentId"] = parse.GetValue(attachment), ["repository"] = repository,
            };
            Set(args, "into", parse.GetValue(into));
            Set(args, "deliveryId", parse.GetValue(delivery));
            if (parse.GetValue(path) is { } p) args["path"] = script.FullPath(p);
            var fetched = await script.Session.CallAsync(McpToolNames.FetchAttachment, args, ct);
            if (!fetched.Succeeded) return await script.FailAsync(fetched.Error!);

            var result = Node(fetched.Value).AsObject();
            if (Script.Text(fetched.Value, "format") == "patch" && parse.GetValue(path) is null)
            {
                var patchFile = Script.Text(fetched.Value, "path")!;
                try
                {
                    await GitBundles.ApplyPatchAsync(patchFile, repository, ct);
                }
                catch (GitException e)
                {
                    return await script.FailAsync(new McpError
                    {
                        Code = McpErrorCodes.Conflict, Message = $"The patch doesn't apply to {repository}: {e.Message} It is saved at {patchFile}.",
                    });
                }
                result["applied"] = true;
            }
            if (script.Json)
            {
                await script.WriteJsonAsync(result);
                return ScriptExitCodes.Ok;
            }
            var value = fetched.Value;
            script.Line(Script.Text(value, "format") switch
            {
                "git-bundle" => $"Fetched {Script.Text(value, "commit")} into {Script.Text(value, "ref")} in {repository}.",
                "patch" when result["applied"] is not null => $"Applied the patch to {repository}.",
                _ => $"Saved to {Script.Text(value, "path")}.",
            });
            return ScriptExitCodes.Ok;
        }, ct));
        command.Subcommands.Add(apply);
        return command;
    }

    // ---- helpers ----

    private static JsonObject Spec(string format, string path, string? branch, string? baseCommit)
    {
        var spec = new JsonObject { ["format"] = format, ["path"] = path };
        Set(spec, "branch", branch);
        Set(spec, "base", baseCommit);
        return spec;
    }

    private static void Set(JsonObject obj, string name, string? value)
    {
        if (value is not null) obj[name] = value;
    }

    private static JsonNode Node(JsonElement value) => JsonNode.Parse(value.GetRawText())!;

    private static McpError Invalid(string field, string message) => new() { Code = McpErrorCodes.InvalidInput, Field = field, Message = message };

    private static string ReadText(Script script, string file) =>
        file == "-" ? script.Host.In.ReadToEnd() : File.ReadAllText(script.FullPath(file));

    private static JsonObject ReadObject(Script script, string file, string option)
    {
        try
        {
            return JsonNode.Parse(ReadText(script, file)) as JsonObject
                   ?? throw new CliException($"{option}: {file} doesn't hold a JSON object.", ScriptExitCodes.InvalidInput);
        }
        catch (JsonException e)
        {
            throw new CliException($"{option}: {file} isn't valid JSON ({e.Message}).", ScriptExitCodes.InvalidInput);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CliException($"{option}: can't read {file} ({e.Message}).", ScriptExitCodes.InvalidInput);
        }
    }
}
