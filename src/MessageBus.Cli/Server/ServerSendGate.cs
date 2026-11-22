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
    private sealed class SendGeneration { internal long Value; internal long LegacyValue; }
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
        _discovery = new HttpClient(ServerHttpTransport.Restrict(createDiscoveryHttpHandler(), anonymous: true), disposeHandler: true)
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
            transaction?.DemandStore(_store);
            var generation = Volatile.Read(ref _generation.Value);
            var legacyGeneration = Volatile.Read(ref _generation.LegacyValue);
            var expectedJson = _environment(ServerBindingExpectation.EnvironmentVariable);
            ServerBindingExpectation? expected = null;
            if (expectedJson is not null)
            {
                var home = _environment(EnvVars.Home)
                    ?? throw new ServerBindingException("Isolated provisioning requires explicit durable MBUS_HOME.");
                expected = transaction is not null
                    ? transaction.ValidateProvisioning(Endpoint, expectedJson, home)
                    : await _store.WithProfileSecurityReadLockAsync(tx => Task.FromResult(
                        tx.ValidateProvisioning(Endpoint, expectedJson, home)), ct).ConfigureAwait(false);
            }

            using var response = await _discovery.GetAsync("api/server", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new ServerBindingException($"Anonymous discovery refused ({(int)response.StatusCode}); credentials were not sent.");
            var discovery = ServerDiscoveryParser.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var snapshot = transaction is not null
                ? PrepareBinding(transaction)
                : await _store.PrepareBindingForSendAsync(Endpoint, discovery, expected, ct).ConfigureAwait(false);
            if (transaction is null && snapshot is not null) Interlocked.Increment(ref _generation.LegacyValue);
            return new PreparedServerSend(_store, transaction, Endpoint, discovery, snapshot, expected,
                () => Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _generation.Value) == generation
                    && (snapshot is not null || Volatile.Read(ref _generation.LegacyValue) == legacyGeneration));

            ServerBindingSnapshot? PrepareBinding(ConfigStore.ProfileSecurityTransaction tx)
            {
                var binding = tx.PrepareBinding(Endpoint, discovery, expected);
                // Invalidate queued legacy contexts after successful durable isolated preparation too.
                // Other processes cannot update this counter: consumption MUST reread the durable floor.
                if (binding is not null) Interlocked.Increment(ref _generation.LegacyValue);
                return binding;
            }
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
    private readonly ConfigStore _store;
    private readonly ConfigStore.ProfileSecurityTransaction? _preparationTransaction;
    private readonly ServerBindingExpectation? _expected;
    private readonly Func<bool> _isCurrent;
    private int _consumed;
    internal PreparedServerSend(ConfigStore store, ConfigStore.ProfileSecurityTransaction? transaction,
        ServerEndpoint endpoint, ServerDiscovery discovery, ServerBindingSnapshot? binding,
        ServerBindingExpectation? expected, Func<bool> isCurrent)
    {
        _store = store; _preparationTransaction = transaction; _expected = expected;
        Endpoint = endpoint; Discovery = discovery; Binding = binding; _isCurrent = isCurrent;
    }
    /// <summary>Exact selected endpoint, independent of the canonical signing authority.</summary>
    public ServerEndpoint Endpoint { get; }
    /// <summary>The current strictly parsed observation, available for approval/destination checks; it never authorizes another send.</summary>
    public ServerDiscovery Discovery { get; }
    /// <summary>Durably prepared binding; null only for affirmative never-pinned legacy discovery.</summary>
    public ServerBindingSnapshot? Binding { get; }
    /// <summary>
    /// Rereads the durable floor and consumes exactly once under the profile lock, retaining it until send completes.
    /// Buffer content first. The callback must sign and await the actual transport send; it must not acquire another
    /// profile lock. A caller already holding a transaction must supply it explicitly, including after preparation.
    /// </summary>
    public Task<T> ConsumeAsync<T>(Uri destination, Func<ServerSendAuthorization, Task<T>> send, CancellationToken ct,
        ConfigStore.ProfileSecurityTransaction? transaction = null)
        => ConsumeCoreAsync(destination, send, ct, transaction, requireEnrollmentTrust: false);

    // Enrollment callers must explicitly retain the transaction through final validation and transmission.
    // The flag travels with this one send, never as ambient state or a reusable trust authorization.
    internal Task<T> ConsumeEnrollmentAsync<T>(Uri destination, Func<ServerSendAuthorization, Task<T>> send,
        CancellationToken ct, ConfigStore.ProfileSecurityTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return ConsumeCoreAsync(destination, send, ct, transaction, requireEnrollmentTrust: true);
    }

    private Task<T> ConsumeCoreAsync<T>(Uri destination, Func<ServerSendAuthorization, Task<T>> send, CancellationToken ct,
        ConfigStore.ProfileSecurityTransaction? transaction, bool requireEnrollmentTrust)
    {
        ArgumentNullException.ThrowIfNull(send);
        if (_preparationTransaction is not null && !ReferenceEquals(transaction, _preparationTransaction))
            throw new InvalidOperationException("Consumption requires the explicit preparation transaction.");
        if (!Endpoint.Contains(destination) || Interlocked.Exchange(ref _consumed, 1) != 0)
            throw new ServerBindingException("The prepared send is stale, already used or for a different destination.");
        return transaction is not null ? ConsumeLocked(transaction)
            : _store.WithProfileSecurityReadLockAsync(ConsumeLocked, ct);

        Task<T> ConsumeLocked(ConfigStore.ProfileSecurityTransaction tx) => tx.WithSendAsync(_store, async floor =>
        {
            ct.ThrowIfCancellationRequested();
            if (!_isCurrent()) throw new ServerBindingException("The prepared send is stale.");
            var decision = ServerBindingPolicy.Evaluate(Endpoint, Discovery, floor, _expected);
            if (decision.Binding != Binding?.Binding || Binding is { } snapshot
                && !floor.Bindings.Any(b => b.TransportOrigin == Endpoint.Origin && b.Binding == snapshot.Binding))
                throw new ServerBindingException("The prepared send no longer matches the durable floor.");
            using var authorization = new ServerSendAuthorization(Endpoint, Binding, _isCurrent);
            return await send(authorization).ConfigureAwait(false);
        }, requireEnrollmentTrust);
    }
}

/// <summary>One actual send inside a lock-held consumption callback; never retain its credentials or headers for later use.</summary>
public sealed class ServerSendAuthorization : IDisposable
{
    private readonly ServerEndpoint _endpoint;
    private readonly Func<bool> _isCurrent;
    private int _used;
    private int _disposed;
    internal ServerSendAuthorization(ServerEndpoint endpoint, ServerBindingSnapshot? binding, Func<bool> isCurrent)
    { _endpoint = endpoint; Binding = binding; _isCurrent = isCurrent; }
    internal ServerBindingSnapshot? Binding { get; }
    internal void DemandSigning(Uri destination)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_endpoint.Contains(destination) || !_isCurrent()
            || Interlocked.Exchange(ref _used, 1) != 0)
            throw new ServerBindingException("The send authorization is stale, already signed or outside its destination scope.");
    }
    /// <inheritdoc />
    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
}

/// <summary>Shared no-redirect and cookie-free policy; anonymous discovery also strips proxy credentials.</summary>
internal static class ServerHttpTransport
{
    internal static HttpMessageHandler Restrict(HttpMessageHandler handler, bool anonymous = false,
        Func<System.Net.IWebProxy>? defaultProxy = null)
    {
        switch (handler)
        {
            case SocketsHttpHandler sockets:
                sockets.AllowAutoRedirect = false;
                sockets.UseCookies = false;
                sockets.Credentials = null;
                sockets.PreAuthenticate = false;
                sockets.DefaultProxyCredentials = null;
                if (anonymous && sockets.UseProxy)
                    sockets.Proxy = MessageBus.Http.CredentialFreeProxy.Wrap(sockets.Proxy, defaultProxy);
                break;
            case HttpClientHandler client:
                client.AllowAutoRedirect = false;
                client.UseCookies = false;
                client.UseDefaultCredentials = false;
                client.Credentials = null;
                client.PreAuthenticate = false;
                client.DefaultProxyCredentials = null;
                if (anonymous && client.UseProxy)
                    client.Proxy = MessageBus.Http.CredentialFreeProxy.Wrap(client.Proxy, defaultProxy);
                break;
            case DelegatingHandler { InnerHandler: { } inner }:
                Restrict(inner, anonymous, defaultProxy);
                break;
        }
        return handler;
    }
}
