using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Server;
using MessageBus.Contracts.Security;

namespace MessageBus.Cli.SignIn;

/// <summary><c>POST /api/cli/auth/start</c>: what mbus says about this machine (the approval page shows it).</summary>
internal sealed record CliStartBody(string ClientName, string MachineName, string? Location, string? Os);

/// <summary>The codes for one sign-in: the device code is mbus's polling secret, the user code is what the person compares.</summary>
public sealed record CliStartResponse
{
    public string DeviceCode { get; init; } = "";
    public string UserCode { get; init; } = "";
    public int ExpiresIn { get; init; }
    public int Interval { get; init; }
}

internal sealed record CliTokenBody(string DeviceCode);

/// <summary>A poll's answer: <c>pending</c>, <c>denied</c>, <c>expired</c>, or <c>approved</c> with the tokens.</summary>
public sealed record CliPollResponse
{
    public string State { get; init; } = "";
    public int? Interval { get; init; }
    public string? AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public int? ExpiresIn { get; init; }
    public string? SessionId { get; init; }
    public CliAccount? Account { get; init; }
}

/// <summary>Who approved the sign-in.</summary>
public sealed record CliAccount
{
    public string? SubjectId { get; init; }
    public string? Name { get; init; }
    public string? Email { get; init; }
}

internal sealed record CliRefreshBody(string RefreshToken);

/// <summary>A refresh's new tokens.</summary>
public sealed record CliRefreshResponse
{
    public string AccessToken { get; init; } = "";
    public string RefreshToken { get; init; } = "";
    public int ExpiresIn { get; init; }
}

/// <summary>
/// The person's mbus sign-in on one server (the original issue), saved in <see cref="ConfigStore.SessionPath"/>. It
/// follows the Groundhog CLI: refresh before a call when fewer than <see cref="RefreshMargin"/> remain,
/// refresh and retry once on a 401, and take the sign-in lock (<see cref="ConfigStore.WithProfileSecurityLockAsync{T}"/>)
/// around every refresh so concurrent mbus processes use each other's new tokens instead of replaying
/// an old refresh token (which the service treats as stolen and answers by ending the sign-in).
/// </summary>
public sealed class CliSession(CliHost host, ConfigStore store, Uri server)
{
    /// <summary>Refresh when the access token has less than this left.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private const string Prefix = "api/cli/auth/";

    private readonly string _serverText = server.AbsoluteUri.TrimEnd('/');
    private readonly ServerEndpoint _endpoint = ServerEndpoint.Parse(server.AbsoluteUri);
    private string? _deviceCode;
    private ServerBindingSnapshot? _deviceBinding;
    private CliPollResponse? _approved;
    private ServerBindingSnapshot? _approvedBinding;

    /// <summary>The saved sign-in for this server, if any.</summary>
    public CliSessionFile? Saved => store.LoadSession() is { } saved && saved.Server == _serverText ? saved : null;

    internal Task<CliStartResponse> StartAsync(CliStartBody body, CancellationToken ct) => StartAsync(body, null, ct);

    internal async Task<CliStartResponse> StartAsync(CliStartBody body, Uri? approval, CancellationToken ct)
    {
        _deviceCode = null;
        _approved = null;
        var sent = await SendGatedAsync(Post("start", body, CliJsonContext.Default.CliStartBody), ct,
            validate: prepared =>
            {
                var current = BrowserSignIn.ParseApproval(prepared.Discovery.Info.SignIn?.CliApprovalUrl);
                if (approval is not null && current != approval)
                    throw new ServerBindingException("The approval address changed. Run login again.");
                BrowserSignIn.DemandApproval(current, prepared);
            });
        using var response = sent.Response;
        await EnsureSuccessAsync(response, "start signing in", ct);
        var started = await ReadAsync(response, CliJsonContext.Default.CliStartResponse, ct);
        _deviceBinding = sent.Binding;
        _deviceCode = started.DeviceCode;
        return started;
    }

    /// <summary>One poll. A rate-limited answer comes back as <c>pending</c> with the wait the server asked for.</summary>
    internal async Task<CliPollResponse> PollAsync(string deviceCode, CancellationToken ct)
    {
        if (_deviceCode is null || deviceCode != _deviceCode)
            throw new ServerBindingException("The device code was not issued by this sign-in at the selected server.");
        var sent = await SendGatedAsync(Post("token", new CliTokenBody(deviceCode), CliJsonContext.Default.CliTokenBody), ct,
            validate: prepared => DemandSameBinding(_deviceBinding, prepared.Binding));
        using var response = sent.Response;
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
            return new CliPollResponse { State = "pending", Interval = (int)Math.Ceiling(wait.TotalSeconds) };
        }
        await EnsureSuccessAsync(response, "check the sign-in", ct);
        var poll = await ReadAsync(response, CliJsonContext.Default.CliPollResponse, ct);
        if (poll.State == "approved") { _approved = poll; _approvedBinding = sent.Binding; }
        if (poll.State is "denied" or "expired") _deviceCode = null;
        return poll;
    }

    /// <summary>Saves an approved poll's tokens, replacing any earlier sign-in.</summary>
    internal Task<CliSessionFile> SaveApprovedAsync(CliPollResponse approved, CancellationToken ct) =>
        store.WithSessionLockAsync(async transaction =>
        {
            if (!ReferenceEquals(approved, _approved))
                throw new ServerBindingException("The approval was not collected by this sign-in.");
            using var gate = new ServerSendGate(host, store, server);
            var destination = new Uri(_endpoint.Value + Prefix + "token");
            var prepared = await gate.PrepareSendAsync(destination, ct, transaction);
            DemandSameBinding(_approvedBinding, prepared.Binding);
            await prepared.ConsumeAsync(destination, _ => Task.FromResult(true), ct, transaction);
            var now = host.Time.GetUtcNow();
            var saved = new CliSessionFile
            {
                Server = _serverText,
                AccessToken = approved.AccessToken ?? throw Unreadable(),
                AccessExpiresAt = now + TimeSpan.FromSeconds(approved.ExpiresIn ?? 0),
                RefreshToken = approved.RefreshToken ?? throw Unreadable(),
                SessionId = approved.SessionId,
                AccountName = approved.Account?.Name,
                AccountEmail = approved.Account?.Email,
                SignedInAt = now,
                ServerBinding = _approvedBinding,
            };
            transaction.SaveSession(saved);
            _approved = null;
            _deviceCode = null;
            return saved;
        }, ct);

    /// <summary>
    /// An access token that is good for a while yet, refreshing it if needed; null when the sign-in
    /// has ended (expired, or signed out from My account), in which case the saved one is deleted.
    /// </summary>
    /// <param name="rejected">The token the server just refused with a 401: refresh even if it looks fresh.</param>
    /// <param name="ct">Cancels.</param>
    public async Task<string?> AccessTokenAsync(string? rejected, CancellationToken ct)
    {
        return await store.WithSessionLockAsync<string?>(async transaction =>
        {
            // Read again: another process may have refreshed while this one waited for the lock.
            if (transaction.LoadSession() is not { } current || current.Server != _serverText) return null;
            if (current.AccessToken != rejected && Fresh(current))
            {
                using var gate = new ServerSendGate(host, store, server);
                var destination = new Uri(_endpoint.Value + Prefix + "refresh");
                var prepared = await gate.PrepareSendAsync(destination, ct, transaction);
                await prepared.ConsumeAsync(destination, _ => Task.FromResult(true), ct, transaction);
                return current.AccessToken;
            }
            return await RefreshAsync(current, transaction, ct);
        }, ct);
    }

    /// <summary>
    /// Sends a request as the signed-in person (<c>Authorization: MbusCli …</c>), refreshing first if
    /// needed and once more after a 401. Null when there is no sign-in left to send it with.
    /// </summary>
    /// <param name="build">Makes the request (called again for the retry).</param>
    /// <param name="ct">Cancels.</param>
    public async Task<HttpResponseMessage?> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        using var request = build();
        ResolveDestination(request); // Reject caller destinations before even refreshing a credential.
        if (await AccessTokenAsync(null, ct) is not { } token) return null;
        var response = (await SendGatedAsync(request, ct, accessToken: token)).Response;
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
        response.Dispose();
        using var retry = build();
        ResolveDestination(retry);
        if (await AccessTokenAsync(token, ct) is not { } refreshed) return null;
        return (await SendGatedAsync(retry, ct, accessToken: refreshed)).Response;
    }

    /// <summary>
    /// Ends the sign-in on the server (its whole refresh chain) and deletes the saved one either way.
    /// True when the server ended it or it had already ended; false (with the reason) when the server
    /// couldn't be asked, in which case only this machine forgot it.
    /// </summary>
    public async Task<(bool Ended, string? Problem)> LogoutAsync(CancellationToken ct)
    {
        return await store.WithSessionLockAsync<(bool Ended, string? Problem)>(async transaction =>
        {
            try
            {
                if (transaction.LoadSession() is not { } current) return (true, null);
                if (current.Server != _serverText)
                    return (false, "the saved sign-in belongs to a different server; it was only forgotten here");
                var request = Post("logout", new CliRefreshBody(current.RefreshToken), CliJsonContext.Default.CliRefreshBody);
                using var response = (await SendGatedAsync(request, ct, transaction, accessToken: current.AccessToken)).Response;
                // 401: already expired or revoked, which is what signing out wants.
                return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Unauthorized
                    ? (true, (string?)null)
                    : (false, $"the server answered {(int)response.StatusCode}");
            }
            catch (Exception ex) when (ex is CliException or ServerBindingException)
            {
                return (false, ex.Message);
            }
            finally
            {
                transaction.DeleteSession();
            }
        }, ct);
    }

    private bool Fresh(CliSessionFile session) => session.AccessExpiresAt - host.Time.GetUtcNow() > RefreshMargin;

    /// <summary>Rotates the refresh token (inside the lock). Null, and the saved sign-in deleted, when the server says it has ended.</summary>
    private async Task<string?> RefreshAsync(CliSessionFile current, ConfigStore.ProfileSecurityTransaction transaction, CancellationToken ct)
    {
        var sent = await SendGatedAsync(
            Post("refresh", new CliRefreshBody(current.RefreshToken), CliJsonContext.Default.CliRefreshBody), ct, transaction);
        using var response = sent.Response;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            transaction.DeleteSession();
            return null;
        }
        await EnsureSuccessAsync(response, "renew your sign-in", ct);
        var tokens = await ReadAsync(response, CliJsonContext.Default.CliRefreshResponse, ct);
        transaction.SaveSession(current with
        {
            AccessToken = tokens.AccessToken,
            AccessExpiresAt = host.Time.GetUtcNow() + TimeSpan.FromSeconds(tokens.ExpiresIn),
            RefreshToken = tokens.RefreshToken,
            ServerBinding = sent.Binding,
        });
        return tokens.AccessToken;
    }

    private void ResolveDestination(HttpRequestMessage request)
    {
        var destination = request.RequestUri ?? throw new ServerBindingException("The request has no destination.");
        if (!destination.IsAbsoluteUri) destination = new Uri(new Uri(_endpoint.Value), destination);
        if (!_endpoint.Contains(destination))
            throw new ServerBindingException("The request destination differs from the selected sign-in server or API base path.");
        request.RequestUri = destination;
    }

    private static void DemandSameBinding(ServerBindingSnapshot? original, ServerBindingSnapshot? current)
    {
        if (original != current) throw new ServerBindingException("The sign-in authority changed after the device code was issued.");
    }

    private async Task<(HttpResponseMessage Response, ServerBindingSnapshot? Binding)> SendGatedAsync(
        HttpRequestMessage request, CancellationToken ct, ConfigStore.ProfileSecurityTransaction? transaction = null,
        Action<PreparedServerSend>? validate = null, string? accessToken = null)
    {
        using (request)
        try
        {
            ResolveDestination(request);
            using var gate = new ServerSendGate(host, store, server);
            using var client = ServerConnection.CreateAnonymousClient(host, server);
            var prepared = await gate.PrepareSendAsync(request.RequestUri!, ct, transaction);
            validate?.Invoke(prepared);
            if (request.Content is { } content)
            {
                var copy = new ByteArrayContent(await content.ReadAsByteArrayAsync(ct));
                foreach (var header in content.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
                request.Content = copy;
                content.Dispose();
            }
            var response = await prepared.ConsumeAsync(request.RequestUri!, async _ =>
            {
                if (accessToken is not null)
                    request.Headers.Authorization = new AuthenticationHeaderValue(AuthSchemes.Cli, accessToken);
                try
                {
                    var result = await client.SendAsync(request, ct);
                    if ((int)result.StatusCode is >= 300 and <= 399)
                    {
                        result.Dispose();
                        throw new ServerBindingException("Sign-in redirects are refused; credentials were not replayed.");
                    }
                    return result;
                }
                finally { if (accessToken is not null) request.Headers.Authorization = null; }
            }, ct, transaction);
            return (response, prepared.Binding);
        }
        catch (HttpRequestException ex)
        {
            throw new CliException($"Can't reach {_serverText}: {ex.Message}", 1, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new CliException($"{_serverText} didn't answer in time.", 1, ex);
        }
    }

    private static HttpRequestMessage Post<T>(string path, T body, JsonTypeInfo<T> type)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body, type)));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return new HttpRequestMessage(HttpMethod.Post, Prefix + path) { Content = content };
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new CliException($"Couldn't {action}: {_serverText} can't sign mbus in through the browser yet (it needs updating).");
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new CliException($"Couldn't {action}: too many sign-in attempts from this address. Wait a few minutes and try again.");
        }
        var text = await response.Content.ReadAsStringAsync(ct);
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
            {
                message = m.GetString();
            }
        }
        catch (JsonException)
        {
        }
        throw new CliException($"Couldn't {action}: the server answered {(int)response.StatusCode}.{(message is null ? "" : " " + message)}");
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, JsonTypeInfo<T> type, CancellationToken ct)
    {
        try
        {
            return JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(ct), type) ?? throw Unreadable();
        }
        catch (JsonException)
        {
            throw Unreadable();
        }
    }

    private static CliException Unreadable() =>
        new($"The server sent an answer mbus doesn't understand. Check that mbus is up to date (this is {CliHost.Version}).");
}
