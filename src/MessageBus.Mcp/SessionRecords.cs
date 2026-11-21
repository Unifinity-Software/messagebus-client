using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Contracts.Messaging;

namespace MessageBus.Mcp;

/// <summary>
/// One session inbox as this machine knows it (the original issue): which agent session opened it, in which folder, and how to tell
/// whether that session is still open. Kept on this machine only (in mbus's settings folder, readable by this user
/// alone), never sent to the service: the runner reads it to resume that same session for a reply that arrives after
/// the session closed.
/// </summary>
public sealed record SessionRecord
{
    /// <summary>The inbox (<c>inb_…</c>).</summary>
    public required string InboxId { get; init; }

    /// <summary>Its workspace.</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>Immutable original host authority; legacy records remain Unknown.</summary>
    public ProposalAuthority OriginAuthority { get; init; }

    /// <summary>Random immutable local record id, assigned once when the inbox is opened.</summary>
    public string? CoordinatorRecordId { get; init; }

    /// <summary>The enrolled machine that opened this record.</summary>
    public string? MachineId { get; init; }

    /// <summary>The agent it belongs to (<c>agt_…</c>), as the service recorded it.</summary>
    public string? AgentId { get; init; }

    /// <summary>The agent kind that opened it (<c>claude-code</c>, <c>codex</c>), or null when the MCP client didn't say.</summary>
    public string? AgentKind { get; init; }

    /// <summary>The session's working folder: a resume runs there.</summary>
    public required string Folder { get; init; }

    /// <summary>Claude Code's or Codex's session id; null when no session log was found (a reply then starts a new session).</summary>
    public string? SessionId { get; init; }

    /// <summary>The <c>mbus mcp</c> process serving the session while it is open (an interactive session).</summary>
    public int? ProcessId { get; init; }

    /// <summary>When that process started, so a later process given the same id isn't taken for it.</summary>
    public DateTimeOffset? ProcessStartedAt { get; init; }

    /// <summary>The runner's delivery whose run opened it (a session the runner started): open while that run is.</summary>
    public string? RunDeliveryId { get; init; }

    /// <summary>Keep this inbox for explicit coordination recovery, even after it expires.</summary>
    public bool CoordinationRecovery { get; init; }

    /// <summary>Retain original proposal provenance independently of inbox expiry and host lifetime.</summary>
    public bool RoleProposalProvenance { get; init; }

    /// <summary>Opaque CLI retry binding keys, saved before sending a proposal.</summary>
    public IReadOnlyList<string> RoleProposalRetryKeys { get; init; } = [];

    /// <summary>The local MCP host, including when a runner owns the session.</summary>
    public int? HostProcessId { get; init; }
    public DateTimeOffset? HostProcessStartedAt { get; init; }

    /// <summary>When it was opened.</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the inbox closes.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Replies (message ids) the session has already read through its tools: they never resume it.</summary>
    public IReadOnlyList<string> Seen { get; init; } = [];

    /// <summary>The inbox as a send's <c>replyTo</c>.</summary>
    public InboxTarget ReplyTo => new() { InboxId = InboxId };

    /// <summary>
    /// Whether the <c>mbus mcp</c> process that serves the session is still running (its session is open). A process
    /// whose start can't be read is taken as running: the session is never run twice at once.
    /// </summary>
    public bool ProcessRunning()
    {
        return Running(ProcessId, ProcessStartedAt);
    }

    public bool HostRunning() => Running(HostProcessId, HostProcessStartedAt);

    private static bool Running(int? processId, DateTimeOffset? processStartedAt)
    {
        if (processId is not { } pid) return false;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            return processStartedAt is not { } started || Math.Abs((process.StartTime.ToUniversalTime() - started.UtcDateTime).TotalSeconds) < 2;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return true;
        }
    }
}

/// <summary>The session records in one mbus settings folder (<c>sessions/&lt;inbox&gt;.json</c>).</summary>
/// <param name="configDirectory">mbus's settings folder (<c>MBUS_HOME</c>).</param>
public sealed class SessionRecords(string configDirectory)
{
    /// <summary>Most reply ids kept per record.</summary>
    private const int MaxSeen = 500;

    /// <summary>The folder the records are in.</summary>
    public string Directory { get; } = Path.Combine(configDirectory, "sessions");

    /// <summary>The record for an inbox, or null.</summary>
    public SessionRecord? Find(string inboxId) => IsSafeId(inboxId) ? Read(PathOf(inboxId)) : null;

    /// <summary>Finds the original interactive CLI proposal record for an opaque retry binding, refusing ambiguous matches.</summary>
    public SessionRecord? ForRoleProposalRetry(string workspaceId, string machineId, string retryKey)
    {
        var matches = All().Where(r => r.WorkspaceId == workspaceId && r.MachineId == machineId
            && r.RoleProposalProvenance && r.RoleProposalRetryKeys.Contains(retryKey, StringComparer.Ordinal)).ToList();
        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidOperationException("Multiple original coordinator records match this proposal retry; give --inbox explicitly."),
        };
    }

    /// <summary>The newest record of an open inbox for this agent session, or null.</summary>
    public SessionRecord? ForSession(string workspaceId, string agentKind, string sessionId, DateTimeOffset now) =>
        All().Where(r => r.WorkspaceId == workspaceId && r.AgentKind == agentKind && r.SessionId == sessionId && r.ExpiresAt > now)
            .OrderByDescending(r => r.ExpiresAt).FirstOrDefault();

    /// <summary>Saves a record (replacing the inbox's last one), readable by this user only.</summary>
    public void Save(SessionRecord record)
    {
        if (!IsSafeId(record.InboxId)) throw new ArgumentException("Not an inbox id.", nameof(record));
        if (!System.IO.Directory.Exists(Directory))
        {
            if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(Directory);
            else System.IO.Directory.CreateDirectory(Directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var original = Find(record.InboxId);
        if (original is not null &&
            (original.OriginAuthority != record.OriginAuthority || original.CoordinatorRecordId != record.CoordinatorRecordId
             || original.MachineId != record.MachineId || original.WorkspaceId != record.WorkspaceId
             || original.AgentId != record.AgentId || original.AgentKind != record.AgentKind
             || original.SessionId != record.SessionId || original.Folder != record.Folder))
            throw new InvalidOperationException("An inbox record's original identity and authority cannot change.");
        // A later maintenance write from an older in-memory copy must not discard durable provenance.
        record = record with
        {
            RoleProposalProvenance = record.RoleProposalProvenance || original?.RoleProposalProvenance == true,
            RoleProposalRetryKeys = [.. (original?.RoleProposalRetryKeys ?? []).Concat(record.RoleProposalRetryKeys).Distinct(StringComparer.Ordinal)],
        };
        var json = new JsonObject
        {
            ["inboxId"] = record.InboxId,
            ["originAuthority"] = record.OriginAuthority.ToString(),
            ["coordinatorRecordId"] = record.CoordinatorRecordId,
            ["machineId"] = record.MachineId,
            ["workspaceId"] = record.WorkspaceId,
            ["agentId"] = record.AgentId,
            ["agentKind"] = record.AgentKind,
            ["folder"] = record.Folder,
            ["sessionId"] = record.SessionId,
            ["processId"] = record.ProcessId,
            ["processStartedAt"] = record.ProcessStartedAt,
            ["runDeliveryId"] = record.RunDeliveryId,
            ["coordinationRecovery"] = record.CoordinationRecovery,
            ["roleProposalProvenance"] = record.RoleProposalProvenance,
            ["roleProposalRetryKeys"] = new JsonArray([.. record.RoleProposalRetryKeys.Select(s => (JsonNode)s)]),
            ["hostProcessId"] = record.HostProcessId,
            ["hostProcessStartedAt"] = record.HostProcessStartedAt,
            ["createdAt"] = record.CreatedAt,
            ["expiresAt"] = record.ExpiresAt,
            ["seen"] = new JsonArray([.. record.Seen.TakeLast(MaxSeen).Select(s => (JsonNode)s)]),
        };
        // Written whole then moved into place, so the runner never reads half a record.
        var target = PathOf(record.InboxId);
        var temp = target + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temp, json.ToJsonString());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>Records that the session read these replies (best effort: a record that can't be written is left as it was).</summary>
    public void MarkSeen(string inboxId, IEnumerable<string> messageIds)
    {
        var ids = messageIds.ToList();
        if (ids.Count == 0 || Find(inboxId) is not { } record) return;
        var seen = record.Seen.Concat(ids.Where(id => !record.Seen.Contains(id, StringComparer.Ordinal))).ToList();
        if (seen.Count == record.Seen.Count) return;
        try
        {
            Save(record with { Seen = seen });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Removes closed inbox records, retaining live hosts, proposal provenance and explicit coordination recovery. A runner's
    /// tools connection ending does not end its run, so retain its records until the host process exits.
    /// </summary>
    public void Prune(DateTimeOffset now, string? protectedInboxId = null)
    {
        foreach (var record in All().Where(r => r.ExpiresAt <= now && r.InboxId != protectedInboxId
            && !r.CoordinationRecovery && !r.RoleProposalProvenance && !r.ProcessRunning() && !r.HostRunning()
            // Older runner records have no host metadata: conservatively retain their resume state.
            && !(r.RunDeliveryId is not null && r.HostProcessId is null)))
        {
            try
            {
                File.Delete(PathOf(record.InboxId));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private IEnumerable<SessionRecord> All()
    {
        if (!System.IO.Directory.Exists(Directory)) return [];
        try
        {
            return [.. System.IO.Directory.EnumerateFiles(Directory, "inb_*.json").Select(Read).OfType<SessionRecord>()];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private string PathOf(string inboxId) => Path.Combine(Directory, inboxId + ".json");

    /// <summary>Inbox ids are <c>inb_</c> and letters and digits: never a path.</summary>
    private static bool IsSafeId(string id) => id.Length is > 4 and < 100 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    private static SessionRecord? Read(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var json = JsonNode.Parse(File.ReadAllText(file));
            if (json is not JsonObject o || Text(o, "inboxId") is not { } inbox || Text(o, "workspaceId") is not { } ws || Text(o, "folder") is not { } folder
                || Time(o, "expiresAt") is not { } expires) return null;
            return new SessionRecord
            {
                InboxId = inbox, WorkspaceId = ws, Folder = folder, ExpiresAt = expires,
                OriginAuthority = Enum.TryParse<ProposalAuthority>(Text(o, "originAuthority"), out var origin) && Enum.IsDefined(origin) ? origin : ProposalAuthority.Unknown,
                CoordinatorRecordId = Text(o, "coordinatorRecordId"), MachineId = Text(o, "machineId"),
                AgentId = Text(o, "agentId"), AgentKind = Text(o, "agentKind"), SessionId = Text(o, "sessionId"),
                ProcessId = o["processId"] is JsonValue p && p.TryGetValue<int>(out var pid) ? pid : null,
                ProcessStartedAt = Time(o, "processStartedAt"), RunDeliveryId = Text(o, "runDeliveryId"),
                CoordinationRecovery = o["coordinationRecovery"] is JsonValue recovery && recovery.TryGetValue<bool>(out var keep) && keep,
                RoleProposalProvenance = o["roleProposalProvenance"] is JsonValue proposal && proposal.TryGetValue<bool>(out var retain) && retain,
                RoleProposalRetryKeys = o["roleProposalRetryKeys"] is JsonArray retries
                    ? [.. retries.Select(s => s is JsonValue v && v.TryGetValue<string>(out var key) ? key : null).OfType<string>()] : [],
                HostProcessId = o["hostProcessId"] is JsonValue host && host.TryGetValue<int>(out var hostPid) ? hostPid : null,
                HostProcessStartedAt = Time(o, "hostProcessStartedAt"),
                CreatedAt = Time(o, "createdAt") ?? expires,
                Seen = o["seen"] is JsonArray seen ? [.. seen.Select(s => s is JsonValue v && v.TryGetValue<string>(out var id) ? id : null).OfType<string>()] : [],
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }

        static string? Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

        static DateTimeOffset? Time(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<DateTimeOffset>(out var t) ? t : null;
    }
}

/// <summary>
/// Claude Code's and Codex's session logs on this machine (the original issue): which session works in a folder now, and whether a
/// session can still be resumed. The same logs <see cref="SessionUsage"/> reads.
/// </summary>
public static class AgentSessions
{
    /// <summary>The newest session of <paramref name="agentKind"/> in <paramref name="folder"/>, or null.</summary>
    /// <param name="agentKind"><c>claude-code</c> or <c>codex</c>.</param>
    /// <param name="folder">The session's working folder.</param>
    /// <param name="claudeHome">Claude Code's settings folder.</param>
    /// <param name="codexHome">Codex's home folder.</param>
    /// <param name="now">Now (Codex files sessions by the day they started).</param>
    public static string? Newest(string? agentKind, string folder, string claudeHome, string codexHome, DateTimeOffset now) => agentKind switch
    {
        "claude-code" => SessionUsage.ClaudeLog(claudeHome, folder) is { } log ? Path.GetFileNameWithoutExtension(log.Name) : null,
        "codex" => SessionUsage.CodexLog(codexHome, folder, now.AddDays(-7), now) is { } log ? SessionUsage.CodexSessionId(log.FullName) : null,
        _ => null,
    };

    /// <summary>Whether that session's log is still on this machine (a resume needs it).</summary>
    public static bool Exists(string agentKind, string folder, string sessionId, string claudeHome, string codexHome, DateTimeOffset now) => agentKind switch
    {
        "claude-code" => SessionUsage.ClaudeFolder(claudeHome, folder) is { Exists: true } dir && File.Exists(Path.Combine(dir.FullName, sessionId + ".jsonl")),
        // Codex names each log rollout-<time>-<session id>.jsonl.
        "codex" => SessionUsage.CodexLogs(codexHome, now.AddDays(-31), now).Any(f => f.Name.EndsWith("-" + sessionId + ".jsonl", StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };
}
