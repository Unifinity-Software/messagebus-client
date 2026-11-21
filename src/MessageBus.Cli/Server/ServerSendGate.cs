using System.Collections.Concurrent;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;

namespace MessageBus.Cli.Server;

/// <summary>Current anonymous discovery and durable binding preparation for each actual credential send.</summary>
public sealed class ServerSendGate : IDisposable
{
    private readonly Func<string, string?> _environment;
    private readonly ConfigStore _store;
    private readonly HttpClient _discovery;
    private sealed class SendGeneration { internal long Value; }
    // Like the profile lock, invalidation spans clients in this process. A failure at any base path invalidates
    // outstanding contexts for the origin's common security floor. Nothing in this table grants a send.
    private static readonly ConcurrentDictionary<(string Profile, string Origin), SendGeneration> Generations = new();
    private readonly SendGeneration _generation;
    private int _disposed;

    /// <summary>Creates a dedicated discovery client, separate from the authenticated client and its headers.</summary>
    public ServerSendGate(CliHost host, ConfigStore store, Uri server)
        : this(store, server, host.CreateDiscoveryHttpHandler ?? host.CreateHttpHandler, host.GetEnvironmentVariable)
    {
    }

    /// <summary>Shared seam for hosts without a CliHost. Supply a fresh credential-free discovery transport and the host's environment reader.</summary>
    public ServerSendGate(ConfigStore store, Uri server, Func<HttpMessageHandler> createDiscoveryHttpHandler,
        Func<string, string?> environment)
    {
        _environment = environment;
        _store = store;
        Endpoint = ServerEndpoint.Parse(server.AbsoluteUri);
        var profile = Path.TrimEndingDirectorySeparator(Path.GetFullPath(store.Directory));
        if (OperatingSystem.IsWindows()) profile = profile.ToUpperInvariant();
        _generation = Generations.GetOrAdd((profile, Endpoint.Origin), _ => new SendGeneration());
        _discovery = new HttpClient(ServerHttpTransport.Restrict(createDiscoveryHttpHandler()), disposeHandler: true)
        { BaseAddress = new Uri(Endpoint.Value) };
    }

    /// <summary>The exact selected API origin and base scope.</summary>
    public ServerEndpoint Endpoint { get; }

    /// <summary>Refuses a foreign destination before discovery, headers, body or credentials are attached.</summary>
    public void DemandDestination(Uri destination)
    {
        if (!Endpoint.Contains(destination))
            throw new ServerBindingException("The request destination differs from the selected server or API base path.");
    }

    /// <summary>
    /// Obtains fresh anonymous discovery and prepares the ledger and matching saved snapshots under the security lock.
    /// The result is single-use; never cache it for a later send. An existing lock-held caller may supply its transaction.
    /// </summary>
    public async Task<PreparedServerSend> PrepareSendAsync(Uri destination, CancellationToken ct,
        ConfigStore.ProfileSecurityTransaction? transaction = null)
    {
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            DemandDestination(destination);
            var generation = Volatile.Read(ref _generation.Value);
            var expectedJson = _environment(ServerBindingExpectation.EnvironmentVariable);
            ServerBindingExpectation? expected = null;
            if (expectedJson is not null)
            {
                var home = _environment(EnvVars.Home)
                    ?? throw new ServerBindingException("Isolated provisioning requires explicit durable MBUS_HOME.");
                expected = transaction is not null
                    ? transaction.ValidateProvisioning(Endpoint, expectedJson, home)
                    : await _store.WithProfileSecurityLockAsync(tx => Task.FromResult(
                        tx.ValidateProvisioning(Endpoint, expectedJson, home)), ct).ConfigureAwait(false);
            }

            using var response = await _discovery.GetAsync("api/server", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ServerBindingException($"Anonymous discovery refused ({(int)response.StatusCode}); credentials were not sent.");
            var discovery = ServerDiscoveryParser.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var snapshot = transaction is not null
                ? transaction.PrepareBinding(Endpoint, discovery, expected)
                : await _store.WithProfileSecurityLockAsync(tx => Task.FromResult(
                    tx.PrepareBinding(Endpoint, discovery, expected)), ct).ConfigureAwait(false);
            return new PreparedServerSend(Endpoint, discovery, snapshot, () => Volatile.Read(ref _disposed) == 0
                && Volatile.Read(ref _generation.Value) == generation);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _generation.Value);
            if (ex is ServerBindingException || ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            if (ex is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException or CliException)
                throw new ServerBindingException("Current anonymous discovery or durable binding preparation failed; credentials refused.", ex);
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _discovery.Dispose();
    }
}

/// <summary>A single-send result of current discovery and durable preparation, never a reusable discovery cache.</summary>
public sealed class PreparedServerSend
{
    private readonly Func<bool> _isCurrent;
    private int _consumed;
    internal PreparedServerSend(ServerEndpoint endpoint, ServerDiscovery discovery, ServerBindingSnapshot? binding, Func<bool> isCurrent)
    { Endpoint = endpoint; Discovery = discovery; Binding = binding; _isCurrent = isCurrent; }
    /// <summary>Exact selected endpoint, independent of the canonical signing authority.</summary>
    public ServerEndpoint Endpoint { get; }
    /// <summary>The current strictly parsed observation, available for approval/destination checks; it never authorizes another send.</summary>
    public ServerDiscovery Discovery { get; }
    /// <summary>Durably prepared binding; null only for affirmative never-pinned legacy discovery.</summary>
    public ServerBindingSnapshot? Binding { get; }
    /// <summary>Checks the final destination and consumes this authorization exactly once immediately before signing/sending.</summary>
    public void Consume(Uri destination)
    {
        if (!Endpoint.Contains(destination) || !_isCurrent() || Interlocked.Exchange(ref _consumed, 1) != 0)
            throw new ServerBindingException("The prepared send is stale, already used or for a different destination.");
    }
}

/// <summary>Shared no-redirect, cookie-free, ambient-credential-free API transport policy.</summary>
internal static class ServerHttpTransport
{
    internal static HttpMessageHandler Restrict(HttpMessageHandler handler)
    {
        switch (handler)
        {
            case SocketsHttpHandler sockets:
                sockets.AllowAutoRedirect = false;
                sockets.UseCookies = false;
                sockets.Credentials = null;
                sockets.PreAuthenticate = false;
                sockets.DefaultProxyCredentials = null;
                break;
            case HttpClientHandler client:
                client.AllowAutoRedirect = false;
                client.UseCookies = false;
                client.UseDefaultCredentials = false;
                client.Credentials = null;
                client.PreAuthenticate = false;
                client.DefaultProxyCredentials = null;
                break;
            case DelegatingHandler { InnerHandler: { } inner }:
                Restrict(inner);
                break;
        }
        return handler;
    }
}
