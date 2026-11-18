using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;

namespace MessageBus.Mcp;

/// <summary>One role or workspace key as <c>mbus keys list</c> shows it.</summary>
/// <param name="Name">What a person types: the role, or <c>workspace</c>.</param>
/// <param name="State">The service's view: the current version and whether it must be replaced (the original issue).</param>
/// <param name="Trust">How it compares with what this computer accepted (the original issue).</param>
/// <param name="HeldHere">Whether this computer holds the private key.</param>
public sealed record KeyListing(string Name, KeyRotationState State, TrustCheck Trust, bool HeldHere);

/// <summary>An admin computer whose signatures this computer accepts.</summary>
/// <param name="MachineId">The machine.</param>
/// <param name="Fingerprint">The device key fingerprint this computer accepted.</param>
/// <param name="Trust">How the key the service shows for it now compares.</param>
public sealed record SignerListing(string MachineId, string Fingerprint, TrustCheck Trust);

/// <summary>A signing device key and its enrolled computer's display name.</summary>
public sealed record AdminComputer(PublicKeyInfo Key, string? Name);

/// <summary>
/// This machine's keys for end-to-end encryption (the original issue), and the key hand-out decided for the MVP: the
/// SENDER makes sure the recipient has a key. A role (or the workspace) without a key gets one made
/// here, signed with this machine's device key, published (public half only) and granted to every machine
/// that should read it, this one included so it can grant again later; each later send grants it to
/// holders that joined since. When the service says a key must be replaced because a machine that had it
/// left the role or was removed (the original issue), an admin's machine replaces it before sealing anything to it, and
/// anyone else's refuses to send. Every key this machine seals to or takes from a grant is checked against
/// the fingerprints it accepted before (<see cref="KeyTrust"/>, the original issue).
/// Receiving, the machine's grants are fetched at start and again whenever a key is missing, and the
/// role keys they carry go into <see cref="Ring"/>. The device key comes from mbus's key store (the original issue), written by
/// <c>mbus login</c>; private keys never leave this process.
/// </summary>
/// <param name="machineKeyPem">Reads the device key's PEM (mbus's key store: DPAPI, the Keychain or the file, the original issue); null reads <see cref="MachineKeyFile"/>.</param>
public sealed class MachineKeys(HttpClient http, string workspaceId, string? machineId, string configDirectory, TextWriter log, TimeProvider time,
    Func<string?>? machineKeyPem = null) : IDisposable
{
    /// <summary>Where <c>mbus login</c> keeps the device key (the original issue's <c>CliConfig.MachineKeyFile</c>).</summary>
    public const string MachineKeyFile = "machine-key.pem";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<(string KeyId, string MachineId)> _granted = [];
    private readonly Dictionary<string, string> _refused = new(StringComparer.Ordinal);
    private KeyDirectory _directory = new([]);
    private KeyPair? _machineKey;
    private PublicKeyInfo? _machineKeyInfo;
    private string? _subjectId;
    private bool _canManageKeys;
    private bool _started;

    public KeyRing Ring { get; } = new();

    public string WorkspaceId => workspaceId;

    /// <summary>The fingerprint derived from this computer's own local device key, independently of the service.</summary>
    public string? DeviceFingerprint => _machineKey?.PublicKey.Fingerprint;

    /// <summary>Whether this machine can make, replace and grant keys (an admin's own enrolled computer).</summary>
    public bool CanManageKeys => _canManageKeys && _machineKeyInfo is not null && _machineKey is not null && _subjectId is not null && machineId is not null;

    private string Route(string path) => $"api/w/{Uri.EscapeDataString(workspaceId)}/{path}";

    /// <summary>Loads the device key, the public keys and this machine's grants (once).</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (_started) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (_started) return;
            var caller = await GetNodeAsync("api/caller", ct);
            _subjectId = (string?)caller?["subjectId"];
            _canManageKeys = caller?["permissions"]?.AsArray().Any(p => (string?)p == PermissionNames.KeysManage) == true;

            if (machineId is not null && ReadMachineKey() is { } pem)
                _machineKey = KeyPair.FromPem(pem);
            await RefreshDirectoryAsync(ct);
            await LoadGrantsCoreAsync(ct);
            _started = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? ReadMachineKey()
    {
        if (machineKeyPem is not null) return machineKeyPem();
        var keyFile = Path.Combine(configDirectory, MachineKeyFile);
        return File.Exists(keyFile) ? File.ReadAllText(keyFile) : null;
    }

    private async Task RefreshDirectoryAsync(CancellationToken ct)
    {
        var keys = await GetAsync<IReadOnlyList<PublicKeyInfo>>(Route("keys"), ct) ?? [];
        _directory = new KeyDirectory(keys);
        if (_machineKey is not null && machineId is not null)
        {
            _machineKeyInfo = keys.Where(k => k.Scope == KeyScope.Machine && k.Owner == machineId && k.Fingerprint == _machineKey.PublicKey.Fingerprint)
                .OrderByDescending(k => k.Version).FirstOrDefault();
            if (_machineKeyInfo is not null) Ring.Add(_machineKeyInfo.KeyId, _machineKey);
        }
    }

    /// <summary>
    /// Opens every grant sealed to this machine that isn't in the ring yet. A grant signed by an admin
    /// computer whose key changed, or for a key version whose fingerprint changed, is refused (the original issue) and
    /// remembered, so opening a message sealed to that key says why.
    /// </summary>
    private async Task LoadGrantsCoreAsync(CancellationToken ct)
    {
        if (_machineKeyInfo is null || machineId is null) return;
        var grants = await GetAsync<IReadOnlyList<KeyGrant>>(Route($"keys/grants?machineId={Uri.EscapeDataString(machineId)}"), ct) ?? [];
        var pins = KeyTrust.Load(configDirectory, workspaceId, http.BaseAddress!.AbsoluteUri);
        var changed = false;
        foreach (var grant in grants.Where(g => g.RevokedAt is null))
        {
            if (_directory.For(grant) is not { } granted || Ring.Find(granted.KeyId) is not null) continue;
            if (_directory.Find(grant.SigningKeyId) is not { } signer) continue;

            var signerTrust = KeyTrust.CheckSigner(pins, signer, _machineKeyInfo.KeyId);
            var known = pins.Keys.GetValueOrDefault(grant.Role)?.FirstOrDefault(k => k.Version == granted.Version);
            var problem = signerTrust.Status == TrustStatus.Changed ? signerTrust.Problem
                : known is not null && known.Fingerprint != granted.Fingerprint
                    ? $"messagebus.ai now shows a different {KeyTrust.Describe(granted)} for version {granted.Version}: it was {KeyTrust.Short(known.Fingerprint)}, it is now {KeyTrust.Short(granted.Fingerprint)}."
                    : null;
            if (problem is not null)
            {
                _refused[granted.KeyId] = Refusal(problem, signerTrust.Status == TrustStatus.Changed ? signer.Owner! : KeyTrust.NameOf(granted));
                await log.WriteLineAsync($"mbus mcp: not using key grant {grant.GrantId}: {problem}");
                continue;
            }
            try
            {
                Ring.Add(granted.KeyId, GrantCrypto.Open(grant, workspaceId, granted, signer, Ring));
                _granted.Add((granted.KeyId, machineId));
                _refused.Remove(granted.KeyId);
                // The grant is signed by an admin computer this machine trusts and holds the key the service lists: both are vouched for.
                KeyTrust.Accept(pins, granted, signer);
                changed = true;
            }
            catch (CryptographicException e)
            {
                await log.WriteLineAsync($"mbus mcp: ignoring key grant {grant.GrantId}: {e.Message}");
            }
        }
        if (changed) KeyTrust.Save(configDirectory, workspaceId, pins, http.BaseAddress!.AbsoluteUri);
    }

    /// <summary>Fetches keys and grants again (a key turned up missing).</summary>
    public async Task ReloadAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await RefreshDirectoryAsync(ct);
            await LoadGrantsCoreAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Opens a sealed body, fetching grants again once if the key isn't here yet.</summary>
    public async Task<MessageBody> OpenAsync(byte[] ciphertext, EncryptionInfo encryption, CancellationToken ct)
    {
        await StartAsync(ct);
        try
        {
            return BodyCrypto.Open(ciphertext, encryption, Ring);
        }
        catch (MissingKeyException e)
        {
            await ReloadAsync(ct);
            ThrowIfRefused(e.KeyId);
            return BodyCrypto.Open(ciphertext, encryption, Ring);
        }
    }

    /// <summary>Runs <paramref name="open"/> with the ring, fetching grants again once if a key is missing.</summary>
    public async Task<T> WithRingAsync<T>(Func<IKeyRing, Task<T>> open, CancellationToken ct)
    {
        await StartAsync(ct);
        try
        {
            return await open(Ring);
        }
        catch (MissingKeyException e)
        {
            await ReloadAsync(ct);
            ThrowIfRefused(e.KeyId);
            return await open(Ring);
        }
    }

    /// <summary>A fresh key check for the runner's inventory; never accepts a changed fingerprint.</summary>
    public async Task<IReadOnlyDictionary<string, bool>> RoleReadinessAsync(CancellationToken ct)
    {
        if (_started) await ReloadAsync(ct);
        else await StartAsync(ct);
        return _directory.All.Where(k => k.Scope == KeyScope.Role && k.Owner is not null && k.RetiredAt is null)
            .GroupBy(k => k.Owner!).Select(g => g.OrderByDescending(k => k.Version).First())
            .Where(k => Ring.Find(k.KeyId) is not null || _refused.ContainsKey(k.KeyId))
            .ToDictionary(k => k.Owner!, k => Ring.Find(k.KeyId) is not null);
    }

    private void ThrowIfRefused(string keyId)
    {
        if (Ring.Find(keyId) is null && _refused.TryGetValue(keyId, out var why))
            throw new McpToolException(new McpError
            {
                Code = McpErrorCodes.Forbidden, Message = why,
                Field = _directory.Find(keyId) is { Scope: KeyScope.Role, Owner: { } role } ? RoleKeyReadiness.FieldPrefix + role : null,
            });
    }

    /// <summary>
    /// The key to seal for <paramref name="target"/>: a role's key, or the workspace key for any other
    /// target. Makes the key if there is none, replaces it first if a machine that had it may no longer
    /// read it (an admin's machine; others are refused), checks it against the fingerprints this machine
    /// accepted, and grants it to the machines that should hold it where they don't have it yet.
    /// </summary>
    /// <param name="target">The message's target.</param>
    /// <param name="ct">Cancels.</param>
    public async Task<PublicKeyInfo> RecipientAsync(MessageTarget? target, CancellationToken ct)
    {
        await StartAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            var role = (target as RoleTarget)?.Role;
            var scope = role is null ? KeyScope.Workspace : KeyScope.Role;
            var state = await StateAsync(scope, role, ct);
            PublicKeyInfo key;
            if (state.Key is null)
            {
                key = await CreateAsync(scope, role, ct);
            }
            else if (state.RotationNeeded)
            {
                if (!CanManageKeys) throw McpToolException.Forbidden(RotationNeededMessage(state));
                key = await RotateCoreAsync(state, ct);
            }
            else
            {
                key = state.Key;
                await CheckTrustAsync(key);
            }
            await GrantAsync(key, state.Holders, ct);
            return key;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Replaces a role's (or the workspace's) key now (<c>mbus keys rotate</c>, the original issue): revokes the grants of
    /// machines that may no longer hold it, makes and publishes the next version, and grants it to the
    /// machines that should hold it and to this one. Needs an admin's machine.
    /// </summary>
    /// <param name="role">The role; null for the workspace key.</param>
    /// <param name="ct">Cancels.</param>
    /// <returns>The new version, and the machines that lost access.</returns>
    public async Task<(PublicKeyInfo Key, IReadOnlyList<KeyAccessLossInfo> Removed)> RotateAsync(string? role, CancellationToken ct)
    {
        await StartAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            if (!CanManageKeys)
                throw McpToolException.Forbidden("Only a workspace admin's own enrolled computer can replace keys (a shared runner can't).");
            var state = await StateAsync(role is null ? KeyScope.Workspace : KeyScope.Role, role, ct);
            var key = state.Key is null ? await CreateAsync(state.Scope, role, ct) : await RotateCoreAsync(state, ct);
            await GrantAsync(key, state.Holders, ct);
            return (key, state.MustLoseAccess);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Every role and workspace key, with the service's rotation state and this machine's trust in it (<c>mbus keys list</c>).</summary>
    public async Task<(IReadOnlyList<KeyListing> Keys, IReadOnlyList<SignerListing> Signers)> ListAsync(CancellationToken ct)
    {
        await StartAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            await RefreshDirectoryAsync(ct);
            var states = await GetAsync<IReadOnlyList<KeyRotationState>>(Route("keys/rotation"), ct) ?? [];
            var pins = KeyTrust.Load(configDirectory, workspaceId, http.BaseAddress!.AbsoluteUri);
            var keys = states.Where(s => s.Key is not null)
                .Select(s => new KeyListing(KeyTrust.NameOf(s.Key!), s, KeyTrust.Check(pins, s.Key!, _directory, workspaceId, _machineKeyInfo?.KeyId),
                    Ring.Find(s.Key!.KeyId) is not null))
                .ToList();
            var signers = pins.Signers.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new SignerListing(p.Key, p.Value.Fingerprint, _directory.Current(KeyScope.Machine, p.Key) is { } now
                    ? KeyTrust.CheckSigner(pins, now, _machineKeyInfo?.KeyId)
                    : new TrustCheck(TrustStatus.Trusted)))
                .ToList();
            return (keys, signers);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The enrolled personal computers that validly signed the workspace's current keys.</summary>
    public async Task<IReadOnlyList<AdminComputer>> AdminComputersAsync(CancellationToken ct)
    {
        var machines = await GetNodeAsync(Route("machines"), ct);
        await StartAsync(ct);
        var enrolled = machines!.AsArray().Where(m => (string?)m?["kind"] == "personal" && (bool?)m?["pendingApproval"] != true)
            .Where(m => (string?)m?["machineId"] is not null)
            .ToDictionary(m => (string)m!["machineId"]!, m => (string?)m?["name"], StringComparer.Ordinal);
        var signers = _directory.All.Where(k => k.Scope != KeyScope.Machine && k.RetiredAt is null)
            .Select(k => KeyTrust.SignerOf(k, _directory, workspaceId)).OfType<PublicKeyInfo>().ToList();
        if (signers.Count > 0 && signers.All(k => k.RetiredAt is not null))
            throw McpToolException.NotFound("The workspace's keys were signed by computer keys that are no longer in use. Ask an admin to replace each key (mbus keys rotate <role-or-workspace>), then run mbus keys trust --admins.");
        return signers
            .Where(k => k.RetiredAt is null && k.Owner is not null && enrolled.ContainsKey(k.Owner)).DistinctBy(k => k.Owner)
            .Select(k => new AdminComputer(k, enrolled[k.Owner!])).ToList();
    }

    /// <summary>The current device key to compare before trusting a computer.</summary>
    public async Task<PublicKeyInfo> ComputerKeyAsync(string name, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await RefreshDirectoryAsync(ct);
            return _directory.Current(KeyScope.Machine, name)
                ?? throw McpToolException.NotFound($"No computer {name} has a current key in this workspace.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A person checked the fingerprint and accepts the key the service shows now (<c>mbus keys trust</c>):
    /// a role, <c>workspace</c>, or an admin computer's machine id (its current device key).
    /// </summary>
    /// <returns>The key accepted (a machine key for a machine id).</returns>
    public Task<PublicKeyInfo> TrustAsync(string name, CancellationToken ct) => TrustAsync(name, null, null, ct);

    /// <summary>Accepts a key only if it still matches the fingerprint the person checked.</summary>
    public async Task<PublicKeyInfo> TrustAsync(string name, string? expectedFingerprint, string? expectedKeyId, CancellationToken ct)
    {
        await StartAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            await RefreshDirectoryAsync(ct);
            var pins = KeyTrust.Load(configDirectory, workspaceId, http.BaseAddress!.AbsoluteUri);
            PublicKeyInfo accepted;
            if (name.StartsWith("mch_", StringComparison.Ordinal))
            {
                accepted = (expectedKeyId is null ? _directory.Current(KeyScope.Machine, name) : _directory.Find(expectedKeyId))
                    ?? throw McpToolException.NotFound($"No computer {name} has that key in this workspace. Run mbus keys trust --admins to check it again.");
                if (accepted.Scope != KeyScope.Machine || accepted.Owner != name || accepted.RetiredAt is not null)
                    throw McpToolException.Forbidden("The computer's key changed while you were checking it. Run mbus keys trust --admins to check it again.");
                CheckFingerprint(accepted, expectedFingerprint);
                KeyTrust.AcceptSigner(pins, accepted);
            }
            else
            {
                var scope = name == "workspace" ? KeyScope.Workspace : KeyScope.Role;
                accepted = _directory.Current(scope, scope == KeyScope.Role ? name : null)
                           ?? throw McpToolException.NotFound(scope == KeyScope.Workspace ? "The workspace has no key yet." : $"The role '{name}' has no key yet.");
                CheckFingerprint(accepted, expectedFingerprint);
                KeyTrust.Accept(pins, accepted, KeyTrust.SignerOf(accepted, _directory, workspaceId));
            }
            KeyTrust.Save(configDirectory, workspaceId, pins, http.BaseAddress!.AbsoluteUri);
            _refused.Clear(); // grants refused before are looked at again on the next reload
            return accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void CheckFingerprint(PublicKeyInfo key, string? expectedFingerprint)
    {
        if (expectedFingerprint is not null && key.Fingerprint != expectedFingerprint)
            throw McpToolException.Forbidden("The key changed while you were checking it. Run mbus keys trust --admins to check it again.");
    }

    /// <summary>
    /// The role and workspace private keys this machine holds that can still decrypt something (current and
    /// retired, not expired), for a recovery kit (<c>mbus keys recovery-kit</c>, the original issue). The ring owns them.
    /// </summary>
    public async Task<IReadOnlyList<RecoveryKitEntry>> RecoveryKeysAsync(CancellationToken ct)
    {
        await StartAsync(ct);
        await ReloadAsync(ct);
        var now = time.GetUtcNow();
        return [.. Ring.KeyIds
            .Select(id => (Info: _directory.Find(id), Key: Ring.Find(id)))
            .Where(k => k.Info is { Scope: not KeyScope.Machine } info && (info.ExpiresAt is null || info.ExpiresAt > now) && k.Key is not null)
            .OrderBy(k => k.Info!.Scope == KeyScope.Workspace ? 0 : 1).ThenBy(k => k.Info!.Owner, StringComparer.Ordinal).ThenBy(k => k.Info!.Version)
            .Select(k => new RecoveryKitEntry(k.Info!.KeyId, k.Info.Scope, k.Info.Owner, k.Info.Version, k.Key!))];
    }

    /// <summary>Tells the service a recovery kit was made (the kit itself never leaves this machine).</summary>
    public async Task RecordRecoveryKitAsync(CancellationToken ct)
    {
        using var response = await http.PostAsync(Route("keys/recovery-kit"), content: null, ct);
        await EnsureAsync(response, "record the recovery kit", ct);
    }

    // ---- making, replacing and granting ----

    private async Task<KeyRotationState> StateAsync(KeyScope scope, string? role, CancellationToken ct)
    {
        var query = scope == KeyScope.Workspace ? "scope=workspace" : $"scope=role&owner={Uri.EscapeDataString(role!)}";
        var state = (await GetAsync<IReadOnlyList<KeyRotationState>>(Route($"keys/rotation?{query}"), ct))?.FirstOrDefault()
                    ?? new KeyRotationState { Scope = scope, Owner = role, Key = _directory.Current(scope, role) };
        if (state.Key is { } key && (_directory.Find(key.KeyId) is null || (key.SigningKeyId is { } signer && _directory.Find(signer) is null)))
            await RefreshDirectoryAsync(ct);
        return state;
    }

    /// <summary>Refuses (and explains) a key that differs from what this machine accepted; records it otherwise.</summary>
    private Task CheckTrustAsync(PublicKeyInfo key)
    {
        var pins = KeyTrust.Load(configDirectory, workspaceId, http.BaseAddress!.AbsoluteUri);
        var check = KeyTrust.Check(pins, key, _directory, workspaceId, _machineKeyInfo?.KeyId);
        switch (check.Status)
        {
            case TrustStatus.Trusted:
                return Task.CompletedTask;
            case TrustStatus.Changed:
                throw McpToolException.Forbidden(Refusal(check.Problem!, KeyTrust.NameOf(key)));
            default:
                KeyTrust.Accept(pins, key, KeyTrust.SignerOf(key, _directory, workspaceId));
                KeyTrust.Save(configDirectory, workspaceId, pins, http.BaseAddress!.AbsoluteUri);
                return log.WriteLineAsync(check.Status == TrustStatus.New
                    ? $"mbus mcp: first use of the {KeyTrust.Describe(key)} (version {key.Version}, fingerprint {KeyTrust.Short(key.Fingerprint)}); remembered."
                    : $"mbus mcp: the {KeyTrust.Describe(key)} was replaced by a trusted admin computer (version {key.Version}, fingerprint {KeyTrust.Short(key.Fingerprint)}).");
        }
    }

    private static string Refusal(string problem, string trustName) =>
        $"Stopped: {problem} This computer won't use it until someone checks it. Compare the fingerprint with the Security page or `mbus keys list` " +
        $"on an admin's computer; if it matches, run `mbus keys trust {trustName}` on this computer.";

    private static string RotationNeededMessage(KeyRotationState state)
    {
        var what = state.Scope == KeyScope.Workspace ? "workspace key" : $"{state.Owner} role's key";
        var who = string.Join(", ", state.MustLoseAccess.Select(m => m.Reason == KeyAccessLoss.MachineRemoved
            ? $"{m.MachineId} was removed from the workspace"
            : $"{m.Name ?? m.MachineId} no longer holds the role"));
        return $"Nothing was sent: the {what} has to be replaced first, because {who} but was given the current key. " +
               $"A workspace admin's mbus replaces it on its next send, or an admin can run `mbus keys rotate {(state.Scope == KeyScope.Workspace ? "workspace" : state.Owner)}`.";
    }

    private async Task<PublicKeyInfo> RotateCoreAsync(KeyRotationState state, CancellationToken ct)
    {
        var owner = state.Scope == KeyScope.Workspace ? KeyDirectory.WorkspaceOwner : state.Owner!;
        foreach (var machine in state.MustLoseAccess)
        {
            using var revoked = await http.DeleteAsync(Route($"keys/grants/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(machine.MachineId)}"), ct);
            await EnsureAsync(revoked, $"revoke {machine.MachineId}'s key grant", ct);
        }
        var key = await CreateAsync(state.Scope, state.Owner, ct);
        var lost = state.MustLoseAccess.Count > 0 ? "; " + string.Join(", ", state.MustLoseAccess.Select(m => m.MachineId)) + " can't read what is sealed to it" : "";
        await log.WriteLineAsync($"mbus mcp: replaced the {(state.Scope == KeyScope.Workspace ? "workspace key" : $"{state.Owner} role's key")} " +
                                 $"(version {state.Key?.Version} → {key.Version}){lost}.");
        return key;
    }

    private async Task<PublicKeyInfo> CreateAsync(KeyScope scope, string? role, CancellationToken ct)
    {
        var what = role is null ? "The workspace" : $"The role '{role}'";
        if (!CanManageKeys)
            throw McpToolException.Forbidden($"{what} has no encryption key yet, and only a workspace admin's enrolled machine can make one. " +
                                             "Ask an admin to send the first message to it with mbus.");
        var pair = KeyPair.GenerateP256();
        var request = new PublishKeyRequest
        {
            Scope = scope, Owner = role, PublicKey = pair.PublicKey.ToBase64(),
            SigningKeyId = _machineKeyInfo!.KeyId, Signature = KeySignatures.Sign(workspaceId, scope, role, pair.PublicKey, _machineKey!),
        };
        using var response = await http.PostAsync(Route("keys"), JsonContent.Create(request, MessageBusJson.Options.GetTypeInfo(typeof(PublishKeyRequest))), ct);
        await EnsureAsync(response, $"publish the {(role is null ? "workspace" : role)} key", ct);
        var info = MessageBusJson.Deserialize<PublicKeyInfo>(await response.Content.ReadAsStringAsync(ct))!;
        Ring.Add(info.KeyId, pair);
        await RefreshDirectoryAsync(ct);
        // Made here, so it is the key this machine trusts for that role from now on.
        var pins = KeyTrust.Load(configDirectory, workspaceId, http.BaseAddress!.AbsoluteUri);
        KeyTrust.Accept(pins, info, _machineKeyInfo);
        KeyTrust.Save(configDirectory, workspaceId, pins, http.BaseAddress!.AbsoluteUri);
        await log.WriteLineAsync($"mbus mcp: made {(role is null ? "the workspace" : $"the {role} role's")} encryption key (version {info.Version}).");
        return info;
    }

    /// <summary>Grants <paramref name="key"/> to each machine (and this one) that doesn't hold it yet. Needs the private key here.</summary>
    private async Task GrantAsync(PublicKeyInfo key, IReadOnlyList<string> machines, CancellationToken ct)
    {
        if (!CanManageKeys) return;
        if (Ring.Find(key.KeyId) is not { } privateKey) return; // someone else made it; they (or an admin) grant it
        var owner = GrantCrypto.OwnerOf(key);
        foreach (var target in machines.Append(machineId!).Distinct())
        {
            if (_granted.Contains((key.KeyId, target))) continue;
            if (_directory.Current(KeyScope.Machine, target) is not { } targetKey) continue; // hasn't registered a key
            var existing = await GetAsync<IReadOnlyList<KeyGrant>>(Route($"keys/grants?machineId={Uri.EscapeDataString(target)}"), ct) ?? [];
            if (!existing.Any(g => g.Role == owner && g.KeyVersion == key.Version && g.RevokedAt is null))
            {
                var grant = GrantCrypto.Create(workspaceId, key, privateKey, targetKey, _machineKeyInfo!, _machineKey!, _subjectId!, time.GetUtcNow());
                using var response = await http.PostAsync(Route("keys/grants"),
                    JsonContent.Create(grant, MessageBusJson.Options.GetTypeInfo(typeof(KeyGrant))), ct);
                if (!response.IsSuccessStatusCode)
                {
                    await log.WriteLineAsync($"mbus mcp: couldn't grant key {key.KeyId} to {target}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
                    continue;
                }
            }
            _granted.Add((key.KeyId, target));
        }
    }

    // ---- plumbing ----

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        await EnsureAsync(response, "read keys", ct);
        return MessageBusJson.Deserialize<T>(await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<JsonNode?> GetNodeAsync(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        await EnsureAsync(response, "check who this machine is", ct);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task EnsureAsync(HttpResponseMessage response, string what, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var text = await response.Content.ReadAsStringAsync(ct);
        var code = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => McpErrorCodes.Forbidden,
            HttpStatusCode.NotFound => McpErrorCodes.NotFound,
            HttpStatusCode.BadRequest => McpErrorCodes.InvalidInput,
            >= HttpStatusCode.InternalServerError => McpErrorCodes.Unavailable,
            _ => McpErrorCodes.Internal,
        };
        throw new McpToolException(new McpError
        {
            Code = code, Retryable = code == McpErrorCodes.Unavailable,
            Message = $"Couldn't {what} ({(int)response.StatusCode}): {Trim(text)}",
        });
    }

    private static string Trim(string text) => text.Length > 300 ? text[..300] : text;

    public void Dispose()
    {
        Ring.Dispose(); // includes the device key once it is in the ring
        if (_machineKeyInfo is null) _machineKey?.Dispose();
        _gate.Dispose();
    }
}
