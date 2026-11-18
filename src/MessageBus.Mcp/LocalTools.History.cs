using System.Collections.Concurrent;
using MessageBus.Attachments.Client;
using MessageBus.Contracts.Attachments;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Security;

namespace MessageBus.Mcp;

/// <summary>
/// the original issue: a task's git bundle carries only the commits after the newest one this machine sent the same target for that
/// repository (<see cref="SentCode"/>). A runner whose copy of the repository lacks the earlier commits says so in its
/// progress (<see cref="ProgressUpdate.Request"/>), and this server answers by itself, never the model: a bundle of the
/// branch's whole history, sealed for the same key as the task, sent to the inbox the run opened for it. The server can't
/// do it, as bundles are sealed for the recipient. It listens on each such thread with <c>watch</c>, which the service
/// answers when something happens on it, until the thread finishes or this server stops.
/// </summary>
internal sealed partial class LocalTools
{
    /// <summary>A git bundle packaged for a task this server sent.</summary>
    /// <param name="AttachmentId">Its attachment.</param>
    /// <param name="Repository">The repository it came from.</param>
    /// <param name="Branch">Its branch.</param>
    /// <param name="Head">The branch's commit when sent.</param>
    /// <param name="SourceRef">The full ref used to package this branch.</param>
    /// <param name="RootCommit">The repository's first commit (null when git couldn't say).</param>
    /// <param name="To">The task's target.</param>
    /// <param name="Key">The key it was sealed for.</param>
    /// <param name="Incremental">It starts after a commit the receiver is expected to have.</param>
    /// <param name="Notice">Guidance when the selected local branch is behind its fetched copy.</param>
    internal sealed record SentBundle(string AttachmentId, string Repository, string Branch, string SourceRef, string Head, string? RootCommit, MessageTarget To,
        PublicKeyInfo Key, bool Incremental, string? Notice);

    private readonly ConcurrentDictionary<string, Task> _answering = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _closing = new();

    /// <summary>A message that carries code for a runner, not an answer: never shown to the model.</summary>
    private static bool IsCodeForRunner(ReceivedMessage message) => message.Envelope.Type == ProgressRequestKinds.GitHistoryMessageType;

    /// <summary>
    /// Packages a task's bundle, recorded for <see cref="Remember"/>: with no base, after what this target was sent last; an empty
    /// base (a retry of a task that went with the whole history) sends the whole history again.
    /// </summary>
    private async Task<AttachmentRef> PackageTaskBundleAsync(AttachSpec spec, string path, MessageTarget to, PublicKeyInfo key, string? threadId,
        List<SentBundle> bundles, CancellationToken ct)
    {
        var branch = spec.Branch!;
        var notice = await GitBundles.BehindFetchedNoticeAsync(path, branch, ct);
        string? root = null;
        try
        {
            root = await GitBundles.RootCommitAsync(path, branch, ct);
        }
        catch (GitException)
        {
            // No such branch: CreateAsync below says so.
        }
        var baseCommit = spec.Base;
        if (baseCommit is null && _o.IncrementalBundles && root is { Length: > 0 }
            && _sentCode.LastSent(_ws, to.ToString(), root) is { } last)
            baseCommit = await GitBundles.SuggestBaseAsync(path, branch, last, ct);
        var (attachment, bundle) = await _attachments.SendGitBundleAsync(path, to, key, branch, baseCommit is { Length: > 0 } ? baseCommit : null, threadId, ct);
        bundles.Add(new SentBundle(attachment.Id, path, bundle.Branch, branch, bundle.HeadCommit, root is { Length: > 0 } ? root : null, to, key, bundle.BaseCommit is not null, notice));
        return attachment;
    }

    private async Task AddParentBundleNoticesAsync(string parent, List<SentBundle> bundles,
        Dictionary<string, StatusResult> parents, CancellationToken ct)
    {
        try
        {
            if (!parents.TryGetValue(parent, out var status))
            {
                // Cache even a failed lookup: a batch checks each parent at most once.
                parents[parent] = new StatusResult();
                parents[parent] = status = await ForwardAsync<StatusInput, StatusResult>(McpToolNames.Status,
                    new StatusInput { ThreadIds = [parent], Compact = false }, ct);
            }
            var workers = status.Threads.SelectMany(t => t.Copies.Select(c => c.AgentId).Append(t.AgentId))
                .OfType<string>().ToHashSet(StringComparer.Ordinal);
            var senders = status.Threads.Select(t => t.Sender.AgentId).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var attachments = status.Replies.Where(r => !IsCodeForRunner(r)
                    && r.Envelope.Sender.AgentId is { } agent && workers.Contains(agent)
                    && agent != _agent && !senders.Contains(agent))
                .SelectMany(r => r.Envelope.Attachments)
                .Where(a => a.Format == AttachmentFormat.GitBundle && a.Branch is not null)
                .DistinctBy(a => a.Id).ToList();
            var refs = status.Threads.SelectMany(t => new[] { t.Result }.Concat(t.Copies.Select(c => c.Result)))
                .OfType<SettleResult>().SelectMany(r => r.Refs)
                .Where(r => r.Kind == RefKinds.GitCommit && r.Sha is { Length: >= 7 and <= 64 } sha && sha.All(Uri.IsHexDigit)).ToList();
            for (var i = 0; i < bundles.Count; i++)
            {
                var bundle = bundles[i];
                var candidates = new List<(string? Commit, string? Branch, string? Attachment)>();
                candidates.AddRange(attachments.Select(a => (_fetchedCode.LastSent(_ws, "attachment", a.Id), a.Branch, (string?)a.Id)));
                foreach (var reference in refs)
                    if (reference.Repo is null || await MatchesRepositoryAsync(bundle.Repository, reference.Repo, ct))
                        candidates.Add((reference.Sha, null, null));
                var notices = new List<string?>();
                var containsReturnedCommit = false;
                foreach (var (commit, branch, attachment) in candidates)
                {
                    if (commit is null)
                    {
                        notices.Add($"The parent returned attachment {attachment} (branch {branch}) that you haven't fetched; call fetch_attachment with attachmentId {attachment} and send branch mbus/{branch}.");
                        continue;
                    }
                    var notice = await GitBundles.ParentCommitNoticeAsync(bundle.Repository, bundle.Head, commit, branch, ct);
                    if (notice is null) containsReturnedCommit = true;
                    else notices.Add(attachment is null ? notice : $"Attachment {attachment}: {notice} Call fetch_attachment with attachmentId {attachment} if needed.");
                }
                // Multiple copies and retries can return alternatives; including any one is sufficient.
                bundles[i] = bundle with { Notice = string.Join(" ", new[] { bundle.Notice }
                    .Concat(containsReturnedCommit ? [] : notices).Where(n => !string.IsNullOrEmpty(n)).Distinct()) };
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Advisory: transport timeouts must not refuse a send, but caller cancellation still cancels it.
            await _o.Log.WriteLineAsync($"mbus mcp: couldn't check parent {parent}'s returned code: {Reason(e)}");
        }
    }

    private static async Task<bool> MatchesRepositoryAsync(string repository, string expected, CancellationToken ct)
    {
        try
        {
            var remotes = await Git.RunAsync(repository, ["config", "--get-regexp", @"remote\..*\.url"], ct);
            return remotes.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line =>
            {
                var url = line[(line.IndexOf(' ') + 1)..].Trim().TrimEnd('/');
                if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) url = url[..^4];
                var name = expected.TrimEnd('/');
                return url.Equals(name, StringComparison.OrdinalIgnoreCase)
                    || url.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase)
                    || url.EndsWith(":" + name, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (GitException) { return false; }
    }

    /// <summary>After a send: records what each target now has, and answers the thread's requests for earlier history.</summary>
    private void Remember(string threadId, IReadOnlyList<SentBundle> bundles)
    {
        foreach (var bundle in bundles.Where(b => b.RootCommit is not null))
            _sentCode.Record(_ws, bundle.To.ToString(), bundle.RootCommit!, bundle.Head);
        var incremental = bundles.Where(b => b.Incremental).ToList();
        if (incremental.Count == 0 || _closing.IsCancellationRequested) return;
        var prepare = _o.PrepareGit;
        _answering.GetOrAdd(threadId, id => Task.Run(async () =>
        {
            using var git = Git.PrepareWith(prepare);
            try
            {
                await AnswerHistoryRequestsAsync(id, incremental, _closing.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e)
            {
                await _o.Log.WriteLineAsync($"mbus mcp: stopped listening for thread {id}'s requests for code: {Reason(e)}");
            }
            finally
            {
                _answering.TryRemove(id, out _);
            }
        }));
    }

    /// <summary>Listens on the thread until it finishes (or its code is gone, after the attachment lifetime) and answers each request once.</summary>
    private async Task AnswerHistoryRequestsAsync(string threadId, IReadOnlyList<SentBundle> bundles, CancellationToken ct)
    {
        var until = _o.Time.GetUtcNow().AddSeconds(MessageLimits.AttachmentLifetimeSeconds);
        var answered = new HashSet<string>(StringComparer.Ordinal);
        string? after = null;
        var failures = 0;
        while (_o.Time.GetUtcNow() < until)
        {
            WatchResult result;
            try
            {
                result = await ForwardAsync<WatchInput, WatchResult>(McpToolNames.Watch, new WatchInput
                {
                    ThreadId = threadId, After = after, TimeoutSeconds = (int)Math.Ceiling(_o.PollChunk.TotalSeconds), Limit = 500, Compact = false,
                }, ct);
                failures = 0;
            }
            catch (Exception e) when (e is not OperationCanceledException && e is not McpToolException { Error.Code: McpErrorCodes.NotFound or McpErrorCodes.Forbidden })
            {
                // The service is restarting or out of reach: try again a little later, and give up after a while.
                if (++failures >= 10) throw;
                if (e is HttpRequestException or ModelContextProtocol.McpException) await ResetServiceAsync();
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * failures)), _o.Time, ct);
                continue;
            }
            after = result.Cursor ?? after;
            foreach (var change in result.Events)
            {
                if (change is ThreadStatusChanged { Status: ThreadStatus.Settled or ThreadStatus.Replied or ThreadStatus.Failed or ThreadStatus.Cancelled or ThreadStatus.DeadLettered })
                    return;
                if (change is ThreadProgressed { Progress.Request: { Kind: ProgressRequestKinds.GitHistory, ReplyTo: InboxTarget inbox } request }
                    && answered.Add(inbox.InboxId))
                {
                    try
                    {
                        await SendHistoryAsync(threadId, request, inbox, bundles, ct);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        await _o.Log.WriteLineAsync($"mbus mcp: couldn't send the earlier history thread {threadId}'s runner asked for: {Reason(e)}");
                    }
                }
            }
        }
    }

    /// <summary>The branch's whole history, sealed for the key the task went with, to the inbox the run opened for it.</summary>
    private async Task SendHistoryAsync(string threadId, ProgressRequest request, InboxTarget inbox, IReadOnlyList<SentBundle> bundles, CancellationToken ct)
    {
        var bundle = request.AttachmentId is { } id ? bundles.FirstOrDefault(b => b.AttachmentId == id) : bundles[0];
        if (bundle is null) return;
        var (attachment, sent) = await _attachments.SendGitBundleAsync(bundle.Repository, inbox, bundle.Key, bundle.SourceRef, null, threadId, ct);
        var body = new MessageBody
        {
            Summary = $"The whole history of {bundle.Branch}, which this task's runner asked for because its copy of the repository didn't have it. Sent by mbus, not the model.",
        };
        await ForwardAsync<SendInput, SendResult>(McpToolNames.Send, new SendInput
        {
            To = inbox, Type = ProgressRequestKinds.GitHistoryMessageType, ThreadId = threadId, Title = $"Earlier history of {bundle.Branch}",
            SealedBody = Seal(body, bundle.Key, "body"), Attachments = [attachment], ExpiresInSeconds = 3600,
        }, ct);
        await _o.Log.WriteLineAsync($"mbus mcp: sent the whole history of {bundle.Branch} (up to {sent.HeadCommit[..12]}) to thread {threadId}'s runner, which asked for it.");
    }

    /// <summary>Stops listening on every thread.</summary>
    private async Task StopAnsweringAsync()
    {
        await _closing.CancelAsync();
        try
        {
            await Task.WhenAll(_answering.Values).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException)
        {
        }
        _closing.Dispose();
    }
}
