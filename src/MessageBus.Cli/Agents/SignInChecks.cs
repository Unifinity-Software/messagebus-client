using System.Text.Json;
using System.Text.Json.Nodes;

namespace MessageBus.Cli.Agents;

/// <summary>Whether an agent can use its sign-in from this session.</summary>
/// <param name="SignedIn">It can.</param>
/// <param name="NotReady">Why it can't and the fix (null when it can).</param>
/// <param name="Warning">A caution when it can but may still fail (null otherwise).</param>
internal sealed record SignInCheck(bool SignedIn, string? NotReady = null, string? Warning = null);

/// <summary>
/// The variables an agent sees when the runner starts it: this session's, with the agent sign-ins stored for the runner
/// service (the original issue, <see cref="Runner.Service.AgentEnvironmentStore"/>) filling the ones it doesn't set.
/// </summary>
/// <param name="Session">This process's variables.</param>
/// <param name="Stored">The stored agent sign-ins.</param>
internal sealed record SignInEnvironment(Func<string, string?> Session, IReadOnlyDictionary<string, string> Stored)
{
    /// <summary>A variable as the agent sees it (null when unset or blank).</summary>
    public string? Get(string name) => Session(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value : Stored.GetValueOrDefault(name);

    /// <summary>The stored variables this session doesn't set: what a status command needs added to see what a run sees.</summary>
    public IReadOnlyDictionary<string, string> Added =>
        Stored.Where(v => string.IsNullOrWhiteSpace(Session(v.Key))).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);

    /// <summary>
    /// Started by a service manager rather than from a terminal: by <c>mbus runner install</c>'s launchd agent (which
    /// sets <see cref="Runner.Service.RunnerServiceDefinitions.ServiceVariable"/>) or a systemd unit (which sets
    /// <c>INVOCATION_ID</c>).
    /// </summary>
    public bool InRunnerService => !string.IsNullOrEmpty(Session(Runner.Service.RunnerServiceDefinitions.ServiceVariable)) || !string.IsNullOrEmpty(Session("INVOCATION_ID"));
}

/// <summary>
/// Asks each agent CLI whether it can use its sign-in, with the status command it offers
/// (<c>claude auth status</c>, <c>codex login status</c>): no model call, and it sees what a run from
/// this session would see. That differs from the settings files on macOS over SSH, where Claude Code's
/// token is in the login keychain the session can't open (the original issue). Null when the CLI gives no answer mbus
/// understands (an older version); the scan then falls back to the agent's saved files.
/// </summary>
internal static class SignInChecks
{
    /// <summary>The variable <c>claude setup-token</c>'s token goes in.</summary>
    public const string ClaudeTokenVariable = "CLAUDE_CODE_OAUTH_TOKEN";

    /// <summary><c>claude auth status</c>: JSON with <c>loggedIn</c> and <c>authMethod</c>; exit 1 when signed out.</summary>
    public static async Task<SignInCheck?> ClaudeAsync(string executable, SignInEnvironment env, CancellationToken cancellationToken)
    {
        if (await CommandRunner.RunAsync(executable, ["auth", "status"], cancellationToken, environment: env.Added).ConfigureAwait(false) is not { } run
            || ParseObject(run.Output) is not { } status
            || status["loggedIn"] is not JsonValue loggedInValue
            || !loggedInValue.TryGetValue<bool>(out var loggedIn))
        {
            return null;
        }
        if (!loggedIn)
        {
            return new SignInCheck(false, ClaudeNotSignedIn(env, OperatingSystem.IsMacOS()));
        }
        var method = (status["authMethod"] as JsonValue)?.TryGetValue<string>(out var m) == true ? m : null;
        return new SignInCheck(true, Warning: method == "claude.ai" ? SshKeychainWarning(env.Get) : null);
    }

    /// <summary>
    /// Why Claude Code can't sign in, and the fix that works where the runner runs: a runner service (the original issue) gets a token
    /// only from <c>mbus runner install</c>, an SSH session on a Mac can't open the login keychain, and elsewhere it
    /// simply isn't signed in.
    /// </summary>
    /// <param name="env">What the agent sees.</param>
    /// <param name="macOs">This is a Mac (where Claude Code keeps its sign-in in the login keychain).</param>
    public static string ClaudeNotSignedIn(SignInEnvironment env, bool macOs)
    {
        var hasToken = env.Get(ClaudeTokenVariable) is not null || env.Get("ANTHROPIC_API_KEY") is not null;
        // On Linux there is no mbus runner install: the variables are stored with mbus runner env --save.
        var store = macOs || OperatingSystem.IsWindows() ? "mbus runner install" : "mbus runner env --save";
        if (env.InRunnerService)
        {
            return hasToken
                ? $"The runner service's Claude Code token doesn't work (it may have expired). Fix: run claude setup-token, then {store} again from a shell where {ClaudeTokenVariable} is set to the new token."
                : $"The runner service has no Claude Code token. Fix: run claude setup-token, then {store} again from a shell where {ClaudeTokenVariable} is set.";
        }
        if (macOs && InSshSession(env.Session) && !hasToken)
        {
            return "Claude Code can't use its sign-in in this SSH session: macOS keeps it in the login keychain, which an SSH session can't open. "
                + $"Fix: run claude setup-token on the Mac and set {ClaudeTokenVariable} to that token in the same session or script that starts mbus runner "
                + "(mbus runner install stores it for the runner service), or start the runner from Terminal on the Mac.";
        }
        return hasToken
            ? $"Claude Code doesn't accept its token (it may have expired). Fix: run claude setup-token and set {ClaudeTokenVariable} to the new token where mbus runner starts; then mbus connect --rescan."
            : $"Claude Code isn't signed in. Fix: run claude and sign in with /login, or run claude setup-token and set {ClaudeTokenVariable} to that token where mbus runner starts; then mbus connect --rescan.";
    }

    /// <summary><c>codex login status</c>: "Logged in using …" and exit 0 when signed in; "Not logged in" and exit 1 when not.</summary>
    public static async Task<SignInCheck?> CodexAsync(string executable, SignInEnvironment env, CancellationToken cancellationToken)
    {
        if (await CommandRunner.RunAsync(executable, ["login", "status"], cancellationToken, environment: env.Added).ConfigureAwait(false) is not { } run)
        {
            return null;
        }
        var said = run.AllOutput;
        if (!said.Contains("Not logged in", StringComparison.OrdinalIgnoreCase))
        {
            // "Logged in using ChatGPT" / "… using an API key"; anything else is an older Codex without the subcommand.
            return run.ExitCode == 0 && said.Contains("Logged in", StringComparison.OrdinalIgnoreCase) ? new SignInCheck(true) : null;
        }
        // codex exec also takes an API key from the environment, which the status doesn't count.
        if (env.Get("CODEX_API_KEY") is not null || env.Get("OPENAI_API_KEY") is not null)
        {
            return new SignInCheck(true);
        }
        return new SignInCheck(false, "Codex isn't signed in. Fix: run codex login on this machine (or set CODEX_API_KEY where mbus runner starts), then mbus connect --rescan.");
    }

    /// <summary>
    /// A caution for a runner started over SSH on a Mac whose Claude Code signs in through the keychain
    /// (with no token in the environment): its runs may say "Not logged in" although the scan passed.
    /// </summary>
    public static string? SshKeychainWarning(Func<string, string?> env) =>
        OperatingSystem.IsMacOS() && InSshSession(env) && string.IsNullOrWhiteSpace(env(ClaudeTokenVariable)) && string.IsNullOrWhiteSpace(env("ANTHROPIC_API_KEY"))
            ? $"This is an SSH session, and Claude Code keeps its sign-in in the macOS keychain. If its tasks fail with \"Not logged in\", run claude setup-token on the Mac and set {ClaudeTokenVariable} where mbus runner starts."
            : null;

    /// <summary>Started from an SSH login (OpenSSH sets these for the session's processes).</summary>
    public static bool InSshSession(Func<string, string?> env) =>
        !string.IsNullOrEmpty(env("SSH_CONNECTION")) || !string.IsNullOrEmpty(env("SSH_CLIENT")) || !string.IsNullOrEmpty(env("SSH_TTY"));

    private static JsonObject? ParseObject(string output)
    {
        var start = output.IndexOf('{');
        var end = output.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(output[start..(end + 1)]) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
