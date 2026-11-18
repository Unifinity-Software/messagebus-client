using System.Net;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Releases;

namespace MessageBus.Cli.Update;

/// <summary>
/// Where a release channel's files come from: the server's feed (<see cref="ReleaseFeed.Route"/>), a
/// mirror's URL, or a folder. Nothing read from it is trusted until the manifest's signature and the
/// file's hash check out.
/// </summary>
public interface IReleaseSource
{
    /// <summary>For messages: the folder or URL.</summary>
    string Description { get; }

    /// <summary>The oldest <c>mbus</c> the server accepts, once known (the server's feed says so with each answer).</summary>
    string? MinimumVersion { get; }

    /// <summary>Opens one file of the channel; <see cref="FileNotFoundException"/> when it isn't there.</summary>
    /// <param name="name">A file name (<see cref="ReleaseFeed.IsValidName"/>).</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    Task<Stream> OpenAsync(string name, CancellationToken cancellationToken);
}

/// <summary>A channel folder on disk (local work, a file share).</summary>
/// <param name="directory">The folder holding <c>manifest.json</c>.</param>
public sealed class FolderReleaseSource(string directory) : IReleaseSource
{
    /// <inheritdoc />
    public string Description => directory;

    /// <inheritdoc />
    public string? MinimumVersion => null;

    /// <inheritdoc />
    public Task<Stream> OpenAsync(string name, CancellationToken cancellationToken)
    {
        if (!ReleaseFeed.IsValidName(name))
        {
            throw new FileNotFoundException($"'{name}' isn't a release file name.");
        }
        var path = Path.Combine(directory, name);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{directory} has no {name}.");
        }
        return Task.FromResult<Stream>(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true));
    }
}

/// <summary>A channel over HTTP(S): the server's <c>/api/releases/{channel}/</c> or a mirror with the same files.</summary>
/// <param name="http">A client (no credentials needed); disposed with the source.</param>
/// <param name="channelUrl">The channel's base URL, ending in <c>/</c>.</param>
public sealed class HttpReleaseSource(HttpClient http, Uri channelUrl) : IReleaseSource, IDisposable
{
    /// <inheritdoc />
    public string Description => channelUrl.AbsoluteUri;

    /// <inheritdoc />
    public string? MinimumVersion { get; private set; }

    /// <inheritdoc />
    public async Task<Stream> OpenAsync(string name, CancellationToken cancellationToken)
    {
        if (!ReleaseFeed.IsValidName(name))
        {
            throw new FileNotFoundException($"'{name}' isn't a release file name.");
        }
        var response = await http.GetAsync(new Uri(channelUrl, name), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.Headers.TryGetValues(MbusHeaders.MinClientVersion, out var minimum))
        {
            MinimumVersion = minimum.FirstOrDefault();
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            throw new FileNotFoundException($"{Description} has no {name}.");
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new HttpRequestException($"{Description}{name} answered {status}.", null, response.StatusCode);
        }
        return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() => http.Dispose();
}
