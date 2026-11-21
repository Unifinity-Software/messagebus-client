using System.Text.RegularExpressions;
using MessageBus.Cli.Runner.Agents;

namespace MessageBus.Cli.Agents;

/// <summary>Credentials and provider diagnostics for the runner's non-interactive Codex invocation.</summary>
internal static partial class CodexAuthentication
{
    public const string CodexKey = "CODEX_API_KEY";
    public const string OpenAiKey = "OPENAI_API_KEY";
    private const string CodexKeyFile = CodexKey + "_FILE";
    private const string OpenAiKeyFile = OpenAiKey + "_FILE";
    public const string ShellKeyOverride = "shell_environment_policy.set.CODEX_API_KEY=\"\"";
    public const string VerifyAccess = "Model access is unverified. Verify it with mbus doctor --smoke --agent codex or a small Codex task.";

    private const string Missing = "OpenAI rejected Codex because no API credential reached the provider. Set CODEX_API_KEY where the runner starts or run codex login, then restart the runner and retry a small Codex task.";
    private const string Invalid = "OpenAI rejected the Codex API credential. Check the runner's key or renew its Codex sign-in, then restart the runner and retry a small Codex task.";
    private const string Ineligible = "OpenAI refused Codex access under the organization's monthly API Credits benefit for ChatGPT subscriptions. Ask the organization owner for a Codex-eligible sign-in or API project, then retry a small Codex task.";

    public static bool IsKeyVariable(string name) =>
        name.Equals(CodexKey, StringComparison.OrdinalIgnoreCase) || name.Equals(OpenAiKey, StringComparison.OrdinalIgnoreCase)
        || name.Equals(CodexKeyFile, StringComparison.OrdinalIgnoreCase) || name.Equals(OpenAiKeyFile, StringComparison.OrdinalIgnoreCase);

    public static string? ReadKey(Func<string, string?> get) => NonBlank(get(CodexKey)) ?? NonBlank(get(OpenAiKey));

    public static void RemoveKeys(IDictionary<string, string?> environment)
    {
        environment.Remove(CodexKey);
        environment.Remove(OpenAiKey);
        environment.Remove(CodexKeyFile);
        environment.Remove(OpenAiKeyFile);
    }

    public static Func<string, string> RedactOutput(string? key) => line =>
        ApiKey().Replace(key is null ? line : line.Replace(key, "[redacted]", StringComparison.Ordinal), "[redacted]");

    [GeneratedRegex(@"\bsk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{4,}\**[A-Za-z0-9_-]*")]
    private static partial Regex ApiKey();

    public static string? Refusal(AgentOutcome? outcome, IEnumerable<string> tail) =>
        outcome?.Usage is { TokensIn: > 0 } or { TokensOut: > 0 }
            ? null
            : Refusal(outcome?.ErrorText) ?? tail.Select(Refusal).FirstOrDefault(reason => reason is not null);

    public static string? Refusal(string? error)
    {
        if (error is null) return null;
        if (error.Contains("monthly API Credits", StringComparison.OrdinalIgnoreCase)
            && (error.Contains("Codex", StringComparison.OrdinalIgnoreCase) || error.Contains("ChatGPT subscriptions", StringComparison.OrdinalIgnoreCase)))
            return Ineligible;
        if (error.Contains("Missing bearer or basic authentication", StringComparison.OrdinalIgnoreCase) || error.Contains(Missing, StringComparison.Ordinal))
            return Missing;
        if (error.Contains("Invalid API key", StringComparison.OrdinalIgnoreCase) || error.Contains("Incorrect API key", StringComparison.OrdinalIgnoreCase)
            || error.Contains(Invalid, StringComparison.Ordinal))
            return Invalid;
        return null;
    }

    private static string? NonBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
