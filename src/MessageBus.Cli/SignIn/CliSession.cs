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
/// refresh and retry once on a 401, and take the sign-in lock (<see cref="ConfigStore.WithSessionLockAsync{T}"/>)
/// around every refresh so concurrent mbus processes use each other's new tokens instead of replaying
/// an old refresh token (which the service treats as stolen and answers by ending the sign-in).
/// </summary>
public sealed class CliSession(CliHost host, ConfigStore store, Uri server)
{
    /// <summary>Refresh when the access token has less than this left.</summary>
    public static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private const string Prefix = "api/cli/auth/";

    private readonly string _serverText = server.AbsoluteUri.TrimEnd('/');

    /// <summary>The saved sign-in for this server, if any.</summary>
    public CliSessionFile? Saved => store.LoadSession() is { } saved && saved.Server == _serverText ? saved : null;

    internal async Task<CliStartResponse> StartAsync(CliStartBody body, CancellationToken ct)
    {
        using var response = await SendAnonymousAsync(Post("start", body, CliJsonContext.Default.CliStartBody), ct);
        await EnsureSuccessAsync(response, "start signing in", ct);
        return await ReadAsync(response, CliJsonContext.Default.CliStartResponse, ct);
    }

    /// <summary>One poll. A rate-limited answer comes back as <c>pending</c> with the wait the server asked for.</summary>
    internal async Task<CliPollResponse> PollAsync(string deviceCode, CancellationToken ct)
    {
        using var response = await SendAnonymousAsync(Post("token", new CliTokenBody(deviceCode), CliJsonContext.Default.CliTokenBody), ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
            return new CliPollResponse { State = "pending", Interval = (int)Math.Ceiling(wait.TotalSeconds) };
        }
        await EnsureSuccessAsync(response, "check the sign-in", ct);
        return await ReadAsync(response, CliJsonContext.Default.CliPollResponse, ct);
    }

    /// <summary>Saves an approved poll's tokens, replacing any earlier sign-in.</summary>
    internal Task<CliSessionFile> SaveApprovedAsync(CliPollResponse approved, CancellationToken ct) =>
        store.WithSessionLockAsync(transaction =>
        {
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
            };
            transaction.SaveSession(saved);
            return Task.FromResult(saved);
        }, ct);

    /// <summary>
    /// An access token that is good for a while yet, refreshing it if needed; null when the sign-in
    /// has ended (expired, or signed out from My account), in which case the saved one is deleted.
    /// </summary>
    /// <param name="rejected">The token the server just refused with a 401: refresh even if it looks fresh.</param>
    /// <param name="ct">Cancels.</param>
    public async Task<string?> AccessTokenAsync(string? rejected, CancellationToken ct)
    {
        if (Saved is not { } saved) return null;
        if (rejected is null && Fresh(saved)) return saved.AccessToken;
        return await store.WithSessionLockAsync<string?>(async transaction =>
        {
            // Read again: another process may have refreshed while this one waited for the lock.
            if (transaction.LoadSession() is not { } current || current.Server != _serverText) return null;
            if (current.AccessToken != rejected && Fresh(current)) return current.AccessToken;
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
        if (await AccessTokenAsync(null, ct) is not { } token) return null;
        var response = await SendWithAsync(build(), token, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
        response.Dispose();
        if (await AccessTokenAsync(token, ct) is not { } refreshed) return null;
        return await SendWithAsync(build(), refreshed, ct);
    }

    /// <summary>
    /// Ends the sign-in on the server (its whole refresh chain) and deletes the saved one either way.
    /// True when the server ended it or it had already ended; false (with the reason) when the server
    /// couldn't be asked, in which case only this machine forgot it.
    /// </summary>
    public async Task<(bool Ended, string? Problem)> LogoutAsync(CancellationToken ct)
    {
        if (Saved is null)
        {
            return (true, null);
        }
        return await store.WithSessionLockAsync<(bool Ended, string? Problem)>(async transaction =>
        {
            try
            {
                if (transaction.LoadSession() is not { } current || current.Server != _serverText) return (true, null);
                var request = Post("logout", new CliRefreshBody(current.RefreshToken), CliJsonContext.Default.CliRefreshBody);
                request.Headers.Authorization = new AuthenticationHeaderValue(AuthSchemes.Cli, current.AccessToken);
                using var response = await SendAnonymousAsync(request, ct);
                // 401: already expired or revoked, which is what signing out wants.
                return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Unauthorized
                    ? (true, (string?)null)
                    : (false, $"the server answered {(int)response.StatusCode}");
            }
            catch (CliException ex)
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
        using var response = await SendAnonymousAsync(
            Post("refresh", new CliRefreshBody(current.RefreshToken), CliJsonContext.Default.CliRefreshBody), ct);
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
        });
        return tokens.AccessToken;
    }

    private async Task<HttpResponseMessage> SendWithAsync(HttpRequestMessage request, string accessToken, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue(AuthSchemes.Cli, accessToken);
        return await SendAnonymousAsync(request, ct);
    }

    private async Task<HttpResponseMessage> SendAnonymousAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var client = ServerConnection.CreateAnonymousClient(host, server);
        try
        {
            using (request)
            {
                return await client.SendAsync(request, ct);
            }
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
