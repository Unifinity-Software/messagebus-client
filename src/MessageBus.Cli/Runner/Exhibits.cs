using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;
using MessageBus.Mcp;

namespace MessageBus.Cli.Runner;

/// <summary>Uploads a workflow step's exhibits and keeps the references for its settlement.</summary>
/// <param name="http">The service client authenticated with the runner's machine credentials.</param>
/// <param name="workspaceId">The workspace containing the job.</param>
/// <param name="step">Trusted context from the delivered task.</param>
/// <param name="workingDirectory">The run's working folder; exhibit paths must stay inside it.</param>
/// <param name="workspaceKey">Resolves the current, trusted workspace encryption key when needed.</param>
/// <param name="tempDirectory">The runner's scratch folder for sealed streams.</param>
public sealed class Exhibits(HttpClient http, string workspaceId, StepContext step, string workingDirectory,
    Func<CancellationToken, Task<PublicKeyInfo>> workspaceKey, string tempDirectory)
{
    private readonly List<Ref> _references = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal)
        { "document", "diagram", "plan", "screenshot", "video", "trace", "file" };

    /// <summary>The successfully uploaded exhibits to include in the step's settle refs.</summary>
    public IReadOnlyList<Ref> References { get { lock (_references) return _references.ToArray(); } }

    /// <summary>Uploads one file immediately, returning its stored id and display metadata.</summary>
    public async Task<Ref> UploadAsync(string path, string? caption = null, string? kind = null, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path, workingDirectory);
        if (!RunToolScope.IsInside(workingDirectory, full))
            throw McpToolException.Invalid("path", "Exhibits must be inside the run's working folder, without symbolic links or junctions.");
        if (!File.Exists(full)) throw McpToolException.Invalid("path", "The exhibit file does not exist.");
        var name = Path.GetFileName(full);
        var mediaType = Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".mp4" => "video/mp4", ".webm" => "video/webm", _ => MediaTypes.ForFileName(full),
        };
        kind ??= mediaType switch
        {
            "image/svg+xml" => "diagram", "application/zip" => "trace",
            _ when mediaType.StartsWith("image/", StringComparison.Ordinal) => "screenshot",
            _ when mediaType.StartsWith("video/", StringComparison.Ordinal) => "video",
            _ when mediaType.StartsWith("text/", StringComparison.Ordinal) || mediaType == "application/pdf" => "document",
            _ => "file",
        };
        if (!Kinds.Contains(kind)) throw McpToolException.Invalid("kind", "Use document, diagram, plan, screenshot, video, trace or file.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            Scan(name, "name");
            Scan(caption, "caption");
            await ScanFileAsync(file, mediaType, ct).ConfigureAwait(false);
            if (!await TargetCapabilities.SupportsWorkflowsAsync(http, ct).ConfigureAwait(false))
                throw McpToolException.Unavailable("The service does not support workflow exhibits.");
            var locked = step.KeepExhibitsLocked == true;
            // Sealing uses scratch storage, never an in-memory copy of a potentially large recording.
            await using var sealedFile = locked ? new FileStream(Path.Combine(tempDirectory, "exhibit-" + Guid.NewGuid().ToString("N")),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.DeleteOnClose) : null;
            HttpContent content;
            if (sealedFile is not null)
            {
                var sealedResult = await MessageCrypto.SealAsync(file, sealedFile, await workspaceKey(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
                sealedFile.Position = 0;
                var prefix = Encoding.UTF8.GetBytes("mbus-sealed-exhibit/1\n" + MessageBusJson.Serialize(sealedResult.Encryption) + "\n");
                content = new SealedContent(prefix, sealedFile);
            }
            else content = new StreamContent(file);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var metadata = new JsonObject
            {
                ["round"] = step.Round, ["kind"] = kind, ["name"] = name, ["caption"] = caption,
                ["mediaType"] = mediaType, ["size"] = file.Length, ["locked"] = locked,
            };
            using var multipart = new MultipartFormDataContent();
            multipart.Add(new StringContent(metadata.ToJsonString(), Encoding.UTF8, "application/json"), "exhibit");
            multipart.Add(content, "content", name);
            var route = $"api/w/{Uri.EscapeDataString(workspaceId)}/jobs/{Uri.EscapeDataString(step.JobId)}/steps/{Uri.EscapeDataString(step.StepId)}/exhibits";
            using var response = await http.PostAsync(route, multipart, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw Refusal(response.StatusCode, body);
            using var stored = JsonDocument.Parse(body);
            var id = stored.RootElement.GetProperty("id").GetString();
            if (string.IsNullOrWhiteSpace(id)) throw new JsonException("The service returned no exhibit id.");
            var reference = new Ref { Kind = RefKinds.Exhibit, Id = id,
                Exhibit = new ExhibitRef { Name = name, MediaType = mediaType, Size = file.Length, Caption = caption } };
            lock (_references) _references.Add(reference);
            return reference;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Uploads every file left in a command's exhibits folder, using each file name as its caption.</summary>
    public async Task<IReadOnlyList<Ref>> UploadFolderAsync(string folder, CancellationToken ct = default)
    {
        var full = Path.GetFullPath(folder, workingDirectory);
        if (!RunToolScope.IsInside(workingDirectory, full))
            throw McpToolException.Invalid("folder", "The exhibits folder must be inside the run's working folder.");
        if (!Directory.Exists(full)) return [];
        var uploaded = new List<Ref>();
        foreach (var file in Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Order(StringComparer.Ordinal))
            uploaded.Add(await UploadAsync(file, Path.GetFileName(file), ct: ct).ConfigureAwait(false));
        return uploaded;
    }

    private static McpToolException Refusal(HttpStatusCode status, string body)
    {
        string? code = null;
        var message = body;
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (json.RootElement.TryGetProperty("message", out var value) && value.ValueKind == JsonValueKind.String) message = value.GetString()!;
                if (json.RootElement.TryGetProperty("code", out value) && value.ValueKind == JsonValueKind.String) code = value.GetString();
            }
        }
        catch (JsonException) { }
        return new McpToolException(new McpError { Code = code ?? status switch
        {
            HttpStatusCode.BadRequest => McpErrorCodes.InvalidInput, HttpStatusCode.Forbidden => McpErrorCodes.Forbidden,
            HttpStatusCode.NotFound => McpErrorCodes.NotFound, HttpStatusCode.RequestEntityTooLarge => McpErrorCodes.TooLarge,
            _ => McpErrorCodes.Unavailable,
        }, Message = message, Retryable = (int)status >= 500 });
    }

    private static void Scan(string? text, string field)
    {
        var findings = SecretScanner.Scan(text);
        if (findings.Count > 0) throw McpToolException.Invalid(field,
            $"The exhibit {field} looks like it holds a secret ({string.Join(", ", findings.Select(f => f.Kind).Distinct())}). Take it out and send again; never pass credentials in exhibits.");
    }

    private static async Task ScanFileAsync(FileStream file, string mediaType, CancellationToken ct)
    {
        var sample = new byte[4096];
        var count = await file.ReadAsync(sample, ct).ConfigureAwait(false);
        file.Position = 0;
        var text = mediaType.StartsWith("text/", StringComparison.Ordinal) || mediaType is "application/json" or "application/xml" or "image/svg+xml"
            || (count >= 2 && ((sample[0] == 0xff && sample[1] == 0xfe) || (sample[0] == 0xfe && sample[1] == 0xff)))
            || !sample.AsSpan(0, count).ContainsAnyInRange((byte)0, (byte)8)
                && !sample.AsSpan(0, count).ContainsAnyInRange((byte)14, (byte)31);
        if (text)
        {
            using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            Scan(await reader.ReadToEndAsync(ct).ConfigureAwait(false), "content");
        }
        file.Position = 0;
    }

    private sealed class SealedContent(byte[] prefix, Stream cipher) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = prefix.Length + cipher.Length; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken ct)
        {
            await stream.WriteAsync(prefix, ct).ConfigureAwait(false);
            await cipher.CopyToAsync(stream, ct).ConfigureAwait(false);
        }
    }
}
