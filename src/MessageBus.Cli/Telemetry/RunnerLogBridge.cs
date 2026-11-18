using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MessageBus.Cli.Telemetry;

/// <summary>
/// The runner's status lines as OpenTelemetry logs (the original issue). Every line still goes to stderr as before;
/// when the owner exports OTLP logs, each line is also sent as a log record (category
/// <see cref="Category"/>), after <see cref="Filter"/>: an agent's own output is cut off and anything
/// that looks like a credential is replaced, so a collector never receives what the agent printed or
/// a token that ended up in an error message.
/// </summary>
public sealed partial class RunnerLogBridge(TextWriter inner, ILogger logger) : TextWriter
{
    public const string Category = "MessageBus.Runner";

    /// <summary>The longest line exported.</summary>
    public const int MaxLength = 500;

    private readonly StringBuilder _line = new();
    private readonly Lock _gate = new();

    public override Encoding Encoding => inner.Encoding;

    public override void Write(char value)
    {
        inner.Write(value);
        lock (_gate)
        {
            if (value == '\n') Flush(_line);
            else if (value != '\r') _line.Append(value);
        }
    }

    public override void Write(string? value)
    {
        if (value is null) return;
        inner.Write(value);
        lock (_gate)
        {
            foreach (var c in value)
            {
                if (c == '\n') Flush(_line);
                else if (c != '\r') _line.Append(c);
            }
        }
    }

    public override void WriteLine(string? value)
    {
        inner.WriteLine(value);
        lock (_gate)
        {
            _line.Append(value);
            Flush(_line);
        }
    }

    public override Task WriteLineAsync(string? value)
    {
        WriteLine(value);
        return Task.CompletedTask;
    }

    public override Task WriteAsync(string? value)
    {
        Write(value);
        return Task.CompletedTask;
    }

    public override void Flush() => inner.Flush();

    private void Flush(StringBuilder line)
    {
        var text = line.ToString();
        line.Clear();
        if (Filter(text) is not { } safe) return;
        var level = safe.Contains("failed", StringComparison.OrdinalIgnoreCase) || safe.StartsWith("Couldn't", StringComparison.Ordinal)
            || safe.Contains("isn't ready", StringComparison.Ordinal) ? LogLevel.Warning : LogLevel.Information;
#pragma warning disable CA2254 // the line is the message: it is filtered text, not a template with arguments
        logger.Log(level, safe.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal));
#pragma warning restore CA2254
    }

    /// <summary>
    /// The line as it may leave the machine, or null for nothing worth sending. A delivery's outcome
    /// line keeps the outcome and drops the agent's error text after it; credentials (bearer and
    /// basic tokens, GitHub, OpenAI and Anthropic, AWS and mbus keys, JWTs, secrets in query strings,
    /// passwords in URLs) become <c>[redacted]</c>; the rest is cut to <see cref="MaxLength"/>.
    /// </summary>
    public static string? Filter(string line)
    {
        line = line.Trim();
        // the original issue: the time the runner put in front of the line (Output.TimestampedWriter); the record has its own.
        line = Timestamp().Replace(line, "", 1);
        if (line.Length == 0) return null;
        if (Outcome().Match(line) is { Success: true } outcome) line = $"{outcome.Groups[1].Value}: {outcome.Groups[2].Value}";
        line = Scheme().Replace(line, "$1 [redacted]");
        line = Token().Replace(line, "[redacted]");
        line = QuerySecret().Replace(line, "$1=[redacted]");
        line = UrlPassword().Replace(line, "://[redacted]@");
        return line.Length <= MaxLength ? line : line[..(MaxLength - 1)] + "…";
    }

    // "14:02:31 " on a terminal, "2026-11-14 14:02:31 +11:00 " in a log file.
    [GeneratedRegex(@"^(?:\d{4}-\d{2}-\d{2} )?\d{2}:\d{2}:\d{2}(?: [+-]\d{2}:\d{2})? ")]
    private static partial Regex Timestamp();

    // "dlv_…: failed (the agent's error text)"
    [GeneratedRegex(@"^(dlv_[A-Za-z0-9]+): (succeeded|failed|abandoned|cancelled)\b.*$")]
    private static partial Regex Outcome();

    [GeneratedRegex(@"(?i)\b(bearer|basic|token|geniekey|splunk)\s+[A-Za-z0-9._~+/=-]{8,}")]
    private static partial Regex Scheme();

    [GeneratedRegex(@"\b(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|sk-[A-Za-z0-9_-]{16,}|AKIA[0-9A-Z]{16}|mbus_[A-Za-z0-9_-]{16,}|eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,})")]
    private static partial Regex Token();

    [GeneratedRegex(@"(?i)\b(password|passwd|pwd|secret|token|access_token|api[_-]?key|key|sig|signature|code)=([^&\s""']+)")]
    private static partial Regex QuerySecret();

    [GeneratedRegex(@"://[^/\s:@]+:[^/\s@]+@")]
    private static partial Regex UrlPassword();
}
