using System.Text.Json;
using MessageBus.Cli.Server;

namespace MessageBus.Cli.Configuration;

internal sealed record ServerBindingLedger
{
    public int SchemaVersion { get; init; }
    public Dictionary<string, ServerBindingSnapshot> Bindings { get; init; } = new(StringComparer.Ordinal);
}

public sealed partial class ConfigStore
{
    /// <summary>Independent origin-keyed ever-isolated ledger.</summary>
    public string ServerBindingsPath => Path.Combine(Directory, "server-bindings.json");
    private string BindingTransitionPath => Path.Combine(Directory, "server-bindings.pending");
    private readonly AsyncLocal<ProfileSecurityTransaction?> _securityTransaction = new();

    // Test-only fault injection at durable boundaries, never a production bypass.
    internal Action<string>? BindingWriteBoundary { get; set; }

    /// <summary>A lock-held context. Do not retain it or run concurrent work through it.</summary>
    public sealed class ProfileSecurityTransaction
    {
        private readonly ConfigStore _store;
        internal bool Active { get; set; } = true;
        internal ProfileSecurityTransaction(ConfigStore store) => _store = store;

        private void RequireActive()
        {
            if (!Active || !ReferenceEquals(_store._securityTransaction.Value, this))
                throw new InvalidOperationException("The profile security transaction is no longer held.");
        }

        /// <summary>Rereads ledger and every surviving snapshot; corruption or missing-ledger evidence refuses.</summary>
        public ServerBindingFloor LoadBindingFloor()
        {
            RequireActive();
            return _store.ReadBindingState().Floor;
        }

        /// <summary>
        /// Validates current discovery and durably pins before returning a matching snapshot for new credentials.
        /// Existing credentials at this exact endpoint receive snapshots before return. This does not authorize a send.
        /// </summary>
        public ServerBindingSnapshot? PrepareBinding(ServerEndpoint selected, ServerDiscovery discovery,
            ServerBindingExpectation? expected = null)
        {
            RequireActive();
            return _store.PrepareBindingLocked(selected, discovery, expected);
        }

        /// <summary>
        /// Validates trusted provisioning and explicitly supplied durable home before any future caller probe.
        /// Writability is verified with a flushed owner-only file; durability of a container mount remains a deployment requirement.
        /// </summary>
        public ServerBindingExpectation ValidateProvisioning(ServerEndpoint selected, string expectedJson, string durableHome)
        {
            RequireActive();
            if (string.IsNullOrWhiteSpace(durableHome) || !Path.IsPathFullyQualified(durableHome)
                || !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(durableHome)),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(_store.Directory)),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new ServerBindingException("Isolated provisioning requires the selected explicit durable MBUS_HOME.");
            var expected = ServerBindingExpectation.Parse(expectedJson);
            var state = _store.ReadBindingState();
            if (selected.Origin != expected.TransportOrigin
                || state.Floor.Bindings.Any(b => b.TransportOrigin == selected.Origin && b.Binding != expected.Binding))
                throw new ServerBindingException("Provisioned binding conflicts with the selected origin or saved floor.");
            DurableProfileFile.RestrictDirectory(_store.Directory);
            var probe = Path.Combine(_store.Directory, ".binding-write-probe-" + Guid.NewGuid().ToString("N"));
            try { _store.WriteOwnerOnly(probe, "durable-profile-probe"); }
            finally { if (File.Exists(probe)) { File.Delete(probe); DurableProfileFile.FlushDirectory(_store.Directory); } }
            return expected;
        }
    }

    private T InSecurityTransaction<T>(Func<ProfileSecurityTransaction, T> body)
    {
        if (_securityTransaction.Value is { Active: true } context) return body(context);
        return WithProfileSecurityLockAsync(context => Task.FromResult(body(context)), CancellationToken.None).GetAwaiter().GetResult();
    }

    private sealed record BindingState(CliConfig Config, CliSessionFile? Session, ServerBindingLedger Ledger, ServerBindingFloor Floor);

    private BindingState ReadBindingState()
    {
        try
        {
            if (ReadOptionalProfileFile(BindingTransitionPath) is not null)
                throw new ServerBindingException("An interrupted binding transition requires profile restoration; credentials refused.");
            var config = ReadConfig();
            var session = ReadSession();
            var ledgerJson = ReadOptionalProfileFile(ServerBindingsPath);
            var ledgerExists = ledgerJson is not null;
            var ledger = new ServerBindingLedger { SchemaVersion = 1 };
            if (ledgerExists)
            {
                var json = ledgerJson!;
                using var document = JsonDocument.Parse(json);
                ValidateObject(document.RootElement, ["schemaVersion", "bindings"]);
                if (!document.RootElement.TryGetProperty("bindings", out var entries) || entries.ValueKind != JsonValueKind.Object)
                    throw new ServerBindingException("Missing binding ledger entries.");
                ValidateObject(entries);
                foreach (var entry in entries.EnumerateObject()) ValidateSnapshotJson(entry.Value);
                ledger = JsonSerializer.Deserialize(json, CliJsonContext.Default.ServerBindingLedger)
                    ?? throw new ServerBindingException("Empty binding ledger.");
                if (ledger.SchemaVersion != 1 || ledger.Bindings is null)
                    throw new ServerBindingException("Unsupported binding ledger schema.");
                foreach (var (origin, snapshot) in ledger.Bindings)
                {
                    if (snapshot is null || origin != snapshot.Endpoint.Origin)
                        throw new ServerBindingException("Invalid binding ledger origin.");
                }
            }
            var evidence = ledger.Bindings.Select(e => ServerBindingExpectation.Create(e.Key, e.Value.Binding)).ToList();
            foreach (var snapshot in new[] { config.ServerBinding, session?.ServerBinding }.OfType<ServerBindingSnapshot>())
            {
                if (!ledgerExists || !ledger.Bindings.TryGetValue(snapshot.Endpoint.Origin, out var pin) || pin.Binding != snapshot.Binding)
                    throw new ServerBindingException("Saved binding has no matching ledger; restore the profile before using credentials.");
                evidence.Add(ServerBindingExpectation.Create(snapshot.Endpoint.Origin, snapshot.Binding));
            }
            CheckSnapshotScope(config.ServerBinding, config.Server);
            CheckSnapshotScope(session?.ServerBinding, session?.Server);
            return new(config, session, ledger, ServerBindingFloor.FromCompleteState(evidence));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        { throw new ServerBindingException("Binding state cannot be completely read; credentials refused.", ex); }
    }

    private static void CheckSnapshotScope(ServerBindingSnapshot? snapshot, string? server)
    {
        if (snapshot is not null && server is not null && snapshot.Endpoint != ServerEndpoint.Parse(server))
            throw new ServerBindingException("Saved credentials and binding endpoint disagree.");
    }

    private static ServerBindingSnapshot? MergeSnapshot(ServerBindingSnapshot? previous, ServerBindingSnapshot? supplied,
        string? server, ServerBindingLedger ledger)
    {
        CheckSnapshotScope(supplied, server);
        if (supplied is not null)
        {
            if (!ledger.Bindings.TryGetValue(supplied.Endpoint.Origin, out var entry) || entry.Binding != supplied.Binding)
                throw new ServerBindingException("Credentials cannot be saved before their binding ledger.");
            return supplied;
        }
        if (server is null) return previous; // Local logout/reset must not erase surviving evidence.
        // Never impose new endpoint parsing rules on old, never-pinned settings.
        if (previous is null && ledger.Bindings.Count == 0) return null;
        var endpoint = ServerEndpoint.Parse(server);
        if (previous?.Endpoint == endpoint) return previous;
        return ledger.Bindings.TryGetValue(endpoint.Origin, out var pin)
            ? ServerBindingSnapshot.Create(endpoint, pin.Binding) : null;
    }

    private ServerBindingSnapshot? PrepareBindingLocked(ServerEndpoint selected, ServerDiscovery discovery, ServerBindingExpectation? expected)
    {
        var state = ReadBindingState();
        var decision = ServerBindingPolicy.Evaluate(selected, discovery, state.Floor, expected);
        if (decision.Binding is not { } binding) return null;
        var snapshot = ServerBindingSnapshot.Create(selected, binding);
        var entries = new Dictionary<string, ServerBindingSnapshot>(state.Ledger.Bindings, StringComparer.Ordinal)
        { [selected.Origin] = snapshot };
        var ledger = new ServerBindingLedger { SchemaVersion = 1, Bindings = entries };
        DurableProfileFile.RestrictDirectory(Directory);
        BindingWriteBoundary?.Invoke("before-marker");
        WriteOwnerOnly(BindingTransitionPath, "binding-transition-v1");
        BindingWriteBoundary?.Invoke("after-marker");
        WriteOwnerOnly(ServerBindingsPath, JsonSerializer.Serialize(ledger, CliJsonContext.Default.ServerBindingLedger));
        BindingWriteBoundary?.Invoke("after-ledger");
        if (state.Config.Server is { } configServer && ServerEndpoint.Parse(configServer) == selected)
            WriteOwnerOnly(ConfigPath, JsonSerializer.Serialize(state.Config with { ServerBinding = snapshot }, CliJsonContext.Default.CliConfig));
        BindingWriteBoundary?.Invoke("after-config");
        if (state.Session is { } session && ServerEndpoint.Parse(session.Server) == selected)
            WriteOwnerOnly(SessionPath, JsonSerializer.Serialize(session with { ServerBinding = snapshot }, CliJsonContext.Default.CliSessionFile));
        BindingWriteBoundary?.Invoke("after-session");
        File.Delete(BindingTransitionPath);
        DurableProfileFile.FlushDirectory(Directory);
        BindingWriteBoundary?.Invoke("after-commit");
        return snapshot;
    }

    private static void ValidateSavedJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        ValidateObject(document.RootElement);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, "serverBinding", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Name != "serverBinding") throw new ServerBindingException("Ambiguous saved binding field.");
                ValidateSnapshotJson(property.Value);
            }
        }
    }

    // File.Exists hides access errors; only an actual missing name is evidence of absent state.
    private static string? ReadOptionalProfileFile(string path)
    {
        try { return File.ReadAllText(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void ValidateSnapshotJson(JsonElement snapshot) => ValidateObject(snapshot,
        ["version", "selectedEndpoint", "stampId", "authorityVersion", "issuer", "audience", "minimumMachineSignatureVersion", "machineSignatureRequired"]);

    private static void ValidateObject(JsonElement value, string[]? exactFields = null)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ServerBindingException("Saved security state must be an object.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (!seen.Add(property.Name) || exactFields is not null && !exactFields.Contains(property.Name, StringComparer.Ordinal))
                throw new ServerBindingException("Duplicate, unknown or ambiguously cased saved security field.");
        }
        if (exactFields is not null && seen.Count != exactFields.Length)
            throw new ServerBindingException("Incomplete saved security state.");
    }
}
