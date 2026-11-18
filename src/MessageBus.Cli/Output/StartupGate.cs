using System.Text;

namespace MessageBus.Cli.Output;

/// <summary>
/// Holds what is written to it while the runner starts (the original issue), so its start-up summary can be printed first, in one
/// piece. <see cref="Open"/> then sends what was held on (or drops it, when the summary already said it) and lets
/// everything after it straight through.
/// </summary>
/// <param name="inner">Where the lines go.</param>
public sealed class StartupGate(TextWriter inner) : TextWriter
{
    private readonly Lock _gate = new();
    private StringBuilder? _held = new();

    /// <inheritdoc />
    public override Encoding Encoding => inner.Encoding;

    /// <summary>The complete lines held so far (empty once open).</summary>
    public IReadOnlyList<string> HeldLines
    {
        get
        {
            lock (_gate)
            {
                return _held is null ? [] : _held.ToString().Split(NewLine, StringSplitOptions.RemoveEmptyEntries);
            }
        }
    }

    /// <summary>Stops holding: what was held is written on (<paramref name="replay"/>) or dropped. Only the first call counts.</summary>
    public void Open(bool replay)
    {
        string? held;
        lock (_gate)
        {
            held = _held?.ToString();
            _held = null;
            // Written inside the lock, so a line written meanwhile can't overtake what was held.
            if (replay && !string.IsNullOrEmpty(held))
            {
                inner.Write(held);
                inner.Flush();
            }
        }
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
            if (_held is not null)
            {
                _held.Append(value);
                return;
            }
            inner.Write(value);
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
    public override void Flush()
    {
        lock (_gate)
        {
            if (_held is null)
            {
                inner.Flush();
            }
        }
    }

    /// <inheritdoc />
    public override Task FlushAsync()
    {
        Flush();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        Flush();
        return Task.CompletedTask;
    }
}
