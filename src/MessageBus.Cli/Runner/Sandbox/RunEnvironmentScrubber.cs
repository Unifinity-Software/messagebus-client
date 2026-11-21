using System.Collections;

namespace MessageBus.Cli.Runner.Sandbox;

/// <summary>
/// Which of the runner's environment variables a run inherits: an allow-list of what programs need
/// to work (paths, locale, proxies, toolchain locations) and what the agent needs to reach its model
/// provider. Everything else (repository tokens, cloud keys, SSH agent, this machine's mbus sign-in and
/// other MBUS_* settings) is removed, so a secret in the runner's environment doesn't reach the agent or
/// the code it runs.
/// </summary>
public static class RunEnvironmentScrubber
{
    private static readonly string[] Names =
    [
        // Every platform.
        "PATH", "HOME", "USER", "LOGNAME", "SHELL", "TERM", "COLORTERM", "LANG", "LANGUAGE", "TZ", "NO_COLOR", "FORCE_COLOR",
        // macOS and Linux.
        "__CF_USER_TEXT_ENCODING", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME", "XDG_STATE_HOME", "XDG_RUNTIME_DIR",
        "XDG_DATA_DIRS", "XDG_CONFIG_DIRS", "MANPATH", "INFOPATH",
        // Windows.
        "PATHEXT", "SystemRoot", "SystemDrive", "windir", "ComSpec", "OS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER",
        "PROCESSOR_LEVEL", "PROCESSOR_REVISION", "NUMBER_OF_PROCESSORS", "USERNAME", "USERDOMAIN", "USERDOMAIN_ROAMINGPROFILE",
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "ProgramData", "ProgramFiles", "ProgramFiles(x86)",
        "ProgramW6432", "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432", "PUBLIC", "ALLUSERSPROFILE",
        "COMPUTERNAME", "PSModulePath", "DriverData",
        // Toolchain locations.
        "NUGET_PACKAGES", "JAVA_HOME", "GOPATH", "GOROOT", "CARGO_HOME", "RUSTUP_HOME", "NVM_DIR", "NVM_BIN", "NVM_INC",
        "VOLTA_HOME", "BUN_INSTALL", "PNPM_HOME", "PYENV_ROOT", "HOMEBREW_PREFIX", "HOMEBREW_CELLAR", "HOMEBREW_REPOSITORY",
        // Certificates and proxies (corporate networks).
        "NODE_EXTRA_CA_CERTS", "SSL_CERT_FILE", "SSL_CERT_DIR", "REQUESTS_CA_BUNDLE", "CURL_CA_BUNDLE",
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY", "ALL_PROXY",
        // The agent's model provider settings (its own sign-in otherwise lives in its settings or the keychain).
        "CLAUDE_CONFIG_DIR",
        // The server's address. Nothing that signs in as this machine (MBUS_API_KEY, MBUS_HOME with its
        // key, the one-time MBUS_ENROLL_TOKEN): the runner serves each run's mbus tools itself, with a
        // credential for that run alone (the original issue).
        "MBUS_SERVER",
    ];

    private static readonly string[] Prefixes = ["LC_", "DOTNET_", "ANTHROPIC_", "CLAUDE_CODE_", "OPENAI_", "CODEX_", "OPENCODE_", "COPILOT_"];

    /// <summary>Whether a run inherits <paramref name="name"/> from the runner.</summary>
    /// <param name="name">The variable.</param>
    /// <param name="extra">Names the machine's owner also lets through (<see cref="SandboxEnvVars.PassEnvironment"/>).</param>
    public static bool IsAllowed(string name, IReadOnlyCollection<string> extra)
    {
        // Windows names are case-insensitive; proxy variables are lower-case as often as not.
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (MessageBus.Cli.Agents.CodexAuthentication.IsKeyVariable(name)) return false;
        return Names.Any(n => string.Equals(n, name, n.EndsWith("_PROXY", StringComparison.Ordinal) ? StringComparison.OrdinalIgnoreCase : comparison))
            || Prefixes.Any(p => name.StartsWith(p, comparison))
            || extra.Any(n => string.Equals(n, name, comparison));
    }

    /// <summary>
    /// The variables to give a launch, in <see cref="ProcessLaunch.Environment"/>'s terms: every
    /// inherited variable that isn't allowed is removed (null), then <paramref name="set"/> is applied.
    /// </summary>
    /// <param name="inherited">The runner's environment (what a started process would inherit).</param>
    /// <param name="extra">Names the machine's owner also lets through.</param>
    /// <param name="set">The run's own variables (the launch's, then the sandbox's).</param>
    public static Dictionary<string, string?> Build(IDictionary inherited, IReadOnlyCollection<string> extra, IEnumerable<KeyValuePair<string, string?>> set)
    {
        var result = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in inherited)
        {
            if (entry.Key is string name && !IsAllowed(name, extra))
            {
                result[name] = null;
            }
        }
        foreach (var (name, value) in set)
        {
            result[name] = value;
        }
        return result;
    }
}
