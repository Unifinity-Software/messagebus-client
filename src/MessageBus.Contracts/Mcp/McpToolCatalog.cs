using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using MessageBus.Contracts.Attachments;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Roles;
using ModelContextProtocol.Protocol;

namespace MessageBus.Contracts.Mcp;

/// <summary>One tool: its name, what it takes and returns, and the description agents read.</summary>
/// <param name="Name">One of <see cref="McpToolNames"/>.</param>
/// <param name="Input">The <c>…Input</c> contract type its arguments deserialize to.</param>
/// <param name="Output">The contract type it returns.</param>
/// <param name="Title">A short title.</param>
/// <param name="Description">When to use it, what comes back and what to do on error, written for an agent.</param>
/// <param name="ReadOnly">True when it changes nothing.</param>
public sealed record McpToolSpec(string Name, Type Input, Type Output, string Title, string Description, bool ReadOnly = false)
{
    /// <summary>Service features required to list this tool; listing never grants invocation authority.</summary>
    public McpCatalogFeatures Features { get; init; }
}

/// <summary>
/// The tool list both MCP servers expose: names from <see cref="McpToolNames"/>, input schemas generated
/// from the <c>MessageBus.Contracts</c> input types with <see cref="MessageBusJson"/> (so a schema can't
/// drift from the contract), and descriptions for agents. The local server hides the fields it fills
/// itself (<c>sealedBody</c>, <c>sealedReply</c>, <c>attachments</c>, <c>replyOnly</c>).
/// </summary>
public static class McpToolCatalog
{
    /// <summary>
    /// What every tool's errors look like. Said once, in the servers' instructions (the local server and the
    /// service's <c>/mcp</c>), rather than in each tool's description: agents re-read the tool list on every turn (the original issue).
    /// </summary>
    public const string Errors =
        "Errors come back with isError and {code, message, retryable, retryAfterSeconds?, field?}: invalid-input (fix `field` and call again), " +
        "not-found, forbidden (don't retry), conflict (re-read with status), too-large (attach large content instead), " +
        "limit-reached (wait retryAfterSeconds), unavailable (retry with backoff).";

    private const string VerificationSendDescription =
        "Required checks for a role task: `verification` {environment, requiredChecks: [test|typecheck|build|browser]}. The service must support verified sends. ";

    /// <summary>Every tool, in <see cref="McpToolNames.All"/> order. Descriptions are kept short: agents re-read them on every turn (the original issue).</summary>
    public static IReadOnlyList<McpToolSpec> Specs { get; } =
    [
        new(McpToolNames.Send, typeof(SendInput), typeof(SendResult), "Send a message",
            "A retry must name sourceThreadId; the server preserves the source project's execution owner and ignores retryOf label changes. Owner-only work and descendants cannot widen scope through targets, projects or active machine sends. " +
            "Send one message (a task, question or event). `to`: role:<name> (any holder, e.g. role:developer), agent:<agt_id>, " +
            "capabilities:<cap>,<cap> (any agent whose machine has them all, e.g. capabilities:os:linux,tool:dotnet) or inbox:<inb_id>. " +
            "Give `type` (e.g. task.implement, task.review, question.design), a short plain `title` for the dashboard and `body` {summary, details?, data?}: " +
            "it is encrypted here for the recipient, so put the real content in it, never secrets (a body that looks like one is refused). " +
            "`projectId` defaults to the project this session last ensured (or its assigned runner task’s project); without either, new tasks sent outside an inbox have no project and a notice says so; `expect: reply` for a question; `parentThreadId` for a sub-task of a task you hold. With a git bundle, a notice warns if its tip lacks the parent’s returned commits; fetch the parent’s result and send that branch or mbus/<branch>. " +
            "Local code packaging: `attach` [{format: git-bundle, path: <repo>, branch?, base?}] (or patch, or file with a path). The first git bundle can start after the last sent commit and a runner missing the rest gets it from mbus. Later bundles default to whole history. Set base: \"\" to send the whole history. Branch accepts mbus/<branch> or refs/remotes/<remote>/<branch>; a notice warns when the local branch is behind its fetched mbus copy. " +
            VerificationSendDescription +
            "Reviews: `excludeAuthors: true` skips agents that succeeded or genuinely failed on the project's (or parent's) non-review tasks (refusals and setup failures do not count), `excludeAgents` [agt_…] skips others; " +
            "with every holder excluded it fails not-found, and `notice` says when fewer copies go out than the role asks for. " +
            "Direct task sends with labels.role return a notice if the agent lacks that role’s required capabilities (except follow-ups to an agent that held the referenced task); prefer role targets. " +
            "`model` (e.g. claude-fable-5-2, from directory) runs it on that model: only an agent reporting it takes it (none: not-found), and a role may limit the models (not for `agent:` or `capabilities:` targets, which only need an agent reporting it). " +
            "Returns {messageId, threadId, status, agentsOnline, agentsFree, queuePosition?, excludedAgents, notice?}: agentsOnline = agents that could take it " +
            "and are connected (0: it waits for one to connect); agentsFree = of those, with a free slot now (0: all busy, it waits its turn); " +
            "queuePosition = its place among the role's waiting tasks (1 = next). Keep threadId for wait, status, retry and cancel."),
        new(McpToolNames.SendMany, typeof(SendManyInput), typeof(SendManyResult), "Send several messages",
            "Send up to 100 messages at once (parallel tasks). Each item of `messages` takes send's arguments (including attach base: \"\" for whole history); `projectId` applies to items without one. " +
            "`join` {mode: all|any, timeoutSeconds?} gives one combined result when they finish (wait on joinThreadId, or on each thread). " +
            "Returns send's result per message, in order (agentsOnline, agentsFree and queuePosition as for send)."),
        new(McpToolNames.Wait, typeof(WaitInput), typeof(WaitResult), "Wait for results",
            "Block until any (mode any) or all (mode all) of `threadIds` finish, or timeoutSeconds (default 100) passes; the local server returns by 110 s " +
            "under Claude Code and 55 s under other clients whatever you ask. A projectId alone only covers threads open when you call. " +
            "Returns finished threads with their result (status, summary, refs, usage; a broadcast lists each agent's result in `copies`), pending ids, timedOut, " +
            "and `replies`: the answers posted on finished threads, decrypted (can be fewer than the copies). Mode any also returns new finished broadcast copies early with partial: true; the thread stays in pending. " +
            "Pass seenCopies and seenReplies from the previous result (at most 1000 each) to suppress repeated partial results. Terminal threads always return all copies and replies; copies mark new finished results with new: true. " +
            "On timedOut call again with the pending ids; nothing is lost.",
            ReadOnly: true),
        new(McpToolNames.Watch, typeof(WatchInput), typeof(WatchResult), "Watch a thread or project",
            "Read events (status changes, progress, messages, settles) on a thread or project after cursor `after`, waiting up to timeoutSeconds for the first. " +
            "At most `limit` (default 50); `more: true` means more wait. `compact` (default true) keeps each thread's latest progress only. " +
            "Pass the returned cursor next time. For live progress; use wait to collect results.", ReadOnly: true),
        new(McpToolNames.Inbox, typeof(InboxInput), typeof(InboxResult), "Take messages for you",
            "Take up to `max` messages for you (your roles' tasks, your agent's queue; `role` for one role, `inboxId` for just your session inbox from open_inbox, `peek` to look without taking). " +
            "Started by the runner for a task: returns that task. Each message has its envelope, decrypted body and `delivery` (deliveryId, threadId for progress and settle). " +
            "A taken task is leased: report progress every few minutes and settle it, or it goes back to the queue. Attachments: fetch_attachment."),
        new(McpToolNames.Progress, typeof(ProgressInput), typeof(ProgressResult), "Report progress",
            "Say in one line what you are doing on a task you hold (optional percent); keeps your lease. If the result says cancelRequested, stop and settle as cancelled."),
        new(McpToolNames.Settle, typeof(SettleInput), typeof(SettleOutput), "Finish a task",
            "Say once how a task you hold ended: result {status: succeeded|failed|abandoned (hand it back for another attempt)|cancelled, summary (one or two sentences), " +
            "refs (PRs, commits, branches, attachments)}. For a message that expects an answer, put it in `reply` {summary, details?, data?} (encrypted for the asker); " +
            "`attach` sends code back with the reply, like send's. Optional `friction`: [{kind, note}] (one sentence each); kinds: code-mismatch, build-or-test, refused, missing-context, merge-or-rebase, other. Notes are encrypted; kinds are counted. Never claim success you didn't verify."),
        new(McpToolNames.Status, typeof(StatusInput), typeof(StatusResult), "Look up threads or a project",
            "Look up threads by id, a project (its threads and totals: tasks, tokens, estimated cost; its state, what needs a person, and when its coordinator last checked in), or neither for your recent threads; use after a conflict or to report to the user. " +
            "Projects and recent threads come compact: `tasks`, one line per thread (status, agent, machine, summary or latest progress, usage, lastError), newest first, " +
            "at most `limit` (default 50; `more` if there are more). Named threadIds come in full as `threads` (compact: true for lines); a broadcast lists each agent's result in `copies`. " +
            "Compact status omits replies and their bodies. Full status of named threads includes up to 200 replies posted so far (more: true if truncated), decrypted locally. " +
            "Named threads report partial: true when other broadcast copies remain pending.",
            ReadOnly: true),
        new(McpToolNames.Cancel, typeof(CancelInput), typeof(CancelResult), "Cancel a thread or project",
            "Cancel a thread and every thread started from it, or a whole project (its open tasks; it ends cancelled). Agents on them are told to stop. Give a short reason. " +
            "Returns the cancelled ids, or `message` when there was nothing to stop. Only cancel what the user asked for or what you started."),
        new(McpToolNames.Retarget, typeof(RetargetInput), typeof(RetargetResult), "Retarget a queued task",
            "Change a queued task's recipient on the same thread and project. `to`: role:<name>, agent:<agt_id> or capabilities:<cap>,<cap>. " +
            "Refused after any copy has been picked up. Preview with dryRun first; a mutation requires operationId and expectedTaskMessageId. " +
            "Keep operationId for retries. reason is at most 500 characters. The local client negotiates support before reading or uploading content, " +
            "and reseals each encrypted input whose key changes. Run on a machine holding all old keys. Returns routing, status and notice.") { Features = McpCatalogFeatures.Retarget },
        new(McpToolNames.Move, typeof(MoveInput), typeof(MoveThreadsResult), "Move a task",
            "Move a task sent under the wrong project instead of sending it again. Give threadId and projectId (null for no project). " +
            "includeSubtasks defaults to true; reason explains why; dryRun previews without changing anything. Returns thread ids and from/to projects. " +
            "Machines may only move tasks sent from this machine/session’s agent. Finished projects refuse tasks; a project at its budget cap refuses running tasks."),
        new(McpToolNames.FindProject, typeof(FindProjectInput), typeof(FindProjectResult), "Find existing work",
            "Read-only lookup by project key or explicitly claimed tracker item (including HTTPS URLs). Returns a visible project or null; never starts work. Use status for its tasks.", ReadOnly: true),
        new(McpToolNames.EnsureProject, typeof(EnsureProjectInput), typeof(EnsureProjectResult), "Find or create the project",
            "executionScope is workspace (default) or owner-runners (only approved personal machines registered to the authenticated creator); it is immutable, and omission on resume preserves it. Ownership is mbus registration identity, not OS or provider credentials. " +
            "Call first for a work item: returns the project for `key` (gh:owner/repo#42, ado:org/project#123 or file:path/to/item.md), creating it with `title` the first time; " +
            "For coordination, supply claimedWorkItems (1–50 tracker keys/URLs): all are atomically claimed for this session inbox. Only coordinationAcquired:true permits dispatch; on conflict stop. Same inbox and same list resumes; no expiry takeover. Cancel interrupted work before a new attempt. Ordinary refs and no-claim callers do not acquire ownership. Tasks sent with its projectId add to its time, tokens and cost."),
        new(McpToolNames.CompleteProject, typeof(CompleteProjectInput), typeof(CompleteProjectResult), "Mark the project finished",
            "When the work is done (or failed): summary for the dashboard, status completed|failed, refs (PRs, branches), and `usage` {model, tokensIn, tokensOut, activeSeconds, estimatedCostUsd?} " +
            "for your own session (its own line in the totals; leave it out when unknown: under Claude Code and Codex the local server reads only usage not yet assigned to another project from this session's log, with a persisted checkpoint; unavailable usage is omitted with a notice). " +
            "Returns the final totals to report to the user."),
        new(McpToolNames.Directory, typeof(DirectoryInput), typeof(DirectoryResult), "List roles and agents",
            "Before planning, list the roles: what each does, delivery mode, required capabilities, holders (machine, vendor, online), queue depth, " +
            "agentsOnline (holders connected now) and agentsFree (of those, with a free slot to start a task now); each agent's defaultModel and models (modelsPartial: it may run others). " +
            "Filter by `role`, `capabilities` or `onlineOnly`. " +
            "Send to roles, not agents, unless you must reach one agent.", ReadOnly: true),
        new(McpToolNames.DescribeRole, typeof(DescribeRoleInput), typeof(DescribeRoleResult), "Read a role's instructions",
            "Read a role's settings and live instructions (what its agents accept and return) before sending it work the first time.", ReadOnly: true),
        new(McpToolNames.Subscribe, typeof(SubscribeInput), typeof(SubscribeResult), "Get events about PRs, builds or work items",
            "Ask for events about references (a PR, build or work item), e.g. pr.merged or checks.completed, delivered as messages to your inbox (or `deliverTo`); read them with inbox."),
        new(McpToolNames.FetchAttachment, typeof(FetchAttachmentInput), typeof(FetchAttachmentResult), "Fetch an attachment",
            "Bring an attachment here, decrypted: a git bundle is fetched into `repository` (default the current directory) as refs/remotes/mbus/<branch> (or `into`) and its commit returned; " +
            "a patch or file is saved to `path` (default a new temp folder), creating missing parent folders. A folder creation failure returns invalid-input with field path. " +
            "Give threadId and attachment id, plus deliveryId for a task you hold. If the repository lacks prerequisite commits, fetch into a repository that has them or ask for a resend with an empty base."),
        new(McpToolNames.Retry, typeof(RetryInput), typeof(SendResult), "Send a finished task again",
            "Send a finished (failed, cancelled, dead-lettered or done) thread's task again as a new thread: same target, type, body, refs and exclusions, the next round of its dedupe key, " +
            "and `note` (what changed) at the top of the details. A single bundle or patch is packaged again from its branch in `repository` (default the current directory). For multiple code attachments use a fresh send with explicit repositories and expectedHead pins for bundles. " +
            "Retry keeps an existing local branch and warns if it is behind the fetched copy; only a missing local branch falls back to refs/remotes/mbus/<branch>, with a notice. A bundle with an empty base sends the whole history. Retrying a thread twice returns the first retry (deduplicated: true). Returns a new thread like send."),
        new(McpToolNames.RequestApproval, typeof(RequestApprovalInput), typeof(ApprovalResult), "Ask a person to approve a step",
            "Before a risky step (deleting data, a migration, anything hard to undo) or when a person must sign off (a design), ask for approval: " +
            "`title` says exactly what will happen, `detail` why and what happens if rejected. Optional `affected` describes impact as plain text (1000 Unicode scalars after line-ending normalization and trimming); it is author-supplied, not verified targets or authority, and requires approvalAffectedV1 support. `approver`: member:<id or email> or role:<name> " +
            "(default: who started the project). Only a person can answer, in the dashboard; you can never approve. " +
            "Returns {approvalId, status: pending, approvers, expiresAt}: then wait_approval. Do the step only once it is approved."),
        new(McpToolNames.WaitApproval, typeof(WaitApprovalInput), typeof(ApprovalResult), "Wait for an approval",
            "Block until a person answers the approval or timeoutSeconds passes (the local server returns by its client's limit, as for wait). " +
            "Returns status approved (do exactly what was asked), rejected (don't; carry on without it or stop), expired (treat as rejected) " +
            "or pending with timedOut (call again), plus decidedBy and comment.", ReadOnly: true),
        new(McpToolNames.ProposeRole, typeof(ProposeRoleInput), typeof(RoleProposalSubmitted), "Propose a role change",
            "Submit a complete create/update definition for a workspace owner's editable dashboard review. Nothing is applied until the owner explicitly applies it. Requires a supported server and an original interactive coordinator session; workers cannot submit. Keep idempotencyKey unchanged only for identical retries.") { Features = McpCatalogFeatures.RoleProposals },
        new(McpToolNames.GetRoleProposal, typeof(GetRoleProposalInput), typeof(RoleProposalDetail), "Read your role proposal",
            "Read the authoritative state/result bound to this original coordinator record, even after its inbox expires. Only approved means the role was applied. Owners decide in the dashboard.", ReadOnly: true) { Features = McpCatalogFeatures.RoleProposals },
        new(McpToolNames.WaitRoleProposal, typeof(WaitRoleProposalInput), typeof(RoleProposalDetail), "Wait for your role proposal",
            "Wait 0-30 seconds for authoritative proposal state. Pending or applying at the deadline is not rejection or failure; call again. Original coordinator only; workers cannot read proposal payloads.", ReadOnly: true) { Features = McpCatalogFeatures.RoleProposals },
        new(McpToolNames.OpenInbox, typeof(OpenInboxInput), typeof(OpenInboxResult), "Get woken for late replies",
            "Before ending your turn with tasks still running: open this session's inbox. Replies to what you send afterwards go there " +
            "(the local server sets replyTo itself), and one that arrives after this session has closed resumes this same session on this machine " +
            "through its runner, in this folder, with read-only access and the mbus tools. While the session is open, read them with inbox {inboxId} or keep using wait. " +
            "Safe to repeat: the same session gets the same inbox. Returns {inboxId, replyTo, expiresAt, resumable, sessionId?, notice?}."),
        new(McpToolNames.SendVerified, typeof(SendInput), typeof(SendResult), "Send a task with required verification",
            "Send one role-targeted task with `verification` {environment, requiredChecks}.") { Features = McpCatalogFeatures.VerifiedTasks },
        new(McpToolNames.SendManyVerified, typeof(SendManyInput), typeof(SendManyResult), "Send tasks with required verification",
            "Send a batch containing required verification. Each item of `messages` takes send's arguments.") { Features = McpCatalogFeatures.VerifiedTasks },
        new(McpToolNames.ListWorkflows, typeof(ListWorkflowsInput), typeof(ListWorkflowsResult), "List workflows",
            "List the workspace's workflows, their published versions and settings.", ReadOnly: true) { Features = McpCatalogFeatures.Workflows },
        new(McpToolNames.StartJob, typeof(StartJobInput), typeof(StartJobResult), "Start a workflow job",
            "Start a job for workItem (such as gh:owner/repo#42) using a workflow id or name. Optional instructions, limitUsd and runsOn (workspace or owner-runners). Returns the job id and dashboard link immediately.") { Features = McpCatalogFeatures.Workflows },
        new(McpToolNames.JobStatus, typeof(JobStatusInput), typeof(JobStatusResult), "Read job progress",
            "Read the job's state, spending, active steps, step runs and dashboard link by jobId.", ReadOnly: true) { Features = McpCatalogFeatures.Workflows },
        new(McpToolNames.FindJob, typeof(FindProjectInput), typeof(FindProjectResult), "Find existing work",
            "Read-only lookup by job key or explicitly claimed tracker item (including HTTPS URLs). Returns a visible job or null. Use status for its tasks.", ReadOnly: true) { Features = McpCatalogFeatures.Workflows },
        new(McpToolNames.EnsureJob, typeof(EnsureProjectInput), typeof(EnsureProjectResult), "Find or create a hands-on job",
            "Give key and title on first use. executionScope is workspace (default) or owner-runners and stays fixed. Supply all explicit tracker keys or URLs in claimedWorkItems for this session inbox. Dispatch only on coordinationAcquired:true and stop on conflict. Resume with the same inbox and claims. Use the returned projectId when sending tasks.") { Features = McpCatalogFeatures.Workflows },
        new(McpToolNames.CompleteJob, typeof(CompleteProjectInput), typeof(CompleteProjectResult), "Mark a hands-on job finished",
            "Give the projectId, dashboard summary, status completed|failed, and refs. Include your session usage if known. Returns final totals to report.") { Features = McpCatalogFeatures.Workflows },
    ];

    /// <summary>
    /// What an agent the runner started for one task needs (the original issue): read the task, fetch its code, report progress,
    /// settle. Everything else is for orchestrating, and each listed tool costs tokens on every turn.
    /// </summary>
    public static IReadOnlyList<string> WorkerTools { get; } =
        [McpToolNames.Inbox, McpToolNames.Progress, McpToolNames.Settle, McpToolNames.FetchAttachment];

    private static readonly Dictionary<string, McpToolSpec> ByName = Specs.ToDictionary(s => s.Name, StringComparer.Ordinal);

    /// <summary>The spec for a tool name, or null.</summary>
    public static McpToolSpec? Find(string name) => ByName.GetValueOrDefault(name);

    /// <summary>Fields the local server fills itself, hidden from the schema it shows agents.</summary>
    private static readonly HashSet<string> LocalHidden = new(StringComparer.Ordinal) { "sealedBody", "sealedReply", "attachments", "replyOnly", "frictionOnly", "request" };

    /// <summary>
    /// The tool list. <paramref name="local"/>: the local server's (all tools, sealed fields hidden); else the remote endpoint's.
    /// <paramref name="only"/>: just these (a runner-started worker's, see <see cref="WorkerTools"/>); null for all.
    /// </summary>
    public static IList<Tool> Tools(bool local, IReadOnlySet<string>? only = null) => Tools(local, only, McpCatalogFeatures.None);

    /// <summary>
    /// The tool list with optional service features and schemas enabled only for installed handlers.
    /// Listing never replaces live registry negotiation or handler authorization.
    /// </summary>
    public static IList<Tool> Tools(bool local, IReadOnlySet<string>? only, McpCatalogFeatures features) =>
    [
        .. Specs.Where(s => (local
            ? (s.Features & McpCatalogFeatures.Workflows & ~features) == 0
            : (features & s.Features) == s.Features) && (local ? !McpToolNames.ServiceOnly.Contains(s.Name) : !McpToolNames.LocalOnly.Contains(s.Name)) && (only is null || only.Contains(s.Name))).Select(s => new Tool
        {
            Name = s.Name,
            Title = s.Title,
            Description = !local && s.Name == McpToolNames.Settle
                ? "Finish a task with result {status, summary, refs, frictionKinds?}. Give only fixed kind names in result.frictionKinds; plaintext friction notes are accepted only by the local mbus tool. Put an answer in reply or sealedReply. Never claim success you didn't verify."
                : local && s.Name is McpToolNames.Send or McpToolNames.SendMany or McpToolNames.Settle
                    ? s.Description + " Each Git bundle can set expectedHead to check its advertised branch against a full commit SHA before upload."
                    : local && s.Name == McpToolNames.Directory
                        ? s.Description + " Supply verificationRepositoryId (github:<numeric-id>) to discover approved environments in the optional verification result."
                        : !local && (features & McpCatalogFeatures.VerifiedTasks) == 0
                            ? s.Description.Replace(VerificationSendDescription, "", StringComparison.Ordinal)
                            : Describe(s, local, features),
            InputSchema = Schema(s.Input, local, features),
            Annotations = new ToolAnnotations { Title = s.Title, ReadOnlyHint = s.ReadOnly, DestructiveHint = s.Name == McpToolNames.Cancel, OpenWorldHint = false },
        }),
    ];

    private static string Describe(McpToolSpec spec, bool local, McpCatalogFeatures features)
    {
        if (!local || (features & McpCatalogFeatures.Workflows) == 0) return spec.Description;
        var replacement = spec.Name switch
        {
            McpToolNames.FindProject => McpToolNames.FindJob,
            McpToolNames.EnsureProject => McpToolNames.EnsureJob,
            McpToolNames.CompleteProject => McpToolNames.CompleteJob,
            _ => null,
        };
        return replacement is null ? spec.Description
            : $"Old name of {replacement}. This alias goes away in the next release. " + spec.Description;
    }

    /// <summary>
    /// The JSON schema of a contract input type, as MessageBusJson reads it, kept small (the original issue): agents re-read every
    /// schema on every turn. Targets are shown in their text form only (<c>role:developer</c>; the object form is still
    /// read), optional fields without their <c>null</c> alternative, and numbers without the text form the reader also
    /// accepts. <c>send_many</c>'s messages point at <c>send</c> instead of repeating its schema.
    /// </summary>
    public static JsonElement Schema(Type input, bool local) => Schema(input, local, McpCatalogFeatures.None);

    private static JsonElement Schema(Type input, bool local, McpCatalogFeatures features)
    {
        var node = MessageBusJson.Options.GetJsonSchemaAsNode(input, new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (ctx, schema) =>
            {
                if (!local && (features & McpCatalogFeatures.VerifiedTasks) == 0
                    && ctx.TypeInfo.Type == typeof(SendInput) && schema["properties"] is JsonObject send)
                    send.Remove("verification");
                if (ctx.TypeInfo.Type == typeof(MessageTarget) && ctx.PropertyInfo is not null)
                    return new JsonObject { ["type"] = "string", ["description"] = "role:<name>, agent:<agt_id>, capabilities:<cap>,<cap> or inbox:<inb_id>" };
                if (ctx.TypeInfo.Type == typeof(AttachSpec) && schema["properties"] is JsonObject attachment)
                {
                    if (!local) attachment.Remove("expectedHead");
                    else if (attachment["expectedHead"] is JsonObject head)
                    {
                        head["pattern"] = "^([0-9a-fA-F]{40}|[0-9a-fA-F]{64})$";
                        head["description"] = "Git bundles only. The full commit SHA the advertised branch must point to.";
                    }
                }
                if (ctx.TypeInfo.Type == typeof(MessageBody) && schema["properties"] is JsonObject body)
                    body.Remove("friction");
                return schema;
            },
        });
        if (input == typeof(SendManyInput) && node["properties"]?["messages"] is JsonObject messages)
            messages["items"] = new JsonObject { ["type"] = "object", ["description"] = "One message: send's arguments." };
        if (!local && input == typeof(DirectoryInput) && node["properties"] is JsonObject directory)
            directory.Remove("verificationRepositoryId");
        if (local) Strip(node);
        if (input == typeof(RequestApprovalInput) && node["properties"]?["affected"] is JsonObject affected)
        {
            affected["maxLength"] = 1000;
            affected["description"] = "Author-supplied impact, not verified targets or authority. Normalize CRLF/CR to LF, trim, empty becomes null. At most 1000 Unicode scalars; reject invalid surrogates and controls except LF/TAB. Requires approvalAffectedV1 and an exact normalized response echo.";
        }
        if (input == typeof(SettleInput) && node["properties"] is JsonObject settle)
        {
            if (local)
            {
                settle.Remove("attempt");
                settle["outcome"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Required for workflow steps. Use an exact outcome word from the task prompt.",
                };
            }
            else settle.Remove("friction");
            settle.Remove("frictionOnly");
        }
        // The runner reports usage and the exit code from the CLI's own output; an agent doesn't know them.
        if (local && input == typeof(SettleInput) && node["properties"]?["result"]?["properties"] is JsonObject result)
        {
            result.Remove("outcome");
            result.Remove("usage");
            result.Remove("exitCode");
            result.Remove("frictionKinds");
        }
        if (input == typeof(SettleInput) && node["properties"]?["friction"] is JsonObject friction)
        {
            friction["maxItems"] = 12;
            if (friction["items"]?["properties"] is JsonObject fields)
            {
                fields["kind"]!["enum"] = new JsonArray("code-mismatch", "build-or-test", "refused", "missing-context", "merge-or-rebase", "other");
                fields["note"]!["minLength"] = 1;
                fields["note"]!["maxLength"] = 500;
            }
        }
        Simplify(node);
        if (input == typeof(RetargetInput))
        {
            node["properties"]!["reason"]!["maxLength"] = 500;
            node["properties"]!["to"]!["description"] = "role:<name>, agent:<agt_id> or capabilities:<cap>,<cap>; inbox is not supported";
        }
        // This required argument uses null to mean no project, so its null alternative must stay in the schema.
        if (input == typeof(MoveInput)) node["properties"]!["projectId"]!["type"] = new JsonArray("string", "null");
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }

    /// <summary>Drops what the exporter adds for nullability and number-as-text reading; arguments are read the same way.</summary>
    private static void Simplify(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["type"] is JsonArray types)
                {
                    var kept = types.Select(t => t!.GetValue<string>()).Where(t => t != "null").ToList();
                    if (kept.Contains("string") && (kept.Contains("integer") || kept.Contains("number")))
                    {
                        kept.Remove("string");
                        obj.Remove("pattern");
                    }
                    obj["type"] = kept.Count == 1 ? kept[0] : new JsonArray([.. kept.Select(k => (JsonNode)k)]);
                }
                if (obj["enum"] is JsonArray values)
                    foreach (var none in values.Where(v => v is null).ToList()) values.Remove(none);
                foreach (var (_, child) in obj.ToList()) Simplify(child);
                break;
            case JsonArray array:
                foreach (var child in array) Simplify(child);
                break;
        }
    }

    private static void Strip(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["properties"] is JsonObject props)
                {
                    foreach (var name in LocalHidden) props.Remove(name);
                    if (obj["required"] is JsonArray required)
                        foreach (var r in required.Where(r => r is not null && LocalHidden.Contains(r.GetValue<string>())).ToList())
                            required.Remove(r);
                }
                foreach (var (_, child) in obj.ToList()) Strip(child);
                break;
            case JsonArray array:
                foreach (var child in array) Strip(child);
                break;
        }
    }

    // ---- arguments and results ----

    /// <summary>
    /// The tool's arguments as its input type. Targets given as text (<c>role:developer</c>) become
    /// target objects. Throws <see cref="McpToolException"/> with <see cref="McpErrorCodes.InvalidInput"/> when they don't fit.
    /// </summary>
    public static T ReadInput<T>(IDictionary<string, JsonElement>? arguments) => (T)ReadInput(typeof(T), arguments);

    /// <summary>The tool's arguments as <paramref name="type"/> (see <see cref="ReadInput{T}"/>).</summary>
    public static object ReadInput(Type type, IDictionary<string, JsonElement>? arguments)
    {
        var obj = new JsonObject();
        foreach (var (name, value) in arguments ?? new Dictionary<string, JsonElement>())
            obj[name] = JsonNode.Parse(value.GetRawText());
        NormalizeTargets(obj);
        try
        {
            return obj.Deserialize(MessageBusJson.Options.GetTypeInfo(type))
                   ?? throw McpToolException.Invalid("arguments", "Give the tool's arguments.");
        }
        catch (JsonException e)
        {
            throw McpToolException.Invalid(string.IsNullOrEmpty(e.Path) ? "arguments" : e.Path.TrimStart('$', '.'),
                "The arguments don't match the tool's schema: " + e.Message);
        }
        catch (NotSupportedException e)
        {
            throw McpToolException.Invalid("arguments", "The arguments don't match the tool's schema: " + e.Message);
        }
    }

    private static readonly string[] TargetFields = ["to", "replyTo", "deliverTo"];

    private static void NormalizeTargets(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var field in TargetFields)
                {
                    if (obj[field] is JsonValue value && value.TryGetValue<string>(out var text))
                    {
                        if (!MessageTarget.TryParse(text, out var target))
                            throw McpToolException.Invalid(field, $"'{text}' isn't a target. Write role:<name>, agent:<agt_id>, capabilities:<cap>,<cap> or inbox:<inb_id>.");
                        obj[field] = JsonSerializer.SerializeToNode(target, (JsonTypeInfo<MessageTarget>)MessageBusJson.Options.GetTypeInfo(typeof(MessageTarget)));
                    }
                }
                foreach (var (_, child) in obj.ToList()) NormalizeTargets(child);
                break;
            case JsonArray array:
                foreach (var child in array) NormalizeTargets(child);
                break;
        }
    }

    /// <summary>A successful result: the contract JSON as structured content and as text.</summary>
    public static CallToolResult Ok<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, MessageBusJson.Options.GetTypeInfo(typeof(T)));
        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = element.GetRawText() }],
            StructuredContent = element,
        };
    }

    /// <summary>A failed call: the <see cref="McpError"/> as structured content and text, with isError set.</summary>
    public static CallToolResult Error(McpError error)
    {
        var element = JsonSerializer.SerializeToElement(error, MessageBusJson.Options.GetTypeInfo(typeof(McpError)));
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = element.GetRawText() }],
            StructuredContent = element,
        };
    }

    /// <summary>The result's structured content as <typeparamref name="T"/>; throws <see cref="McpToolException"/> carrying its error when it failed.</summary>
    public static T ReadResult<T>(CallToolResult result)
    {
        var element = result.StructuredContent ?? (result.Content.OfType<TextContentBlock>().FirstOrDefault() is { } text
            ? JsonDocument.Parse(text.Text).RootElement
            : throw new McpToolException(new McpError { Code = McpErrorCodes.Internal, Message = "The service sent an empty result." }));
        if (result.IsError == true)
            throw new McpToolException(element.Deserialize((JsonTypeInfo<McpError>)MessageBusJson.Options.GetTypeInfo(typeof(McpError)))
                                       ?? new McpError { Code = McpErrorCodes.Internal, Message = element.GetRawText() });
        return element.Deserialize((JsonTypeInfo<T>)MessageBusJson.Options.GetTypeInfo(typeof(T)))
               ?? throw new McpToolException(new McpError { Code = McpErrorCodes.Internal, Message = "The service sent an empty result." });
    }
}

/// <summary>A tool call that fails with a structured <see cref="McpError"/>.</summary>
/// <param name="error">The structured error returned to the caller.</param>
public sealed class McpToolException(McpError error) : Exception(error.Message)
{
    /// <summary>The structured tool error.</summary>
    public McpError Error { get; } = error;

    /// <summary>An invalid argument the caller can correct.</summary>
    public static McpToolException Invalid(string field, string message) =>
        new(new McpError { Code = McpErrorCodes.InvalidInput, Message = message, Field = field });

    /// <summary>A requested resource or tool that does not exist.</summary>
    public static McpToolException NotFound(string message) => new(new McpError { Code = McpErrorCodes.NotFound, Message = message });

    /// <summary>An operation the caller is not allowed to perform.</summary>
    public static McpToolException Forbidden(string message) => new(new McpError { Code = McpErrorCodes.Forbidden, Message = message });

    /// <summary>A transient failure the caller can retry.</summary>
    public static McpToolException Unavailable(string message) =>
        new(new McpError { Code = McpErrorCodes.Unavailable, Message = message, Retryable = true });
}
