using System.Text;

namespace MessageBus.Cli.Runner.Service;

/// <summary>
/// The runner's log file when it runs as a service (<c>mbus runner --log-file</c>, the original issue): appended to, and when it
/// passes <see cref="MaxBytes"/> it becomes <c>runner.log.1</c> (the one before that <c>runner.log.2</c>, and so on,
/// keeping <see cref="Keep"/>) and a new one starts, so the log never grows without end. Others can read it while the
/// runner writes. Every line is written through at once, so nothing is lost if the runner is stopped.
/// </summary>
public sealed class RunnerLogFile : TextWriter
{
    /// <summary>Size at which the log is rotated.</summary>
    public const long DefaultMaxBytes = 10 * 1024 * 1024;

    /// <summary>Rotated logs kept by default.</summary>
    public const int DefaultKeep = 3;

    private readonly object _gate = new();
    private FileStream _file;

    /// <param name="path">The log (its folder is created).</param>
    /// <param name="maxBytes">Size at which it is rotated.</param>
    /// <param name="keep">Rotated logs kept (at least one).</param>
    public RunnerLogFile(string path, long maxBytes = DefaultMaxBytes, int keep = DefaultKeep)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(keep, 1);
        Path = System.IO.Path.GetFullPath(path);
        MaxBytes = maxBytes;
        Keep = keep;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _file = Open();
    }

    /// <summary>The log.</summary>
    public string Path { get; }

    /// <summary>Size at which it is rotated.</summary>
    public long MaxBytes { get; }

    /// <summary>Rotated logs kept.</summary>
    public int Keep { get; }

    /// <inheritdoc />
    public override Encoding Encoding { get; } = new UTF8Encoding(false);

    /// <inheritdoc />
    public override void Write(char value) => Write(new ReadOnlySpan<char>(in value));

    /// <inheritdoc />
    public override void Write(string? value) => Write(value.AsSpan());

    /// <inheritdoc />
    public override void Write(char[] buffer, int index, int count) => Write(buffer.AsSpan(index, count));

    /// <inheritdoc />
    public override void Write(ReadOnlySpan<char> buffer)
    {
        if (buffer.IsEmpty)
        {
            return;
        }
        var bytes = new byte[Encoding.GetByteCount(buffer)];
        Encoding.GetBytes(buffer, bytes);
        lock (_gate)
        {
            if (_file.Length > 0 && _file.Length + bytes.Length > MaxBytes)
            {
                Rotate();
            }
            _file.Write(bytes);
            _file.Flush();
        }
    }

    /// <inheritdoc />
    public override void WriteLine(string? value) => Write((value ?? "") + NewLine);

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

    /// <summary>runner.log → runner.log.1 → … → runner.log.<see cref="Keep"/> (dropped). If a file can't be moved (open elsewhere without sharing), the log carries on where it is.</summary>
    private void Rotate()
    {
        _file.Dispose();
        try
        {
            File.Delete($"{Path}.{Keep}");
            for (var i = Keep - 1; i >= 1; i--)
            {
                if (File.Exists($"{Path}.{i}"))
                {
                    File.Move($"{Path}.{i}", $"{Path}.{i + 1}");
                }
            }
            File.Move(Path, $"{Path}.1", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        _file = Open();
    }

    private FileStream Open() => new(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _file.Dispose();
            }
        }
        base.Dispose(disposing);
    }
}
