using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Security;
using MessageBus.Cli.Server;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Security;

namespace MessageBus.Cli.Runner;

/// <summary>Bootstraps and resumes a runner only at its exact issuing endpoint with the same persisted key.</summary>
internal static class RunnerKeyEnrollment
{
    /// <summary>Which API key enrolled a runner, without storing the key.</summary>
    public static string Fingerprint(string apiKey) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey.Trim())))[..16];

    private sealed record EnrollmentResult(bool Used, MachineEnrollResponse? Enrolled = null, MachineInventory? Inventory = null);

    /// <summary>Validates an existing runner or durably checkpoints and enrolls a new runner using an explicitly supplied API key.</summary>
    public static async Task<CommandContext?> EnsureAsync(CommandContext context, Func<CommandContext> reload, TextWriter log, bool onlyRunnerKeys, CancellationToken ct)
    {
        var host = context.Host;
        var apiKey = host.GetEnvironmentVariable(EnvVars.ApiKey)?.Trim();
        if (string.IsNullOrEmpty(apiKey) || context.Settings.Server is not { } server) return null;
        var endpoint = ServerEndpoint.Parse(server.AbsoluteUri);
        var fingerprint = Fingerprint(apiKey);
        var result = await context.Store.WithProfileSecurityLockAsync(async transaction =>
        {
            var current = transaction.Load();
            transaction.DemandEnrollmentTrust();
            var sameEndpoint = current.Server is not null && ServerEndpoint.Parse(current.Server) == endpoint;
            using var gate = new ServerSendGate(host, context.Store, server);
            var destination = new Uri(CliSettings.BaseAddress(server), ServerConnection.Relative(ApiRoutes.MachineEnroll));

            if (current.MachineId is not null && sameEndpoint && current.EnrolledWithApiKey == fingerprint)
            {
                using var existing = MachineKey.FromPem(transaction.LoadEnrollmentKeyPem()
                    ?? throw new ServerBindingException("The enrolled runner has no persisted machine key."));
                var prepared = await gate.PrepareSendAsync(destination, ct, transaction);
                DemandProvenance(current, endpoint, prepared.Binding, fingerprint, existing.Fingerprint, pending: false);
                await prepared.ConsumeAsync(destination, _ => Task.FromResult(true), ct, transaction);
                // Old, never-pinned profiles acquire provenance only at the same exact endpoint after key verification.
                current = transaction.Load();
                transaction.Save(current with { KeyFingerprint = existing.Fingerprint,
                    RunnerEnrollmentBinding = Provenance(endpoint, prepared.Binding, fingerprint, existing.Fingerprint) });
                return new EnrollmentResult(true);
            }

            // A bootstrap credential already consumed by this runner is not a fresh enrollment
            // at another endpoint, even when that server could report the same workspace or key.
            if (!sameEndpoint && current.MachineId is not null && current.EnrolledWithApiKey == fingerprint)
                throw new ServerBindingException("The runner bootstrap belongs to another endpoint; its existing identity was preserved.");

            var pending = current.PendingRunnerEnrollment;
            if (pending is not null && (!sameEndpoint || pending != fingerprint))
                throw new ServerBindingException("The runner checkpoint belongs to another bootstrap key; restore its original configuration.");
            using var key = pending is null ? MachineKey.Create()
                : MachineKey.FromPem(transaction.LoadEnrollmentKeyPem()
                    ?? throw new ServerBindingException("The runner checkpoint has no persisted machine key."));
            var initial = await gate.PrepareSendAsync(destination, ct, transaction);
            transaction.DemandEnrollmentTrust();
            if (pending is not null) DemandProvenance(current, endpoint, initial.Binding, fingerprint, key.Fingerprint, pending: true);
            if (onlyRunnerKeys && !await IsRunnerKeyAsync(context, server, apiKey, initial.Binding, transaction, ct))
                return new EnrollmentResult(false);

            // Only runner credentials need a new durable machine checkpoint. An ordinary API key
            // may update the selected account/server using its own credential and no saved identity.
            if (current.MachineId is not null || current.WorkspaceId is not null || current.Server is not null && !sameEndpoint)
                throw new ServerBindingException("New runner enrollment requires a separate private MBUS_HOME; the existing server identity was preserved.");

            await log.WriteLineAsync($"Signing this runner in with the API key from {EnvVars.ApiKey}...");
            var inventory = await AgentScanner.BuildInventoryAsync(host, null, context.Settings.Location, ct);
            current = transaction.Load();
            var checkpoint = current with
            {
                Server = CommandContext.ServerText(server), ServerBinding = initial.Binding,
                PendingRunnerEnrollment = fingerprint, KeyFingerprint = key.Fingerprint,
                RunnerEnrollmentBinding = Provenance(endpoint, initial.Binding, fingerprint, key.Fingerprint),
            };
            if (pending is null)
                checkpoint = transaction.CommitEnrollment(current, checkpoint, key.ExportPrivateKeyPem(), ct);
            else
                transaction.Save(checkpoint);

            // The key and checkpoint are durable before POST. A lost response resumes this exact key and scope.
            var (enrolled, binding) = await ServerConnection.EnrollSuppliedAsync(host, context.Store, server,
                new AuthenticationHeaderValue(AuthSchemes.ApiKey, apiKey),
                () => Task.FromResult(new MachineEnrollRequest { PublicKey = key.PublicKeyBase64,
                    Name = context.Settings.MachineName, Location = context.Settings.Location, Inventory = inventory }),
                ct, transaction, prepared =>
                {
                    transaction.DemandEnrollmentTrust();
                    DemandProvenance(checkpoint, endpoint, prepared.Binding, fingerprint, key.Fingerprint, pending: true);
                });
            var final = await gate.PrepareSendAsync(destination, ct, transaction);
            if (final.Binding != binding) throw new ServerBindingException("The runner authority changed before identity publication.");
            await final.ConsumeAsync(destination, _ => Task.FromResult(true), ct, transaction);
            await ServerConnection.PutEnrollmentInventoryAsync(host, context.Store, server, key,
                enrolled.Machine.MachineId, enrolled.Machine.WorkspaceId, inventory, binding, transaction, ct);
            current = transaction.Load();
            transaction.Save(current with
            {
                ServerBinding = binding, MachineId = enrolled.Machine.MachineId, WorkspaceId = enrolled.Machine.WorkspaceId,
                WorkspaceName = enrolled.WorkspaceName, MachineName = enrolled.Machine.Name,
                Location = enrolled.Machine.Location ?? context.Settings.Location, EnrolledAt = enrolled.Machine.EnrolledAt,
                EnrolledWithApiKey = fingerprint, PendingRunnerEnrollment = null,
            });
            return new EnrollmentResult(true, enrolled, inventory);
        }, ct);

        if (!result.Used) return null;
        var signed = reload();
        if (result.Enrolled is { } enrolledResult)
        {
            await log.WriteLineAsync($"This machine is {enrolledResult.Machine.Name}, a shared runner in the {enrolledResult.WorkspaceName} workspace."
                + (enrolledResult.Machine.PendingApproval ? " A workspace admin must approve it before it takes work." : ""));
        }
        return signed;
    }

    private static RunnerEnrollmentBinding Provenance(ServerEndpoint endpoint, ServerBindingSnapshot? binding, string apiFingerprint, string keyFingerprint) => new()
    { SelectedEndpoint = endpoint.Value, ServerBinding = binding, ApiKeyFingerprint = apiFingerprint, MachineKeyFingerprint = keyFingerprint };

    private static void DemandProvenance(CliConfig config, ServerEndpoint endpoint, ServerBindingSnapshot? currentBinding,
        string apiFingerprint, string keyFingerprint, bool pending)
    {
        if (config.Server is null || ServerEndpoint.Parse(config.Server) != endpoint
            || config.KeyFingerprint is { } expectedKey && expectedKey != keyFingerprint)
            throw new ServerBindingException("The runner identity/checkpoint differs from the selected endpoint or persisted key.");
        if (config.RunnerEnrollmentBinding is { } provenance)
        {
            if (!provenance.Matches(endpoint, currentBinding, apiFingerprint, keyFingerprint))
                throw new ServerBindingException("The runner identity/checkpoint authority or key fingerprints changed.");
        }
        else if (config.ServerBinding is not null || currentBinding is not null)
            throw new ServerBindingException("An isolated runner requires its original enrollment provenance; restore the checkpoint.");
        // Old checkpoint format is accepted only under affirmative legacy at its original endpoint.
        if (pending && config.PendingRunnerEnrollment != apiFingerprint
            || !pending && config.EnrolledWithApiKey != apiFingerprint)
            throw new ServerBindingException("The bootstrap key does not match the runner identity/checkpoint.");
    }

    private static async Task<bool> IsRunnerKeyAsync(CommandContext context, Uri server, string apiKey,
        ServerBindingSnapshot? binding, ConfigStore.ProfileSecurityTransaction transaction, CancellationToken ct)
    {
        using var gate = new ServerSendGate(context.Host, context.Store, server);
        var destination = new Uri(CliSettings.BaseAddress(server), ServerConnection.Relative(ApiRoutes.Caller));
        var prepared = await gate.PrepareSendAsync(destination, ct, transaction);
        if (prepared.Binding != binding) throw new ServerBindingException("The server authority changed before the caller probe.");
        using var client = ServerConnection.CreateAnonymousClient(context.Host, server);
        using var request = new HttpRequestMessage(HttpMethod.Get, destination);
        using var response = await prepared.ConsumeAsync(destination, async _ =>
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(AuthSchemes.ApiKey, apiKey);
            try
            {
                var answer = await client.SendAsync(request, ct);
                if ((int)answer.StatusCode is >= 300 and <= 399)
                {
                    answer.Dispose();
                    throw new ServerBindingException("Caller redirects are refused; the API key was not replayed.");
                }
                return answer;
            }
            finally { request.Headers.Authorization = null; }
        }, ct, transaction);
        if (!response.IsSuccessStatusCode) return false;
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return body.RootElement.TryGetProperty("scopes", out var scopes) && scopes.ValueKind == JsonValueKind.Array
            && scopes.EnumerateArray().Any(s => s.GetString() == ApiKeyScopes.Runner);
    }
}
