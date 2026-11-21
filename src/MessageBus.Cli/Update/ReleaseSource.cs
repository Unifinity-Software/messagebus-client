using System.Net;
using System.Runtime.CompilerServices;
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
/// <param name="http">A separate client from <see cref="CreateHttpClient"/>; disposed with the source.</param>
/// <param name="channelUrl">The channel's base URL, ending in <c>/</c>.</param>
public sealed class HttpReleaseSource(HttpClient http, Uri channelUrl) : IReleaseSource, IDisposable
{
    private static readonly ConditionalWeakTable<HttpClient, object> PublicClients = new();

    /// <summary>Creates a public artifact transport without redirects, cookies, default headers or ambient credentials.</summary>
    public static HttpClient CreateHttpClient() => CreateHttpClient(() => HttpClient.DefaultProxy);

    // Per-instance default-proxy seam keeps real transport fixtures independent of process globals.
    internal static HttpClient CreateHttpClient(Func<IWebProxy> defaultProxy) => Register(new HttpClient(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, Credentials = null,
        PreAuthenticate = false, DefaultProxyCredentials = null,
        Proxy = MessageBus.Http.CredentialFreeProxy.Wrap(null, defaultProxy),
    }));

    private static HttpClient Register(HttpClient client)
    {
        PublicClients.Add(client, new object());
        return client;
    }

    internal static HttpClient CreateTestHttpClient(HttpMessageHandler handler, bool disposeHandler = true)
    {
        if (handler is DelegatingHandler or HttpClientHandler or SocketsHttpHandler)
            throw new ArgumentException("Test feeds require an in-memory terminal handler.", nameof(handler));
        return Register(new HttpClient(handler, disposeHandler));
    }

    internal static void ValidateChannelUrl(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme is not ("https" or "http")
            || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0
            || url.OriginalString.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\'))
            throw new CliException("Unsafe public release feed URL; credentials, query and fragment are forbidden.");
    }

    /// <inheritdoc />
    public string Description => channelUrl.AbsoluteUri;

    /// <inheritdoc />
    public string? MinimumVersion { get; private set; }

    /// <inheritdoc />
    public async Task<Stream> OpenAsync(string name, CancellationToken cancellationToken)
    {
        ValidateChannelUrl(channelUrl);
        if (!PublicClients.TryGetValue(http, out _) || http.DefaultRequestHeaders.Any())
            throw new UpdateRefusedException("Public releases require a separate factory-created client without default headers.");
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
        try
        {
            return new ResponseStream(response, await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        }
        catch { response.Dispose(); throw; }
    }

    /// <inheritdoc />
    public void Dispose() => http.Dispose();

    // Keep downloads streaming so SelfUpdater can enforce its limits before buffering the body.
    private sealed class ResponseStream(HttpResponseMessage response, Stream stream) : Stream
    {
        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => stream.CanSeek;
        public override bool CanWrite => false;
        public override long Length => stream.Length;
        public override long Position { get => stream.Position; set => stream.Position = value; }
        public override int Read(byte[] buffer, int offset, int count) => stream.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => stream.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => stream.Seek(offset, origin);
        public override void Flush() => stream.Flush();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { stream.Dispose(); response.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
