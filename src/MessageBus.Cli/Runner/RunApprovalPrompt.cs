using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner;

/// <summary>
/// Approval before each run (the original issue), answered at the machine (the original issue): while a run waits for its owner's approval, a runner
/// in a terminal asks there, and a typed yes or no is recorded through the approvals endpoint as the person signed in
/// to mbus (<paramref name="answer"/>), never as the machine, so the audit log says who and the waiting run sees it on
/// its next ask. A runner with nobody to ask (a service, a log file, input redirected) or no one signed in to mbus
/// says how to answer instead. No answer changes nothing: the dashboard and <c>mbus approve</c> still work.
/// </summary>
/// <param name="confirmer">The runner's terminal.</param>
/// <param name="answer">Records the decision as the signed-in person and says what happened; null when nobody is signed in.</param>
/// <param name="log">The runner's output.</param>
public sealed class RunApprovalPrompt(IMachineConfirmer confirmer, Func<string, bool, CancellationToken, Task<string>>? answer, TextWriter log)
{
    /// <summary>The longest a question stays open: as long as the owner has to answer (the service's one hour).</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromHours(1);

    /// <summary>The last question's work, for tests and for the runner to wait on when it stops.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>A run has started waiting; <paramref name="stillWaiting"/> ends when it stops waiting (answered anywhere, or stopped).</summary>
    public void Waiting(TaskDelivery delivery, RunApprovalState state, CancellationToken stillWaiting)
    {
        var what = delivery.Envelope.Title is { Length: > 0 } title ? $"\"{title}\"" : $"A {delivery.Role ?? "new"} task";
        if (!confirmer.CanAsk || answer is null)
        {
            log.WriteLine($"{what} is waiting for your approval to run here. Answer it with mbus approvals (then mbus approve or mbus decline), or in the dashboard.");
            return;
        }
        var from = delivery.Envelope.Sender.DisplayName is { Length: > 0 } sender ? $" from {sender}" : "";
        var role = delivery.Role is { Length: > 0 } r ? $" ({r})" : "";
        Pending = AskAsync($"{what}{role}{from} is waiting for your approval. Run it on this machine?", state.ApprovalId, answer, stillWaiting);
    }

    private async Task AskAsync(string question, string approvalId, Func<string, bool, CancellationToken, Task<string>> record, CancellationToken stillWaiting)
    {
        try
        {
            if (await confirmer.AnswerAsync(question, AnswerTimeout, stillWaiting).ConfigureAwait(false) is not { } yes) return;
            await log.WriteLineAsync(await record(approvalId, yes, CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or CliException)
        {
            await log.WriteLineAsync($"Couldn't record your answer: {ex.Message}").ConfigureAwait(false);
        }
    }
}
