using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MessageBus.Cli.Runner.Service;

/// <summary>
/// The agent sign-in variables <c>mbus runner install</c> keeps for the runner service (the original issue). A service starts without
/// the installing shell's variables (launchd with almost none, Task Scheduler with the user's saved ones), so a token set
/// in that shell, such as <c>claude setup-token</c>'s, would never reach the agents. The values can't go into the
/// service definition: the launchd plist is readable by every user of the Mac. They are kept in mbus's settings folder
/// instead, readable only by this user: an owner-only (<c>600</c>) file on macOS and Linux, like the machine key on
/// Linux, and DPAPI-protected for this Windows account, like the machine key on Windows.
/// </summary>
/// <remarks>
/// Not the macOS Keychain: the problem this solves is a runner that can't use the login keychain (Claude Code's own
/// token is there), and a locked keychain would hide this item from the service the same way. The runner reads the file
/// for each run and gives the values only to the agent process (<see cref="AgentRun"/>), never to the setup command,
/// the checkout or the runner's other children; the macOS run sandbox keeps runs out of the settings folder.
/// </remarks>
public sealed class AgentEnvironmentStore
{
    /// <summary>The variables carried into the service: the agents' sign-ins mbus recognises (<see cref="MessageBus.Cli.Agents.AgentScanner"/>).</summary>
    /// <remarks>Not <c>GH_TOKEN</c> or <c>GITHUB_TOKEN</c>: those reach repositories, and runs get their own per-run credential (the original issue).</remarks>
    public static readonly IReadOnlyList<string> Names =
        [MessageBus.Cli.Agents.SignInChecks.ClaudeTokenVariable, "ANTHROPIC_API_KEY", "OPENAI_API_KEY", "CODEX_API_KEY", "COPILOT_GITHUB_TOKEN", "GEMINI_API_KEY"];

    /// <summary>macOS and Linux: JSON, owner-only.</summary>
    public const string FileName = "agent-env";

    /// <summary>Windows: the same JSON, DPAPI-protected for this user.</summary>
    public const string ProtectedFileName = "agent-env.dpapi";

    // Ties the blob to this use, so another program's DPAPI blob can't be swapped in for it.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("messagebus.ai mbus agent environment v1");
    private readonly Action<string, byte[]> _writeOwnerOnly;

    /// <param name="directory">mbus's settings folder.</param>
    /// <param name="writeOwnerOnly">Writes a file readable only by this user, in one step (<see cref="Configuration.ConfigStore"/>).</param>
    public AgentEnvironmentStore(string directory, Action<string, byte[]> writeOwnerOnly)
    {
        _writeOwnerOnly = writeOwnerOnly;
        Path = System.IO.Path.Combine(directory, OperatingSystem.IsWindows() ? ProtectedFileName : FileName);
    }

    /// <summary>Where they are kept.</summary>
    public string Path { get; }

    /// <summary>The stored variables (none when there is no file, or it can't be read, e.g. inside a run's sandbox).</summary>
    public IReadOnlyDictionary<string, string> Load() => Read(Path);

    /// <summary>Replaces what is stored with <paramref name="variables"/>.</summary>
    public void Save(IReadOnlyDictionary<string, string> variables)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            variables.Where(v => v.Value.Length > 0).OrderBy(v => v.Key, StringComparer.Ordinal).ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal),
            Configuration.CliJsonContext.Default.DictionaryStringString);
        if (OperatingSystem.IsWindows())
        {
            json = ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
        }
        _writeOwnerOnly(Path, json);
    }

    /// <summary>Deletes them; true when there were some.</summary>
    public bool Delete()
    {
        if (!File.Exists(Path))
        {
            return false;
        }
        File.Delete(Path);
        return true;
    }

    /// <summary>The variables from <see cref="Names"/> that are set in <paramref name="env"/> (the installing shell).</summary>
    public static Dictionary<string, string> FromShell(Func<string, string?> env)
    {
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Names)
        {
            if (env(name)?.Trim() is { Length: > 0 } value)
            {
                found[name] = value;
            }
        }
        return found;
    }

    /// <summary>The stored variables in the settings folder <paramref name="directory"/>, for code that only reads them.</summary>
    public static IReadOnlyDictionary<string, string> ReadFrom(string directory) =>
        Read(System.IO.Path.Combine(directory, OperatingSystem.IsWindows() ? ProtectedFileName : FileName));

    private static Dictionary<string, string> Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }
            var bytes = File.ReadAllBytes(path);
            if (OperatingSystem.IsWindows())
            {
                bytes = ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser);
            }
            var stored = JsonSerializer.Deserialize(bytes, Configuration.CliJsonContext.Default.DictionaryStringString) ?? [];
            // Only the known names: the file can't be used to hand an agent anything else.
            return stored.Where(v => Names.Contains(v.Key, StringComparer.Ordinal) && v.Value is { Length: > 0 })
                .ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            return [];
        }
    }
}
