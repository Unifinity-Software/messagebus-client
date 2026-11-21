using System.Text.Json;
using System.Text.Json.Serialization;

namespace MessageBus.Cli.Configuration;

/// <summary>
/// What <c>mbus login</c> stores in <c>~/.mbus/config.json</c>. The machine's private key is in its
/// own file next to it (<see cref="MachineKeyFile"/>); both are readable by the owner only.
/// </summary>
public sealed record CliConfig
{
    /// <summary>Exact endpoint and isolated floor for this identity, including a pending runner enrollment.</summary>
    public Server.ServerBindingSnapshot? ServerBinding { get; init; }

    /// <summary>The persisted machine key store: file or keychain; null uses the platform default.</summary>
    public string? KeyStore { get; init; }

    /// <summary>The default server URL (no trailing slash).</summary>
    public string? Server { get; init; }

    /// <summary>The enrolled machine (<c>mch_…</c>); null until enrolled.</summary>
    public string? MachineId { get; init; }

    /// <summary>The workspace the machine is enrolled in.</summary>
    public string? WorkspaceId { get; init; }

    /// <summary>The workspace's display name, for messages.</summary>
    public string? WorkspaceName { get; init; }

    /// <summary>The machine's display name.</summary>
    public string? MachineName { get; init; }

    /// <summary>The machine's Location label.</summary>
    public string? Location { get; init; }

    /// <summary>SHA-256 of the machine's public key, lowercase hex.</summary>
    public string? KeyFingerprint { get; init; }

    /// <summary>When the machine enrolled.</summary>
    public DateTimeOffset? EnrolledAt { get; init; }

    /// <summary>
    /// The runner API key this machine enrolled itself with (the original issue), as <see cref="Runner.RunnerKeyEnrollment.Fingerprint"/>:
    /// while <c>MBUS_API_KEY</c> holds that key, mbus signs as the machine and never sends the key.
    /// </summary>
    public string? EnrolledWithApiKey { get; init; }

    /// <summary>Bootstrap checkpoint: the runner API-key fingerprint whose pre-saved machine key must be reused until enrollment completes.</summary>
    public string? PendingRunnerEnrollment { get; init; }

    /// <summary>The settings file's name.</summary>
    public const string FileName = "config.json";

    /// <summary>The machine private key's file name (PKCS#8 PEM, P-256) where there is no OS key store (the original issue).</summary>
    public const string MachineKeyFile = "machine-key.pem";

    /// <summary>Windows: the same key, encrypted with DPAPI for this user (the original issue).</summary>
    public const string ProtectedMachineKeyFile = "machine-key.dpapi";
}

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CliConfig))]
[JsonSerializable(typeof(Server.ServerInfoResponse))]
[JsonSerializable(typeof(CliSessionFile))]
[JsonSerializable(typeof(Server.ServerBindingSnapshot))]
[JsonSerializable(typeof(ServerBindingLedger))]
[JsonSerializable(typeof(SignIn.CliStartBody))]
[JsonSerializable(typeof(SignIn.CliStartResponse))]
[JsonSerializable(typeof(SignIn.CliTokenBody))]
[JsonSerializable(typeof(SignIn.CliPollResponse))]
[JsonSerializable(typeof(SignIn.CliRefreshBody))]
[JsonSerializable(typeof(SignIn.CliRefreshResponse))]
[JsonSerializable(typeof(Dictionary<string, string>))] // the runner service's agent sign-ins (the original issue)
internal sealed partial class CliJsonContext : JsonSerializerContext;
