using System.Text.RegularExpressions;
using MessageBus.Contracts.Messaging;

namespace MessageBus.Crypto;

/// <summary>A likely credential found in text about to be sent.</summary>
/// <param name="Kind">What it looks like, for example <c>github-token</c>.</param>
/// <param name="Index">Where it starts (character offset in the scanned text).</param>
/// <param name="Length">How long it is.</param>
/// <param name="Preview">The first few characters and an ellipsis, safe to show (never the whole secret).</param>
public sealed record SecretFinding(string Kind, int Index, int Length, string Preview);

/// <summary>
/// Looks for obvious credentials before a body is encrypted and sent. The service can't scan bodies
/// (it can't read them), so the sender's machine does: the local MCP server and the runner refuse to send
/// when this finds something, unless the sender passes the override flag. It flags well-known token
/// shapes only, so it misses secrets with no recognisable format.
/// </summary>
public static partial class SecretScanner
{
    private static readonly (string Kind, Regex Pattern)[] Patterns =
    [
        ("private-key", PrivateKey()),
        ("aws-access-key-id", AwsAccessKey()),
        ("github-token", GitHubToken()),
        ("slack-token", SlackToken()),
        ("azure-storage-key", AzureStorageKey()),
        ("anthropic-api-key", AnthropicKey()),
        ("openai-api-key", OpenAiKey()),
        ("google-api-key", GoogleApiKey()),
        ("stripe-secret-key", StripeKey()),
        ("jwt", Jwt()),
        ("messagebus-api-key", MessageBusKey()),
    ];

    /// <summary>Likely credentials in <paramref name="text"/>, in order of position.</summary>
    /// <param name="text">The text to scan.</param>
    public static IReadOnlyList<SecretFinding> Scan(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var findings = new List<SecretFinding>();
        foreach (var (kind, pattern) in Patterns)
        {
            foreach (Match m in pattern.Matches(text))
                findings.Add(new SecretFinding(kind, m.Index, m.Length, Preview(m.Value)));
        }
        return [.. findings.OrderBy(f => f.Index)];
    }

    /// <summary>Likely credentials anywhere in a body: its summary, details and structured data.</summary>
    /// <param name="body">The body about to be sent.</param>
    public static IReadOnlyList<SecretFinding> Scan(MessageBody body) =>
    [
        .. Scan(body.Summary),
        .. Scan(body.Details),
        .. body.Friction.SelectMany(n => Scan(n.Note)),
        .. body.Data is { } data ? Scan(data.GetRawText()) : [],
    ];

    private static string Preview(string value) => value.Length <= 6 ? "…" : value[..4] + "…";

    [GeneratedRegex(@"-----BEGIN (?:[A-Z0-9]+ )*PRIVATE KEY-----")]
    private static partial Regex PrivateKey();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[0-9A-Z]{16}\b")]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(@"\b(?:gh[pousr]_[A-Za-z0-9]{36,255}|github_pat_[A-Za-z0-9_]{50,255})\b")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bxox[abposr]-[A-Za-z0-9-]{10,}")]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"AccountKey=[A-Za-z0-9+/]{86}==")]
    private static partial Regex AzureStorageKey();

    [GeneratedRegex(@"\bsk-ant-[A-Za-z0-9_-]{20,}")]
    private static partial Regex AnthropicKey();

    [GeneratedRegex(@"\bsk-(?!ant-)(?:proj-|svcacct-)?[A-Za-z0-9_-]{32,}")]
    private static partial Regex OpenAiKey();

    [GeneratedRegex(@"\bAIza[0-9A-Za-z_-]{35}\b")]
    private static partial Regex GoogleApiKey();

    [GeneratedRegex(@"\b(?:sk|rk)_live_[0-9A-Za-z]{24,}\b")]
    private static partial Regex StripeKey();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}")]
    private static partial Regex Jwt();

    [GeneratedRegex(@"\bmb[ke]_[A-Za-z0-9_-]{16,}")]
    private static partial Regex MessageBusKey();
}
