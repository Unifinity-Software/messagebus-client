namespace MessageBus.Cli;

/// <summary>
/// A problem the person can fix (wrong URL, not signed in, server refused). The command prints the
/// message, without a stack trace, and exits with <see cref="ExitCode"/>.
/// </summary>
public class CliException(string message, int exitCode = 1, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>Process exit code.</summary>
    public int ExitCode { get; } = exitCode;
}
