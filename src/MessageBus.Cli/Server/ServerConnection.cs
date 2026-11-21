using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Security;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Security;

namespace MessageBus.Cli.Server;

/// <summary>
/// The calls mbus makes to the service, and the authenticated <see cref="HttpClient"/> every other
/// part of mbus (the local MCP server, the runner) uses for its own calls.
/// </summary>
public static class ServerConnection
{
    /// <summary>
    /// An <see cref="HttpClient"/> for <paramref name="settings"/>' server whose every request carries this
    /// machine's credentials: <c>MBUS_API_KEY</c> when set, else the enrolled machine key's signature.
    /// Relative paths resolve against the server URL (<c>api/…</c>, without a leading slash, keeps a
    /// server that lives under a path prefix working).
    /// </summary>
    /// <param name="host">The machine.</param>
    /// <param name="settings">Resolved settings.</param>
    /// <param name="store">The settings folder (for the machine key).</param>
    /// <param name="component"><c>cli</c>, <c>mcp</c> or <c>runner</c> (<see cref="MbusHeaders.ClientComponent"/>).</param>
    public static HttpClient CreateAuthenticatedClient(CliHost host, CliSettings settings, ConfigStore store, string component)
    {
        var server = settings.Server ?? throw NotSignedIn();
        DemandStoredCredentialScope(settings);
        AuthenticatingHandler handler;
        if (settings.ApiKey is { } apiKey)
        {
            handler = AuthenticatingHandler.WithApiKey(ServerHttpTransport.Restrict(host.CreateHttpHandler()), apiKey, settings.Stored.WorkspaceId, component, CliHost.Version, new ServerSendGate(host, store, server));
        }
        else
        {
            var signer = CreateSigner(host, settings, store) ?? throw NotSignedIn();
            handler = AuthenticatingHandler.WithMachineKey(ServerHttpTransport.Restrict(host.CreateHttpHandler()), signer, settings.Stored.WorkspaceId, component, CliHost.Version, new ServerSendGate(host, store, server));
        }
        return ServerAttachmentTransfers.Bind(new HttpClient(handler, disposeHandler: true) { BaseAddress = CliSettings.BaseAddress(server) },
            () => new ServerSendGate(host, store, server));
    }

    /// <summary>The enrolled machine's request signer, or null when this machine isn't enrolled.</summary>
    public static MachineRequestSigner? CreateSigner(CliHost host, CliSettings settings, ConfigStore store)
    {
        DemandStoredCredentialScope(settings);
        if (settings.Stored.MachineId is not { } machineId || store.LoadKeyPem() is not { } pem)
        {
            return null;
        }
        var key = MachineKey.FromPem(pem);
        if (settings.Stored.KeyFingerprint is { } fingerprint && key.Fingerprint != fingerprint)
        {
            key.Dispose();
            throw new ServerBindingException("The saved machine key does not match this identity.");
        }
        return new MachineRequestSigner(key, machineId, host.Time);
    }

    // A selected destination is not permission to reuse an identity or workspace from another endpoint.
    internal static void DemandStoredCredentialScope(CliSettings settings)
    {
        if (settings.Stored.MachineId is null && settings.Stored.WorkspaceId is null) return;
        if (settings.Server is null || settings.Stored.Server is null
            || ServerEndpoint.Parse(settings.Server.AbsoluteUri) != ServerEndpoint.Parse(settings.Stored.Server))
            throw new ServerBindingException("Saved machine credentials belong to a different server or API base path. Enroll explicitly at the selected endpoint.");
    }

    /// <summary>A client with no credentials, for <c>/api/server</c> and enrollment.</summary>
    internal static HttpClient CreateAnonymousClient(CliHost host, Uri server)
    {
        var client = new HttpClient(ServerHttpTransport.Restrict(host.CreateHttpHandler()), disposeHandler: true) { BaseAddress = CliSettings.BaseAddress(server) };
        client.DefaultRequestHeaders.TryAddWithoutValidation(MbusHeaders.ClientVersion, CliHost.Version);
        client.DefaultRequestHeaders.TryAddWithoutValidation(MbusHeaders.ClientComponent, "cli");
        return client;
    }

    internal static CliException NotSignedIn() =>
        new("This machine isn't signed in. Run: mbus login --server <url>");

    /// <summary>Asks <paramref name="server"/> what it is and checks it is a messagebus.ai server.</summary>
    internal static async Task<ServerInfoResponse> GetServerInfoAsync(HttpClient client, Uri server, CancellationToken ct)
    {
        using var response = await SendAsync(client, new HttpRequestMessage(HttpMethod.Get, Relative("/api/server")), server, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new CliException($"{server} isn't a messagebus.ai server (it answered {(int)response.StatusCode} to /api/server).");
        }
        ServerInfoResponse? info;
        try
        {
            info = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(ct), CliJsonContext.Default.ServerInfoResponse);
        }
        catch (JsonException)
        {
            info = null;
        }
        if (info?.Product != ServerInfoResponse.ProductName)
        {
            throw new CliException($"{server} isn't a messagebus.ai server. Check the address you were given.");
        }
        return info;
    }

    /// <summary>Enrolls this machine (<see cref="ApiRoutes.MachineEnroll"/>).</summary>
    internal static async Task<MachineEnrollResponse> EnrollAsync(HttpClient client, Uri server, AuthenticationHeaderValue authorization, MachineEnrollRequest body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Relative(ApiRoutes.MachineEnroll)) { Content = Json(body) };
        request.Headers.Authorization = authorization;
        using var response = await SendAsync(client, request, server, ct);
        return await ReadEnrollAsync(response, ct);
    }

    /// <summary>Enrollment with an intentionally supplied credential, gated before attaching headers or enrollment content.</summary>
    internal static async Task<(MachineEnrollResponse Response, ServerBindingSnapshot? Binding)> EnrollSuppliedAsync(
        CliHost host, ConfigStore store, Uri server, AuthenticationHeaderValue credential,
        Func<Task<MachineEnrollRequest>> body, CancellationToken ct, ConfigStore.ProfileSecurityTransaction? transaction = null,
        Action<PreparedServerSend>? validate = null)
    {
        using var gate = new ServerSendGate(host, store, server);
        var destination = new Uri(CliSettings.BaseAddress(server), Relative(ApiRoutes.MachineEnroll));
        var prepared = await gate.PrepareSendAsync(destination, ct, transaction);
        validate?.Invoke(prepared);
        using var request = new HttpRequestMessage(HttpMethod.Post, destination);
        // No enrollment content or credential is attached until current anonymous validation succeeds.
        request.Content = Json(await body());
        using var client = CreateAnonymousClient(host, server);
        using var response = transaction is null
            ? await store.WithProfileSecurityLockAsync(tx => prepared.ConsumeEnrollmentAsync(destination, Send, ct, tx), ct)
            : await prepared.ConsumeEnrollmentAsync(destination, Send, ct, transaction);
        return (await ReadEnrollAsync(response, ct), prepared.Binding);

        async Task<HttpResponseMessage> Send(ServerSendAuthorization _)
        {
            request.Headers.Authorization = credential;
            try
            {
                var result = await SendAsync(client, request, server, ct);
                if ((int)result.StatusCode is >= 300 and <= 399)
                {
                    result.Dispose();
                    throw new ServerBindingException("Enrollment redirects are refused; credentials were not replayed.");
                }
                return result;
            }
            finally { request.Headers.Authorization = null; }
        }
    }

    /// <summary>An enrollment request with no credential yet (the caller adds the person's sign-in).</summary>
    internal static HttpRequestMessage EnrollRequest(MachineEnrollRequest body) =>
        new(HttpMethod.Post, Relative(ApiRoutes.MachineEnroll)) { Content = Json(body) };

    // Prove the newly enrolled key before publishing it over the previous profile identity. No stored
    // machine/workspace or key loader participates, and the explicit transaction stays held through send.
    internal static async Task PutEnrollmentInventoryAsync(CliHost host, ConfigStore store, Uri server, MachineKey key,
        string machineId, string workspaceId, MachineInventory inventory, ServerBindingSnapshot? binding,
        ConfigStore.ProfileSecurityTransaction transaction, CancellationToken ct)
    {
        using var gate = new ServerSendGate(host, store, server);
        var destination = new Uri(CliSettings.BaseAddress(server), Relative(ApiRoutes.MachineInventory));
        var prepared = await gate.PrepareSendAsync(destination, ct, transaction);
        if (prepared.Binding != binding) throw new ServerBindingException("The enrollment authority changed before proving the new key.");
        using var request = new HttpRequestMessage(HttpMethod.Put, destination) { Content = Json(inventory with { MachineId = machineId }) };
        var bytes = await request.Content.ReadAsByteArrayAsync(ct);
        using var client = CreateAnonymousClient(host, server);
        var signer = new MachineRequestSigner(key, machineId, host.Time);
        using var response = await prepared.ConsumeEnrollmentAsync(destination, async authorization =>
        {
            request.Headers.TryAddWithoutValidation(MbusHeaders.Workspace, workspaceId);
            foreach (var (name, value) in signer.CreateHeaders(authorization, request, bytes))
                request.Headers.TryAddWithoutValidation(name, value);
            try
            {
                var result = await SendAsync(client, request, server, ct);
                if ((int)result.StatusCode is >= 300 and <= 399)
                {
                    result.Dispose();
                    throw new ServerBindingException("Enrollment proof redirects are refused; credentials were not replayed.");
                }
                return result;
            }
            finally
            {
                request.Headers.Authorization = null;
                request.Headers.Remove(MbusHeaders.Workspace);
                request.Headers.Remove(MbusHeaders.Signature);
            }
        }, ct, transaction);
        await EnsureSuccessAsync(response, "prove the enrolled machine key", ct);
    }

    // API-key login has no saved machine/workspace credentials. Keep its actual inventory send in
    // the explicit enrollment transaction, validating current trust after this send's discovery.
    internal static async Task<ServerBindingSnapshot?> PutApiKeyEnrollmentInventoryAsync(CliHost host, ConfigStore store,
        Uri server, string apiKey, MachineInventory inventory, ConfigStore.ProfileSecurityTransaction transaction, CancellationToken ct)
    {
        using var gate = new ServerSendGate(host, store, server);
        var destination = new Uri(CliSettings.BaseAddress(server), Relative(ApiRoutes.MachineInventory));
        var prepared = await gate.PrepareSendAsync(destination, ct, transaction);
        using var request = new HttpRequestMessage(HttpMethod.Put, destination) { Content = Json(inventory) };
        await request.Content.LoadIntoBufferAsync(ct);
        request.Headers.TryAddWithoutValidation(MbusHeaders.ClientVersion, CliHost.Version);
        request.Headers.TryAddWithoutValidation(MbusHeaders.ClientComponent, "cli");
        using var client = CreateAnonymousClient(host, server);
        using var response = await prepared.ConsumeEnrollmentAsync(destination, async _ =>
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(AuthSchemes.ApiKey, apiKey);
            try
            {
                var result = await SendAsync(client, request, server, ct);
                if ((int)result.StatusCode is >= 300 and <= 399)
                {
                    result.Dispose();
                    throw new ServerBindingException("Inventory redirects are refused; the API key was not replayed.");
                }
                return result;
            }
            finally { request.Headers.Authorization = null; }
        }, ct, transaction);
        await EnsureSuccessAsync(response, "report this machine's inventory", ct);
        return prepared.Binding;
    }

    /// <summary>The enrolled machine from an enrollment answer; throws <see cref="CliException"/> when it was refused.</summary>
    internal static async Task<MachineEnrollResponse> ReadEnrollAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, "enroll this machine", ct, showWorkspaceSetup: true);
        return await ReadAsync<MachineEnrollResponse>(response, ct);
    }

    /// <summary>True for the service's "you belong to more than one workspace: choose one" answer.</summary>
    internal static async Task<bool> IsWorkspaceRequiredAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.BadRequest) return false;
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("error", out var error) && error.GetString() == "workspace-required";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The workspaces the signed-in person can enroll a machine in (<see cref="ApiRoutes.MachineWorkspaces"/>).</summary>
    internal static HttpRequestMessage WorkspacesRequest() => new(HttpMethod.Get, Relative(ApiRoutes.MachineWorkspaces));

    internal static async Task<IReadOnlyList<EnrollableWorkspace>> ReadWorkspacesAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, "list your workspaces", ct, showWorkspaceSetup: true);
        return await ReadAsync<IReadOnlyList<EnrollableWorkspace>>(response, ct);
    }

    /// <summary>Reports a fresh agent scan (<see cref="ApiRoutes.MachineInventory"/>).</summary>
    internal static async Task PutInventoryAsync(HttpClient authenticated, Uri server, MachineInventory inventory, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, Relative(ApiRoutes.MachineInventory)) { Content = Json(inventory) };
        using var response = await SendAsync(authenticated, request, server, ct);
        await EnsureSuccessAsync(response, "update this machine's agent list", ct);
    }
    /// <summary>
    /// Removes this machine from its workspace (<c>DELETE /api/w/{workspaceId}/machines/{machineId}</c>;
    /// a machine may remove itself), so its key stops working on the server too. Throws
    /// <see cref="CliException"/> when the server can't be reached or refuses.
    /// </summary>
    internal static async Task RemoveMachineAsync(HttpClient authenticated, Uri server, string workspaceId, string machineId, CancellationToken ct)
    {
        var path = ApiRoutes.Machines.Replace("{workspaceId}", Uri.EscapeDataString(workspaceId), StringComparison.Ordinal)
            + "/" + Uri.EscapeDataString(machineId);
        using var response = await SendAsync(authenticated, new HttpRequestMessage(HttpMethod.Delete, Relative(path)), server, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return; // already removed (on the dashboard, say)
        }
        await EnsureSuccessAsync(response, "remove this machine from the server", ct);
    }


    /// <summary>An <see cref="ApiRoutes"/> path made relative to the server URL.</summary>
    public static string Relative(string apiRoute) => apiRoute.TrimStart('/');

    private static ByteArrayContent Json<T>(T value)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(MessageBusJson.Serialize(value)));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        return content;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonSerializer.Deserialize(text, (JsonTypeInfo<T>)MessageBusJson.Options.GetTypeInfo(typeof(T)))
                ?? throw new JsonException("empty");
        }
        catch (JsonException)
        {
            throw new CliException($"The server sent an answer mbus doesn't understand. Check that mbus is up to date (this is {CliHost.Version}).");
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, Uri server, CancellationToken ct)
    {
        try
        {
            using (request)
            {
                return await client.SendAsync(request, ct);
            }
        }
        catch (HttpRequestException ex)
        {
            throw new CliException($"Can't reach {server}: {ex.Message}", 1, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new CliException($"{server} didn't answer in time.", 1, ex);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string action, CancellationToken ct, bool showWorkspaceSetup = false)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        if (await SuspensionRefusal.ReadAsync(response, ct) is { } suspension) throw new CliException(suspension);
        var code = response.Headers.TryGetValues(MbusHeaders.Error, out var values) ? values.FirstOrDefault() : null;
        var (detail, workspaceSetup) = await ReadProblemAsync(response, code,
            showWorkspaceSetup && response.StatusCode == HttpStatusCode.Forbidden, ct);
        if (workspaceSetup)
        {
            throw new CliException(string.IsNullOrWhiteSpace(detail)
                ? "Your account doesn't belong to a workspace yet. Open the dashboard, join or create a workspace, then run mbus login again."
                : detail);
        }
        var reason = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "the server didn't accept this machine's sign-in. Run mbus login again",
            HttpStatusCode.Forbidden => "you don't have permission in this workspace",
            HttpStatusCode.NotFound => "the server doesn't offer this yet; it may need updating",
            _ => $"the server answered {(int)response.StatusCode}",
        };
        var extra = string.Join(" ", new[] { detail, code is null ? null : $"({code})" }.Where(s => !string.IsNullOrEmpty(s)));
        throw new CliException($"Couldn't {action}: {reason}.{(extra.Length > 0 ? " " + extra : "")}");
    }

    private static async Task<(string? Detail, bool WorkspaceSetup)> ReadProblemAsync(
        HttpResponseMessage response, string? code, bool showWorkspaceSetup, CancellationToken ct)
    {
        var workspaceSetup = showWorkspaceSetup && code == "no-workspace";
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!string.IsNullOrWhiteSpace(text))
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    string? Field(string name) => doc.RootElement.TryGetProperty(name, out var value)
                        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                    workspaceSetup |= showWorkspaceSetup && (code ?? Field("code") ?? Field("error")) == "no-workspace";
                    if (workspaceSetup)
                    {
                        var message = Field("message");
                        return (string.IsNullOrWhiteSpace(message) ? Field("detail") : message, true);
                    }
                    return (Field("detail") ?? Field("message") ?? Field("title"), false);
                }
            }
        }
        catch (JsonException)
        {
        }
        return (null, workspaceSetup);
    }
}
