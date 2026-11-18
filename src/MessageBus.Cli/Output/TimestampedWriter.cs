using System.Globalization;
using System.Text;

namespace MessageBus.Cli.Output;

/// <summary>
/// The runner's status lines with the time each was written in front (the original issue): <c>14:02:31 Runner connection: connected</c>
/// on a terminal, the date, time and offset (<c>2026-11-14 14:02:31 +11:00</c>) in a log that outlives the day. The
/// exported copy of each line (<see cref="Telemetry.RunnerLogBridge"/>) drops the time again: log records carry their own.
/// </summary>
/// <param name="inner">Where the lines go.</param>
/// <param name="time">The clock (local time).</param>
/// <param name="timeOnly">Only the time of day (a terminal someone is watching); otherwise date, time and offset.</param>
public sealed class TimestampedWriter(TextWriter inner, TimeProvider time, bool timeOnly) : TextWriter
{
    private readonly Lock _gate = new();
    private bool _lineStart = true;

    /// <inheritdoc />
    public override Encoding Encoding => inner.Encoding;

    /// <summary>The prefix for a line written now, with its trailing space.</summary>
    public string Prefix()
    {
        var now = time.GetLocalNow();
        return now.ToString(timeOnly ? "HH:mm:ss " : "yyyy-MM-dd HH:mm:ss zzz ", CultureInfo.InvariantCulture);
    }

    /// <inheritdoc />
    public override void Write(char value) => Write(value.ToString());

    /// <inheritdoc />
    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

    /// <inheritdoc />
    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }
        lock (_gate)
        {
            var text = new StringBuilder(value.Length + 32);
            foreach (var c in value)
            {
                if (_lineStart && c is not ('\r' or '\n'))
                {
                    text.Append(Prefix());
                    _lineStart = false;
                }
                text.Append(c);
                if (c == '\n')
                {
                    _lineStart = true;
                }
            }
            inner.Write(text.ToString());
        }
    }

    /// <inheritdoc />
    public override void WriteLine(string? value) => Write((value ?? "") + NewLine);

    /// <inheritdoc />
    public override void WriteLine() => Write(NewLine);

    /// <inheritdoc />
    public override Task WriteAsync(char value)
    {
        Write(value);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task WriteAsync(string? value)
    {
        Write(value);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task WriteLineAsync(string? value)
    {
        WriteLine(value);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task WriteLineAsync()
    {
        WriteLine();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override void Flush() => inner.Flush();

    /// <inheritdoc />
    public override Task FlushAsync() => inner.FlushAsync();

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
}
