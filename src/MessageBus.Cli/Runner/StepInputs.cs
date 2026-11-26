using MessageBus.Attachments.Client;
using System.Text;
using System.Text.Json;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner;

internal sealed record PreparedStepInputs(IReadOnlyList<string> Files, IReadOnlyDictionary<string, string> InputBranches);

internal sealed class UnreadableStepInputException(string step, Exception inner)
    : IOException($"Could not read the result from step '{step}': {inner.Message}", inner);

internal static class StepInputs
{
    internal static async Task<PreparedStepInputs> PrepareAsync(TaskDelivery delivery, RunWorkspace workspace, IBundleSource? source,
        Func<string, string?> locate, Func<ProcessLaunch, ProcessLaunch> wrap, CancellationToken ct)
    {
        var files = new List<string>();
        var branches = new Dictionary<string, string>(StringComparer.Ordinal);
        var prepared = new List<(StepInput Input, MessageBus.Contracts.Mcp.ReceivedMessage Result)>();
        foreach (var input in delivery.StepContext?.Inputs ?? [])
        {
            if (input.What == "work-item") continue;
            if (input.What is not ("result" or "branch"))
            {
                workspace.GitLog?.Invoke($"Ignoring step input '{input.What}' from '{input.FromStep}'.");
                continue;
            }
            try
            {
                SafeName(input.FromStep);
                var previous = prepared.FirstOrDefault(p => p.Input.FromStep == input.FromStep);
                if (previous.Input is not null && previous.Input.ThreadId != input.ThreadId)
                    throw new IOException("The same input step names different result threads.");
                var result = source is null ? null : await source.ReadStepResultAsync(delivery, input, ct).ConfigureAwait(false);
                if (result?.Body is null) throw new IOException("The result reply is unavailable or could not be decrypted.");

                // Import code before writing inputs: a checkout must never replace a prepared input folder.
                if (input.What == "branch")
                {
                    var bundle = result.Envelope.Attachments.FirstOrDefault(a => a.Format == AttachmentFormat.GitBundle)
                        ?? throw new IOException("The result has no code bundle.");
                    var path = await source!.FetchStepAttachmentAsync(bundle, Path.Combine(workspace.Root, "input-" + Guid.NewGuid().ToString("N") + ".bundle"), ct)
                        .ConfigureAwait(false) ?? throw new IOException("The code bundle could not be decrypted.");
                    var header = GitBundles.ReadHeader(path);
                    var branch = bundle.Branch ?? header.Refs.Keys.Single(r => r.StartsWith("refs/heads/", StringComparison.Ordinal))["refs/heads/".Length..];
                    if (!header.Refs.ContainsKey("refs/heads/" + branch)) throw new IOException("The bundle does not contain its named branch.");
                    var git = locate("git") ?? throw new IOException("git is not installed.");
                    if (await workspace.ImportStepBranchAsync(git, path, branch, wrap, ct).ConfigureAwait(false) is { } error)
                        throw new IOException(error);
                    branches[input.FromStep] = branch;
                }

                if (previous.Input is null) prepared.Add((input, result));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new UnreadableStepInputException(input.FromStep, ex);
            }
        }

        // All code checkouts finish before local inputs are written.
        foreach (var (input, result) in prepared)
        {
            try
            {
                var body = result.Body!;
                var relative = $".mbus/inputs/{input.FromStep}";
                var directory = Path.Combine(workspace.WorkTree, ".mbus", "inputs", input.FromStep);

                EnsureDirectory(workspace.WorkTree, ".mbus", "inputs", input.FromStep);
                await ExcludeAsync(workspace, ct).ConfigureAwait(false);
                await WriteAsync("summary.txt", body.Summary).ConfigureAwait(false);
                await WriteAsync("result.md", body.Summary + (body.Details is { Length: > 0 } details ? "\n\n" + details : "")).ConfigureAwait(false);
                if (branches.TryGetValue(input.FromStep, out var inputBranch)) await WriteAsync("branch.txt", inputBranch).ConfigureAwait(false);
                var exhibits = result.Envelope.Refs.Where(r => r.Kind == RefKinds.Exhibit).Select(r => r.Exhibit).OfType<ExhibitRef>().ToArray();
                if (exhibits.Length > 0) await WriteAsync("exhibits.json", SerializeExhibits(exhibits)).ConfigureAwait(false);
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "summary.txt", "result.md", "branch.txt", "exhibits.json" };
                foreach (var attachment in result.Envelope.Attachments.Where(a => a.Format != AttachmentFormat.GitBundle))
                {
                    SafeName(attachment.Name);
                    if (!names.Add(attachment.Name)) throw new IOException("A document name conflicts with another input file.");
                    var path = Path.Combine(directory, attachment.Name);
                    EnsureNewFile(path);
                    if (await source!.FetchStepAttachmentAsync(attachment, path, ct).ConfigureAwait(false) is null)
                        throw new IOException($"The document '{attachment.Name}' could not be decrypted.");
                    files.Add(relative + "/" + attachment.Name);
                }

                async Task WriteAsync(string name, string text)
                {
                    var path = Path.Combine(directory, name);
                    EnsureNewFile(path);
                    await File.WriteAllTextAsync(path, text, ct).ConfigureAwait(false);
                    files.Add(relative + "/" + name);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new UnreadableStepInputException(input.FromStep, ex);
            }
        }
        return new PreparedStepInputs(files, branches);
    }

    private static void SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')
            || name.Any(c => char.IsControl(c) || c is '/' or '\\' or ':' || Path.GetInvalidFileNameChars().Contains(c)))
            throw new IOException("An input step or document has an unsafe file name.");
    }

    private static string SerializeExhibits(ExhibitRef[] exhibits)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var exhibit in exhibits) JsonSerializer.Serialize(writer, exhibit, MessageBusJson.Options);
            writer.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void EnsureDirectory(string root, params string[] parts)
    {
        var current = root;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            var info = new DirectoryInfo(current);
            if (info.LinkTarget is not null || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
                throw new IOException("An input folder is a symbolic link.");
            Directory.CreateDirectory(current);
        }
    }

    private static void EnsureNewFile(string path)
    {
        if (File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null
            || new FileInfo(path + ".partial").LinkTarget is not null)
            throw new IOException("An input file already exists in the checkout.");
    }

    private static async Task ExcludeAsync(RunWorkspace workspace, CancellationToken ct)
    {
        // Run checkouts have their own .git directory. Empty, non-git runs need no exclude file.
        if (!Directory.Exists(Path.Combine(workspace.WorkTree, ".git"))) return;
        EnsureDirectory(workspace.WorkTree, ".git", "info");
        var path = Path.Combine(workspace.WorkTree, ".git", "info", "exclude");
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("The git exclude file is a symbolic link.");
        var text = File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : "";
        if (!text.Split('\n').Any(line => line.TrimEnd('\r') == "/.mbus/"))
            await File.AppendAllTextAsync(path, "\n/.mbus/\n", ct).ConfigureAwait(false);
    }
}
