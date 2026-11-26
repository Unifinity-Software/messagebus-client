using System.Text;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner.Actions;

internal sealed class RunCommandAction(TimeProvider time) : IRunnerAction
{
    private const int MaxSummaryBytes = 4096;

    public string Kind => RunnerActionKinds.RunCommand;

    public async Task<SettleResult> RunAsync(RunnerActionContext context, CancellationToken cancellationToken)
    {
        var parameters = context.Delivery.RunnerAction?.RunCommand;
        if (parameters is null || string.IsNullOrWhiteSpace(parameters.Command))
            return Failed("The published workflow must specify a command.");

        var minutes = parameters.TimeLimitMinutes ?? 30;
        // CancellationTokenSource timers support at most 2^32 - 2 milliseconds.
        if (minutes is < 1 or > 71582)
            return Failed("The command time limit must be between 1 and 71582 minutes.");

        var folder = ResolveFolder(context.WorkDirectory, parameters.Folder);
        if (folder is null)
            return Failed("The command folder must be a directory inside the checkout without symbolic links or junctions.");

        var run = context.RunProcessAsync ?? throw new InvalidOperationException("The action has no sandboxed process runner.");
        var exhibitsFolder = context.Delivery.StepContext is null ? null
            : Directory.CreateDirectory(Path.Combine(context.WorkDirectory, ".mbus", "exhibits")).FullName;
        var environment = new Dictionary<string, string?>();
        if (exhibitsFolder is not null) environment["MBUS_EXHIBITS_DIR"] = exhibitsFolder;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(minutes), time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        SettleResult settled;
        try
        {
            var result = await run(AgentProcess.Shell(parameters.Command, folder, environment), stop.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            settled = timeout.IsCancellationRequested ? TimedOut(minutes) : new SettleResult
            {
                Status = SettleStatus.Succeeded,
                Outcome = result.ExitCode == 0 ? "passed" : "failed",
                ExitCode = result.ExitCode,
                Summary = OutputTail(result.Tail),
            };
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            settled = TimedOut(minutes);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (exhibitsFolder is not null && context.Exhibits is { } exhibits)
        {
            var refusals = new List<string>();
            var references = await exhibits.UploadFolderAsync(exhibitsFolder,
                (name, message) => refusals.Add($"Exhibit {name}: {message.ReplaceLineEndings(" ")}"), cancellationToken).ConfigureAwait(false);
            settled = settled with
            {
                Refs = [.. settled.Refs, .. references],
                Summary = string.Join('\n', new[] { settled.Summary }.Concat(refusals).Where(line => !string.IsNullOrEmpty(line))),
            };
        }
        return settled;
    }

    private static SettleResult Failed(string summary) => new()
    {
        Status = SettleStatus.Succeeded,
        Outcome = "failed",
        Summary = summary,
    };

    private static SettleResult TimedOut(int minutes) => Failed($"The command stopped after reaching its time limit of {minutes} min.");

    private static string? ResolveFolder(string root, string? folder)
    {
        if (string.IsNullOrEmpty(folder))
            return root;
        if (Path.IsPathRooted(folder))
            return null;

        var current = root;
        foreach (var part in folder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
                continue;
            if (part == ".." || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || (OperatingSystem.IsWindows() && (part.EndsWith('.') || part.EndsWith(' '))))
                return null;

            current = Path.Combine(current, part);
            var directory = new DirectoryInfo(current);
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                return null;
        }
        return current;
    }

    private static string OutputTail(IReadOnlyList<string> lines)
    {
        var output = string.Join('\n', lines);
        // At most 4096 UTF-16 code units can fit into 4096 UTF-8 bytes.
        var start = Math.Max(0, output.Length - MaxSummaryBytes);
        var bytes = Encoding.UTF8.GetByteCount(output.AsSpan(start));
        while (start < output.Length && (bytes > MaxSummaryBytes || char.IsLowSurrogate(output[start])))
        {
            var width = char.IsSurrogatePair(output, start) ? 2 : 1;
            bytes -= Encoding.UTF8.GetByteCount(output.AsSpan(start, width));
            start += width;
        }
        return output[start..];
    }
}
