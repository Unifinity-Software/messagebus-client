using MessageBus.Cli.Agents;
using MessageBus.Cli.Connect;
using MessageBus.Cli.Hosting;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner;

/// <summary>Asks the person at the machine a yes/no question in the runner's own terminal.</summary>
public interface IMachineConfirmer
{
    /// <summary>False when the runner has no terminal to ask in (a service, a container, input redirected).</summary>
    bool CanAsk { get; }

    /// <summary>The answer: true only for a typed yes; false for anything else or when <paramref name="timeout"/> passes.</summary>
    Task<bool> AskAsync(string question, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// A yes-or-no answer (the original issue): true for a typed yes, false for a typed no, null for anything else: no answer before
    /// <paramref name="timeout"/>, the question withdrawn (<paramref name="cancellationToken"/>), or no terminal.
    /// </summary>
    Task<bool?> AnswerAsync(string question, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>
/// Asks on the runner's terminal input. One question at a time. A line still being read when a question times out
/// answers the next question instead, so no typed line is lost to a reader nobody waits on.
/// </summary>
public sealed class TerminalConfirmer(TextReader input, TextWriter output, bool interactive, TimeProvider time) : IMachineConfirmer
{
    private readonly SemaphoreSlim _one = new(1, 1);
    private Task<string?>? _pending;

    public bool CanAsk => interactive;

    public async Task<bool> AskAsync(string question, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var (answered, line) = await ReadAsync($"{question} [y/N]", timeout, cancellationToken).ConfigureAwait(false);
        if (!answered)
        {
            if (interactive) await output.WriteLineAsync("No answer, so nothing was changed.").ConfigureAwait(false);
            return false;
        }
        return IsYes(line);
    }

    public async Task<bool?> AnswerAsync(string question, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            var (answered, line) = await ReadAsync($"{question} [y/n]", timeout, cancellationToken).ConfigureAwait(false);
            if (!answered || line is null) return null;
            if (IsYes(line)) return true;
            return line.Equals("n", StringComparison.OrdinalIgnoreCase) || line.Equals("no", StringComparison.OrdinalIgnoreCase) ? false : null;
        }
        catch (OperationCanceledException)
        {
            return null; // withdrawn while another question was open
        }
    }

    private static bool IsYes(string? line) =>
        line is not null && (line.Equals("y", StringComparison.OrdinalIgnoreCase) || line.Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>Asks and reads one line (trimmed); not answered when the time passes, the question is withdrawn or there is no terminal.</summary>
    private async Task<(bool Answered, string? Line)> ReadAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!interactive) return (false, null);
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await output.WriteLineAsync(prompt).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            _pending ??= Task.Run(input.ReadLine, CancellationToken.None);
            var finished = await Task.WhenAny(_pending, Task.Delay(timeout, time, cancellationToken)).ConfigureAwait(false);
            if (finished != _pending) return (false, null);
            var answer = (await _pending.ConfigureAwait(false))?.Trim();
            _pending = null;
            return (true, answer);
        }
        finally
        {
            _one.Release();
        }
    }
}

/// <summary>
/// The runner's side of "Connect" in the dashboard (the original issue): sets up one agent to use mbus exactly as <c>mbus connect</c>
/// does, but only after the person at the machine types yes in the runner's terminal, having been shown every file it
/// will change. The request is answered at once; the question waits for up to <see cref="AnswerTimeout"/>. Without a
/// terminal (the runner is a service or in a container) nothing is asked and the dashboard is told to run the command.
/// </summary>
public sealed class AgentConnectRequests(CliHost host, IMachineConfirmer confirmer, TextWriter log, Func<CancellationToken, Task> rescan)
{
    /// <summary>How long the question stays open on the machine.</summary>
    public static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The last question's work, for tests and for the runner to wait on when it stops.</summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    public async Task<ConnectAgentResult> RequestAsync(ConnectAgentRequest request, CancellationToken cancellationToken)
    {
        var name = AgentScanner.DisplayName(request.Agent);
        var command = $"mbus connect {CommandArgument(request.Agent)}";
        if (!AgentConnector.Supported.Contains(request.Agent))
            return new() { Status = ConnectAgentStatus.Unsupported, Message = $"mbus can't set up {name} yet." };
        var found = (await AgentScanner.ScanAsync(host, cancellationToken).ConfigureAwait(false)).FirstOrDefault(a => a.Kind == request.Agent);
        if (found is not { Found: true })
            return new() { Status = ConnectAgentStatus.NotInstalled, Message = $"{name} isn't installed on {host.MachineName}." };
        if (!confirmer.CanAsk)
            return new() { Status = ConnectAgentStatus.NeedsTerminal, Message = $"The runner on {host.MachineName} can't ask anyone. Run {command} there." };

        var changes = AgentConnector.Plan(host, request.Agent);
        Pending = AskAndApplyAsync(request, name, changes);
        return new() { Status = ConnectAgentStatus.Asked, Message = $"Answer the question in the runner's window on {host.MachineName}. Nothing changes until you do." };
    }

    private async Task AskAndApplyAsync(ConnectAgentRequest request, string name, IReadOnlyList<PlannedChange> changes)
    {
        try
        {
            var lines = string.Join(Environment.NewLine, changes.Select(c => $"  {c.What}: {c.Path}"));
            var who = request.RequestedBy is { Length: > 0 } person ? person : "Someone";
            var question = $"{who} asked from the messagebus.ai dashboard to set up {name} on this machine to use mbus. It changes:{Environment.NewLine}{lines}{Environment.NewLine}Make these changes?";
            if (!await confirmer.AskAsync(question, AnswerTimeout, CancellationToken.None).ConfigureAwait(false))
            {
                await log.WriteLineAsync($"{name} was not set up: nobody said yes on this machine.").ConfigureAwait(false);
                return;
            }
            foreach (var change in changes) change.Apply();
            await log.WriteLineAsync($"{name} now uses mbus. Start a new session in it to pick up the mbus tools.").ConfigureAwait(false);
            await rescan(CancellationToken.None).ConfigureAwait(false); // the dashboard shows it connected
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or TaskCanceledException or CliException)
        {
            await log.WriteLineAsync($"Couldn't set up {name}: {ex.Message}").ConfigureAwait(false);
        }
    }

    private static string CommandArgument(AgentKind kind) => kind switch
    {
        AgentKind.ClaudeCode => "claude",
        AgentKind.Codex => "codex",
        AgentKind.Copilot => "copilot",
        AgentKind.OpenCode => "opencode",
        _ => "",
    };
}
