using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MessageBus.Contracts.Attachments;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;

namespace MessageBus.Attachments.Client;

/// <summary>What an upload is: who it's for, its name and how it's packaged.</summary>
/// <param name="Target">The message's target (its role's size limit applies).</param>
/// <param name="Name">File name the receiver sees.</param>
/// <param name="Format">How it's packaged.</param>
/// <param name="ThreadId">The thread it goes on, when known (a reply's files: lets the asker download them).</param>
/// <param name="ContentType">Media type of the plaintext.</param>
/// <param name="Branch">For a bundle or patch: its branch.</param>
/// <param name="BaseCommit">For a bundle or patch: the commit it starts after.</param>
public sealed record AttachmentUpload(
    MessageTarget Target,
    string Name,
    AttachmentFormat Format,
    string? ThreadId = null,
    string ContentType = "application/octet-stream",
    string? Branch = null,
    string? BaseCommit = null);

/// <summary>The service refused an attachment call, or a transfer failed.</summary>
public sealed class AttachmentTransferException(string message, HttpStatusCode? status = null, string? code = null) : Exception(message)
{
    /// <summary>The HTTP status, when the service answered.</summary>
    public HttpStatusCode? Status { get; } = status;

    /// <summary>The <see cref="McpErrorCodes"/> code, when the service gave one (<c>too-large</c> for an attachment over the role's limit).</summary>
    public string? Code { get; } = code;
}

/// <summary>
/// The machine side of code attachments (the original issue). Sealing happens here, so the service only ever sees
/// ciphertext: <see cref="UploadAsync"/> seals to a temporary file (the size and hash must be known before
/// asking for a link), asks for an upload link, and streams the file to it. <see cref="DownloadAsync"/>
/// streams a link's ciphertext to a temporary file, checks its size and SHA-256 against the
/// <see cref="AttachmentRef"/>, then opens it. Nothing is held in memory whole.
/// </summary>
/// <param name="api">
/// Calls the service as this machine (or API key): base address the server URL with a trailing slash,
/// requests signed (<c>McpHostContext.Http</c> in the CLI). Any <see cref="AttachmentTransfers.Bind"/>
/// authorization registered on this client is inherited by every actual upload and download.
/// </param>
/// <param name="links">
/// Fetches and uploads link URLs. Use <see cref="AttachmentTransfers.CreateHttpClient"/> for a separate
/// credential-free, cookie-free, no-redirect client without default headers or body-buffering handlers.
/// A legacy link may be a SAS URL straight to Blob storage; an isolated binding restricts its destination.
/// </param>
/// <param name="workspaceId">The workspace.</param>
/// <param name="tempDirectory">Where the git bundles git writes and reads go (a runner's task: its own temp folder, which its sandbox lets git use); null for the system's.</param>
public sealed class AttachmentClient(HttpClient api, HttpClient links, string workspaceId, string? tempDirectory = null)
{
    private const int CopyBuffer = 81920;

    // ---- sending ----

    /// <summary>Seals <paramref name="plaintext"/> to <paramref name="recipient"/>, uploads it, and returns the reference to put on the message.</summary>
    /// <param name="plaintext">The content, read to its end.</param>
    /// <param name="upload">Who it's for and what it is.</param>
    /// <param name="recipient">The key to seal to (<c>KeyDirectory.RecipientFor(target)</c>).</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="AttachmentTransferException">Refused (over the role's limit: <see cref="McpErrorCodes.TooLarge"/>) or the upload failed.</exception>
    public async Task<AttachmentRef> UploadAsync(Stream plaintext, AttachmentUpload upload, PublicKeyInfo recipient, CancellationToken ct = default)
    {
        await using var sealedFile = TempFile();
        var sealedResult = await MessageCrypto.SealAsync(plaintext, sealedFile, recipient, ct);
        var link = await PostAsync<UploadLinkRequest, UploadLink>(Route(ApiRoutes.AttachmentUploadLink), new UploadLinkRequest
        {
            Target = upload.Target,
            Name = upload.Name,
            Format = upload.Format,
            SizeBytes = sealedResult.CiphertextLength,
            Sha256 = sealedResult.Sha256,
            ThreadId = upload.ThreadId,
            Encryption = sealedResult.Encryption,
        }, ct);

        sealedFile.Position = 0;
        using (var request = new HttpRequestMessage(HttpMethod.Put, Resolve(link.Url)))
        {
            request.Content = new StreamContent(sealedFile, CopyBuffer);
            request.Content.Headers.ContentLength = sealedResult.CiphertextLength;
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");   // what a SAS link needs; the API ignores it
            using var response = await AttachmentTransfers.SendAsync(api, links, request, HttpCompletionOption.ResponseContentRead, ct);
            await EnsureAsync(response, ct);
        }

        return new AttachmentRef
        {
            Id = link.AttachmentId,
            Name = upload.Name,
            Format = upload.Format,
            SizeBytes = sealedResult.CiphertextLength,
            Sha256 = sealedResult.Sha256,
            ContentType = upload.ContentType,
            Branch = upload.Branch,
            BaseCommit = upload.BaseCommit,
            Encryption = sealedResult.Encryption,
        };
    }

    /// <summary>Bundles <paramref name="branch"/> after <paramref name="baseCommit"/> (whole history when null), seals and uploads it.</summary>
    /// <param name="repository">The repository or worktree.</param>
    /// <param name="target">The message's target.</param>
    /// <param name="recipient">The key to seal to.</param>
    /// <param name="branch">The branch; default the current one.</param>
    /// <param name="baseCommit">A commit the receiver has; null to send the whole history.</param>
    /// <param name="threadId">The thread, when known.</param>
    /// <param name="ct">Cancels.</param>
    public async Task<AttachmentRef> SendBundleAsync(string repository, MessageTarget target, PublicKeyInfo recipient, string? branch = null,
        string? baseCommit = null, string? threadId = null, CancellationToken ct = default) =>
        (await SendGitBundleAsync(repository, target, recipient, branch, baseCommit, threadId, ct)).Attachment;

    /// <summary><see cref="SendBundleAsync"/>, also returning what was bundled (its branch and head commit).</summary>
    public Task<(AttachmentRef Attachment, GitBundle Bundle)> SendGitBundleAsync(string repository, MessageTarget target, PublicKeyInfo recipient,
        string? branch = null, string? baseCommit = null, string? threadId = null, CancellationToken ct = default) =>
        SendGitBundleAsync(repository, target, recipient, branch, baseCommit, threadId, expectedHead: null, ct);

    /// <summary>Packages and uploads a bundle, checking its advertised branch against <paramref name="expectedHead"/> before upload when supplied.</summary>
    public async Task<(AttachmentRef Attachment, GitBundle Bundle)> SendGitBundleAsync(string repository, MessageTarget target, PublicKeyInfo recipient,
        string? branch, string? baseCommit, string? threadId, string? expectedHead, CancellationToken ct)
    {
        var dir = TempFolders.Create(tempDirectory, "mbus-bundle-");
        try
        {
            var bundle = await GitBundles.CreateAsync(repository, Path.Combine(dir.FullName, "branch.bundle"), branch, baseCommit, ct);
            if (expectedHead is not null && !string.Equals(expectedHead, bundle.HeadCommit, StringComparison.OrdinalIgnoreCase))
                throw new GitException($"Expected bundle head {expectedHead}, but {bundle.Branch} advertises {bundle.HeadCommit}.", input: "expectedHead");

            await using var file = new FileStream(bundle.Path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBuffer, useAsync: true);
            var attachment = await UploadAsync(file, new AttachmentUpload(target, FileName(bundle.Branch) + ".bundle", AttachmentFormat.GitBundle, threadId,
                GitBundles.BundleContentType, bundle.Branch, bundle.BaseCommit), recipient, ct);
            return (attachment, bundle);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ---- receiving ----

    /// <summary>Download links for the attachments of a delivery this machine holds (valid for the lease).</summary>
    /// <param name="threadId">The delivery's thread.</param>
    /// <param name="deliveryId">The delivery.</param>
    /// <param name="ct">Cancels.</param>
    public async Task<IReadOnlyList<DownloadLink>> DeliveryLinksAsync(string threadId, string deliveryId, CancellationToken ct = default)
    {
        var path = Route(ApiRoutes.AttachmentDownloadLinks).Replace("{deliveryId}", Uri.EscapeDataString(deliveryId), StringComparison.Ordinal)
                   + "?threadId=" + Uri.EscapeDataString(threadId);
        using var response = await api.PostAsync(path, null, ct);
        return await ReadAsync<IReadOnlyList<DownloadLink>>(response, ct);
    }

    /// <summary>A download link for one attachment this caller uploaded or whose thread it started (a reply's files).</summary>
    /// <param name="attachmentId">The attachment.</param>
    /// <param name="ct">Cancels.</param>
    public async Task<DownloadLink> DownloadLinkAsync(string attachmentId, CancellationToken ct = default)
    {
        var path = Route(ApiRoutes.AttachmentDownloadLink).Replace("{attachmentId}", Uri.EscapeDataString(attachmentId), StringComparison.Ordinal);
        using var response = await api.PostAsync(path, null, ct);
        return await ReadAsync<DownloadLink>(response, ct);
    }

    /// <summary>
    /// Downloads <paramref name="attachment"/> through <paramref name="link"/>, checks its size and SHA-256,
    /// and writes the plaintext to <paramref name="destination"/>. Writing stops at the first damaged chunk,
    /// so download to a temporary file (<see cref="DownloadToFileAsync"/> does) and use it only on success.
    /// </summary>
    /// <param name="attachment">The reference from the message.</param>
    /// <param name="link">Its download link.</param>
    /// <param name="destination">Where the plaintext goes.</param>
    /// <param name="keys">This machine's private keys (it needs the role key the file was sealed to).</param>
    /// <param name="ct">Cancels.</param>
    /// <exception cref="AttachmentTransferException">The download failed or doesn't match the reference.</exception>
    /// <exception cref="MissingKeyException">This machine doesn't hold the key it was sealed to.</exception>
    /// <exception cref="CryptographicException">It was changed on the way.</exception>
    public async Task DownloadAsync(AttachmentRef attachment, DownloadLink link, Stream destination, IKeyRing keys, CancellationToken ct = default)
    {
        await using var cipher = TempFile();
        using (var request = new HttpRequestMessage(HttpMethod.Get, Resolve(link.Url)))
        using (var response = await AttachmentTransfers.SendAsync(api, links, request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            await EnsureAsync(response, ct);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[CopyBuffer];
            long total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > attachment.SizeBytes) throw new AttachmentTransferException($"{attachment.Name} is larger than its message says. Not opened.");
                hash.AppendData(buffer, 0, read);
                await cipher.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            if (total != attachment.SizeBytes || Convert.ToHexStringLower(hash.GetHashAndReset()) != attachment.Sha256)
                throw new AttachmentTransferException($"{attachment.Name} doesn't match the size and SHA-256 on its message. Not opened.");
        }

        cipher.Position = 0;
        if (attachment.Encryption is { } encryption) await MessageCrypto.OpenAsync(cipher, destination, encryption, keys, ct);
        else await cipher.CopyToAsync(destination, CopyBuffer, ct);
    }

    /// <summary><see cref="DownloadAsync"/> into <paramref name="path"/>, which appears only when everything checked out.</summary>
    /// <param name="attachment">The reference from the message.</param>
    /// <param name="link">Its download link.</param>
    /// <param name="path">Where to save it.</param>
    /// <param name="keys">This machine's private keys.</param>
    /// <param name="ct">Cancels.</param>
    public async Task DownloadToFileAsync(AttachmentRef attachment, DownloadLink link, string path, IKeyRing keys, CancellationToken ct = default)
    {
        var partial = path + ".partial";
        try
        {
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, CopyBuffer, useAsync: true))
                await DownloadAsync(attachment, link, file, keys, ct);
            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    /// <summary>
    /// Downloads and opens a git bundle attachment, verifies it against <paramref name="repository"/> (made
    /// with <c>git init</c> when it doesn't exist) and fetches its branch into <paramref name="into"/>
    /// (default <c>refs/remotes/mbus/{branch}</c>). Returns the commit id fetched.
    /// </summary>
    /// <param name="attachment">A <see cref="AttachmentFormat.GitBundle"/> reference.</param>
    /// <param name="link">Its download link.</param>
    /// <param name="keys">This machine's private keys.</param>
    /// <param name="repository">The receiving repository (the task's worktree).</param>
    /// <param name="into">The ref to fetch into.</param>
    /// <param name="ct">Cancels.</param>
    public async Task<string> ReceiveBundleAsync(AttachmentRef attachment, DownloadLink link, IKeyRing keys, string repository, string? into = null,
        CancellationToken ct = default)
    {
        if (attachment.Format != AttachmentFormat.GitBundle || attachment.Branch is null)
            throw new ArgumentException("That attachment isn't a git bundle.", nameof(attachment));
        var dir = TempFolders.Create(tempDirectory, "mbus-bundle-");
        try
        {
            var path = Path.Combine(dir.FullName, "branch.bundle");
            await DownloadToFileAsync(attachment, link, path, keys, ct);
            return await GitBundles.FetchAsync(path, repository, attachment.Branch, into, ct);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // ---- plumbing ----

    private string Route(string template) =>
        template.TrimStart('/').Replace("{workspaceId}", Uri.EscapeDataString(workspaceId), StringComparison.Ordinal);

    /// <summary>Links are absolute; a relative one is resolved against the server.</summary>
    private Uri Resolve(string url) => AttachmentTransfers.Resolve(api.BaseAddress, url);

    private static FileStream TempFile() =>
        new(Path.Combine(Path.GetTempPath(), "mbus-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            CopyBuffer, FileOptions.Asynchronous | FileOptions.DeleteOnClose);

    private static string FileName(string branch)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string([.. branch.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '-' : c)]);
        return name.Length > 0 ? name : "branch";
    }

    private async Task<TResult> PostAsync<TBody, TResult>(string path, TBody body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body, MessageBusJson.Options.GetTypeInfo(typeof(TBody)));
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await api.PostAsync(path, content, ct);
        return await ReadAsync<TResult>(response, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureAsync(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return (T?)await JsonSerializer.DeserializeAsync(stream, MessageBusJson.Options.GetTypeInfo(typeof(T)), ct)
               ?? throw new AttachmentTransferException("The service sent an empty answer.", response.StatusCode);
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var text = await response.Content.ReadAsStringAsync(ct);
        McpError? error = null;
        try
        {
            error = text.Length > 0 ? MessageBusJson.Deserialize<McpError>(text) : null;
        }
        catch (JsonException)
        {
        }
        var code = error?.Code ?? (response.Headers.TryGetValues(MbusHeaders.Error, out var values) ? values.FirstOrDefault() : null);
        throw new AttachmentTransferException(
            error?.Message is { Length: > 0 } message ? message : $"The attachment request failed ({(int)response.StatusCode} {response.ReasonPhrase}).",
            response.StatusCode, code);
    }
}
