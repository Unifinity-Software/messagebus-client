using System.Reflection;
using MessageBus.Cli.Connect;
using MessageBus.Cli.Mcp;
using MessageBus.Cli.SignIn;

namespace MessageBus.Cli.Hosting;

/// <summary>
/// Everything mbus takes from the machine it runs on: console streams, environment variables, the
/// home directory, the network, the clock and sign-in. <see cref="CreateDefault"/> is the real
/// machine; tests build one over temp directories and a fake server, and run the real commands
/// against it in-process. Commands never read <see cref="Environment"/> or <see cref="Console"/>
/// directly.
/// </summary>
public sealed class CliHost
{
    /// <summary>Normal output (tables, results). <c>mbus mcp</c> never writes here: stdout carries MCP.</summary>
    public required TextWriter Out { get; init; }

    /// <summary>Errors, warnings and progress.</summary>
    public required TextWriter Error { get; init; }

    /// <summary>Whether <see cref="Out"/> is a terminal a person reads (not redirected to a file or another program): the header line shows only then.</summary>
    public bool OutIsTerminal { get; init; }

    /// <summary>Whether <see cref="Error"/> is a terminal: the runner prints its start-up summary as a table only then, plain lines otherwise.</summary>
    public bool ErrorIsTerminal { get; init; }

    /// <summary>Answers to yes/no questions.</summary>
    public required TextReader In { get; init; }

    /// <summary>Raw stdin, for the local MCP server.</summary>
    public required Func<Stream> OpenStandardInput { get; init; }

    /// <summary>Raw stdout, for the local MCP server.</summary>
    public required Func<Stream> OpenStandardOutput { get; init; }

    /// <summary>Environment variables (null when unset).</summary>
    public required Func<string, string?> GetEnvironmentVariable { get; init; }

    /// <summary>The user's home directory (<c>~</c>, <c>%USERPROFILE%</c>).</summary>
    public required string HomeDirectory { get; init; }

    /// <summary>The directory mbus was started in (relative paths in the script commands, the original issue, start here).</summary>
    public string CurrentDirectory { get; init; } = Environment.CurrentDirectory;

    /// <summary>This machine's host name.</summary>
    public required string MachineName { get; init; }

    /// <summary>Command that starts this mbus, written into agents' MCP configuration.</summary>
    public required McpLaunchCommand MbusCommand { get; init; }

    /// <summary>Directories searched for agent CLIs after PATH (installers that don't touch PATH).</summary>
    public IReadOnlyList<string> ExtraAgentDirectories { get; init; } = [];

    /// <summary>Handler every server call goes through; the real one is <see cref="SocketsHttpHandler"/>.</summary>
    public required Func<HttpMessageHandler> CreateHttpHandler { get; init; }

    /// <summary>Optional separate anonymous discovery transport factory. It must not inject credentials or follow redirects.</summary>
    public Func<HttpMessageHandler>? CreateDiscoveryHttpHandler { get; init; }
    // Public artifacts never share the API/discovery pipeline. Only friend tests can replace
    // this factory, with a registered in-memory terminal or the real restricted transport.
    internal Func<HttpClient> CreateReleaseHttpClient { get; init; } = Update.HttpReleaseSource.CreateHttpClient;
    internal Func<HttpMessageHandler> CreateLocalModelHttpHandler { get; init; } = Agents.OpenCodeReadiness.Handler;

    /// <summary>Clock for request signatures and scan times.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Opens the approval page for <c>mbus login</c> (the original issue); tests answer it by approving through the API.</summary>
    public required IBrowser Browser { get; init; }

    /// <summary>Where <c>mbus connect</c> gets the orchestrate skill's files.</summary>
    public required ISkillSource Skills { get; init; }

    /// <summary>The local MCP server <c>mbus mcp</c> runs.</summary>
    public required ILocalMcpServer McpServer { get; init; }

    /// <summary>Read-only macOS sleep diagnosis; tests supply canned output.</summary>
    internal Func<CancellationToken, Task<string?>> SleepSettings { get; init; } = async ct =>
        OperatingSystem.IsMacOS() ? await Readiness.MacSleepSettings.ReadAsync(ct).ConfigureAwait(false) : null;

    /// <summary>The service manager <c>mbus runner install</c> registers with (the original issue); tests give a fake one.</summary>
    public Func<Runner.Service.ServiceManager> RunnerServices { get; init; } = Runner.Service.ServiceManager.ForThisMachine;

    /// <summary>
    /// The mbus version: a release's exact version, or a development build's with build metadata, e.g.
    /// <c>0.1.0+dev.1a2b3c4</c> (the original issue, the csproj's MbusSourceVersion). Comparisons ignore the metadata (ReleaseVersion).
    /// </summary>
    public static string Version { get; } = ReadVersion();

    internal static string? SourceRevision { get; } = BuildMetadata("MbusSourceRevision");
    internal static bool IsRunnerImage { get; } = BuildMetadata("MbusImageManaged") == "true";
    internal bool ImageManaged { get; init; } = IsRunnerImage;

    private static string? BuildMetadata(string key) => typeof(CliHost).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == key)?.Value;

    /// <summary>
    /// Folder for mbus's settings and machine key: <c>MBUS_HOME</c>, else <c>~/.mbus</c>.
    /// </summary>
    public string ConfigDirectory =>
        GetEnvironmentVariable(EnvVars.Home) is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(HomeDirectory, ".mbus");

    /// <summary>The real machine.</summary>
    public static CliHost CreateDefault() => new()
    {
        Out = Console.Out,
        Error = Console.Error,
        OutIsTerminal = !Console.IsOutputRedirected,
        ErrorIsTerminal = !Console.IsErrorRedirected,
        In = Console.In,
        OpenStandardInput = Console.OpenStandardInput,
        OpenStandardOutput = Console.OpenStandardOutput,
        GetEnvironmentVariable = Environment.GetEnvironmentVariable,
        HomeDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        MachineName = Environment.MachineName,
        MbusCommand = McpLaunchCommand.ForCurrentProcess(),
        ExtraAgentDirectories = DefaultExtraAgentDirectories(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
        CreateHttpHandler = () => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false, UseCookies = false },
        CreateDiscoveryHttpHandler = () => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, Credentials = null },
        Browser = new SystemBrowser(Environment.GetEnvironmentVariable),
        Skills = new EmbeddedSkillSource(),
        McpServer = LocalMcpServerRegistration.Create(),
    };

    /// <summary>
    /// Where agent installers put their command when it isn't on PATH, or when mbus runs with a short
    /// PATH (a launchd agent on macOS starts with only the system folders).
    /// </summary>
    private static string[] DefaultExtraAgentDirectories(string home)
    {
        var dirs = new List<string>
        {
            Path.Combine(home, ".local", "bin"),       // Claude Code's native installer
            Path.Combine(home, ".claude", "local"),    // Claude Code's older local install
            Path.Combine(home, ".npm-global", "bin"),
            Path.Combine(home, ".bun", "bin"),
            Path.Combine(home, ".opencode", "bin"),
        };
        if (OperatingSystem.IsMacOS())
        {
            dirs.Add("/opt/homebrew/bin");
            dirs.Add("/usr/local/bin");
        }
        else if (OperatingSystem.IsWindows() && Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } appData)
        {
            dirs.Add(Path.Combine(appData, "npm"));
        }
        return [.. dirs];
    }

    private static string ReadVersion() =>
        typeof(CliHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(CliHost).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";
}

/// <summary>Environment variables mbus reads. The container image (docker/runner) sets the first four.</summary>
public static class EnvVars
{
    /// <summary>Server URL; wins over the stored one.</summary>
    public const string Server = "MBUS_SERVER";
    /// <summary>An API key; when set, requests use it instead of the machine key and no sign-in is needed.</summary>
    public const string ApiKey = "MBUS_API_KEY";
    /// <summary>The machine's display name.</summary>
    public const string MachineName = "MBUS_MACHINE_NAME";
    /// <summary>The machine's Location label.</summary>
    public const string Location = "MBUS_LOCATION";
    /// <summary>A one-time enrollment token for a shared runner (instead of signing in).</summary>
    public const string EnrollToken = "MBUS_ENROLL_TOKEN";
    /// <summary>Folder for mbus's settings and machine key (default <c>~/.mbus</c>).</summary>
    public const string Home = "MBUS_HOME";
}
