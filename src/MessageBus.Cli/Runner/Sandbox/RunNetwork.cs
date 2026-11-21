using System.Collections.Concurrent;

namespace MessageBus.Cli.Runner.Sandbox;

/// <summary>
/// Host names a run may reach (the original issue): exact names (<c>github.com</c>) and wildcards for every name under a domain
/// (<c>*.githubusercontent.com</c>, which doesn't include the domain itself). Entries may be written as URLs; their
/// scheme, port and path are dropped. Matching ignores case.
/// </summary>
public sealed class HostAllowList
{
    private readonly HashSet<string> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _suffixes = [];

    public HostAllowList(IEnumerable<string> entries)
    {
        foreach (var entry in entries)
        {
            if (Normalize(entry) is not { } host)
            {
                continue;
            }
            if (host.StartsWith("*.", StringComparison.Ordinal))
            {
                if (!_suffixes.Contains(host[1..], StringComparer.OrdinalIgnoreCase))
                {
                    _suffixes.Add(host[1..]);
                }
            }
            else
            {
                _exact.Add(host);
            }
        }
    }

    /// <summary>The entries, as matched (sorted).</summary>
    public IReadOnlyList<string> Entries => [.. _exact.Concat(_suffixes.Select(s => "*" + s)).Order(StringComparer.Ordinal)];

    /// <summary>Whether <paramref name="host"/> (a name or an IP address, no port) is on the list.</summary>
    public bool Allows(string host)
    {
        var name = host.Trim().TrimEnd('.').Trim('[', ']');
        return name.Length > 0 && (_exact.Contains(name) || _suffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase) && name.Length > s.Length));
    }

    /// <summary>The host part of an entry (<c>https://dev.azure.com/org</c> → <c>dev.azure.com</c>), lower case; null when there is none.</summary>
    internal static string? Normalize(string entry)
    {
        var text = entry.Trim();
        if (text.Length == 0)
        {
            return null;
        }
        if (text.Contains("://", StringComparison.Ordinal))
        {
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host.ToLowerInvariant() : null;
        }
        var at = text.IndexOf('@');
        if (at >= 0)
        {
            text = text[(at + 1)..]; // git@github.com:owner/repo
        }
        var end = text.IndexOfAny([':', '/']);
        if (text.StartsWith('['))
        {
            end = text.IndexOf(']') + 1;
        }
        var host = (end > 0 ? text[..end] : text).Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        return host.Length == 0 || host == "*" ? null : host;
    }
}

/// <summary>
/// One run's network limit (the original issue): what the run may reach (<see cref="HostAllowList"/>) on a small HTTP proxy on
/// <c>127.0.0.1</c> (never another interface) that opens connections only to the hosts on that list, for HTTPS
/// (<c>CONNECT</c>, also used for git over SSH) and plain HTTP. Runs are pointed at it with the usual proxy
/// variables; on macOS the run's sandbox also blocks every connection that isn't to this computer, so going
/// around it fails. Elsewhere a program that ignores the proxy variables isn't stopped. When the runner itself
/// reaches the internet through a proxy (<see cref="UpstreamProxy"/>, the original issue), allowed connections go through it.
/// </summary>
/// <remarks>
/// A run gets a proxy of its own on a port of its own (<see cref="Start"/>), or a place on the runner's one proxy
/// (<see cref="RunNetworkProxy.Register"/>, Windows, the original issue), where a credential in its proxy address says which run
/// is asking. Disposing it ends the run's proxy, or withdraws its credential.
/// </remarks>
public sealed class RunNetwork : IDisposable
{
    private readonly RunNetworkProxy _proxy;
    private readonly bool _ownsProxy;
    private readonly ConcurrentDictionary<string, bool> _refused = new(StringComparer.OrdinalIgnoreCase);

    internal RunNetwork(RunNetworkProxy proxy, bool ownsProxy, HostAllowList? allow, string runName, (string User, string Token)? credential)
    {
        _proxy = proxy;
        _ownsProxy = ownsProxy;
        Allowed = allow;
        RunName = runName;
        Credential = credential;
    }

    /// <summary>Starts a proxy of its own for one run, on a port of its own; it takes requests without a credential.</summary>
    /// <param name="allow">What the run may reach.</param>
    /// <param name="log">Runner log (each refused host once).</param>
    /// <param name="runName">The run, for the log.</param>
    /// <param name="connect">Opens a connection to an allowed host and port, or to the upstream proxy (tests); TCP when null.</param>
    /// <param name="upstream">The proxy the runner reaches the internet through (the original issue); null connects directly.</param>
    public static RunNetwork Start(HostAllowList allow, TextWriter log, string runName, Func<string, int, CancellationToken, Task<Stream>>? connect = null,
        UpstreamProxy? upstream = null)
    {
        var proxy = RunNetworkProxy.Start(0, log, connect, upstream);
        var network = new RunNetwork(proxy, ownsProxy: true, allow, runName, credential: null);
        proxy.Only = network;
        return network;
    }

    /// <summary>The proxy's port on 127.0.0.1.</summary>
    public int Port => _proxy.Port;

    /// <summary>What the run may reach; null: any host (a role set to any host, on the runner's one proxy).</summary>
    public HostAllowList? Allowed { get; }

    internal Uri? LocalModelEndpoint { get; set; }

    internal bool AllowsLocalModel(string host, int port) => LocalModelEndpoint is { } endpoint
        && port == endpoint.Port && host.Trim('[', ']').Equals(endpoint.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase)
        && MessageBus.Cli.Agents.OpenCodeSettings.IsLoopback(host);

    /// <summary>The run, for the log.</summary>
    internal string RunName { get; }

    /// <summary>The run's proxy credential on the runner's one proxy; null on a proxy of its own.</summary>
    internal (string User, string Token)? Credential { get; }

    /// <summary>Hosts this run asked for that aren't on its list.</summary>
    public IReadOnlyCollection<string> Refused => [.. _refused.Keys];

    /// <summary>Whether requests must carry the run's credential (the runner's one proxy): <c>nc</c>, which sends git over SSH through the proxy on macOS, can't.</summary>
    public bool NeedsCredential => Credential is not null;

    /// <summary>Notes a refused host; true the first time.</summary>
    internal bool Refuse(string host) => _refused.TryAdd(host, true);

    /// <summary>The proxy variables for the run, in upper and lower case (tools read one or the other).</summary>
    public IReadOnlyDictionary<string, string?> Variables
    {
        get
        {
            var port = Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var url = Credential is { } c
                ? $"http://{Uri.EscapeDataString(c.User)}:{Uri.EscapeDataString(c.Token)}@127.0.0.1:{port}"
                : $"http://127.0.0.1:{port}";
            const string local = "localhost,127.0.0.1,::1";
            return new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["HTTPS_PROXY"] = url, ["https_proxy"] = url,
                ["HTTP_PROXY"] = url, ["http_proxy"] = url,
                ["ALL_PROXY"] = url, ["all_proxy"] = url,
                ["NO_PROXY"] = local, ["no_proxy"] = local,
                // Node (Claude Code, npm) and the .NET SDK read these too; nothing in the run gets past the list with them.
                ["NODE_USE_ENV_PROXY"] = "1",
            };
        }
    }

    /// <summary>The hosts every run may reach whatever its role: this server, the agents' model providers (and the base URLs set for them), and package registries, plus <see cref="SandboxEnvVars.AllowHosts"/>.</summary>
    public static IReadOnlyList<string> BaseHosts(Func<string, string?> env, Uri? server)
    {
        var hosts = new List<string>(ModelProviders);
        hosts.AddRange(PackageRegistries);
        if (server is not null)
        {
            hosts.Add(server.Host);
        }
        foreach (var name in new[] { "ANTHROPIC_BASE_URL", "ANTHROPIC_BEDROCK_BASE_URL", "ANTHROPIC_VERTEX_BASE_URL", "OPENAI_BASE_URL", "CODEX_BASE_URL" })
        {
            if (RunnerEnvironment.NonEmpty(env(name)) is { } url)
            {
                hosts.Add(url);
            }
        }
        if (RunnerEnvironment.NonEmpty(env("CLAUDE_CODE_USE_BEDROCK")) is not null)
        {
            hosts.Add("*.amazonaws.com");
        }
        if (RunnerEnvironment.NonEmpty(env("CLAUDE_CODE_USE_VERTEX")) is not null)
        {
            hosts.Add("*.googleapis.com");
        }
        hosts.AddRange((RunnerEnvironment.NonEmpty(env(SandboxEnvVars.AllowHosts)) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return hosts;
    }

    /// <summary>The hosts for a task's repository: the host itself and every name under it (GitHub: also its download hosts).</summary>
    public static IReadOnlyList<string> RepositoryHosts(Uri repository)
    {
        var host = repository.Host.ToLowerInvariant();
        List<string> hosts = [host, "*." + host];
        if (host == "github.com")
        {
            hosts.Add("*.githubusercontent.com");
        }
        return hosts;
    }

    /// <summary>The agents' model providers and sign-in (Claude Code, Codex, GitHub Copilot CLI, OpenCode).</summary>
    internal static readonly string[] ModelProviders =
    [
        "anthropic.com", "*.anthropic.com", "claude.ai", "*.claude.ai", "claude.com", "*.claude.com",
        "openai.com", "*.openai.com", "chatgpt.com", "*.chatgpt.com",
        "githubcopilot.com", "*.githubcopilot.com", "api.github.com",
        "opencode.ai", "*.opencode.ai", "models.dev",
    ];

    /// <summary>Package registries builds and tests restore from (download only), and Playwright's browser downloads.</summary>
    internal static readonly string[] PackageRegistries =
    [
        "nuget.org", "*.nuget.org", "registry.npmjs.org", "registry.yarnpkg.com", "pypi.org", "files.pythonhosted.org",
        "crates.io", "*.crates.io", "proxy.golang.org", "sum.golang.org", "repo.maven.apache.org", "repo1.maven.org",
        "plugins.gradle.org", "services.gradle.org", "rubygems.org", "index.rubygems.org",
        // the original issue: Playwright's browser downloads (npx playwright install), which browser tests restore like packages.
        "cdn.playwright.dev", "playwright.download.prss.microsoft.com",
    ];

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsProxy)
        {
            _proxy.Dispose();
        }
        else
        {
            _proxy.Withdraw(this);
        }
    }
}
