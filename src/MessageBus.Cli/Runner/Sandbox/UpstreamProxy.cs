using System.Net;
using System.Text;

namespace MessageBus.Cli.Runner.Sandbox;

/// <summary>
/// The proxy the runner itself needs to reach the internet (the original issue), from its own <c>HTTPS_PROXY</c>, <c>HTTP_PROXY</c>,
/// <c>ALL_PROXY</c> and <c>NO_PROXY</c> (either case), so each run's allow-list proxy (<see cref="RunNetwork"/>) goes
/// through it rather than connecting directly. Only <c>http://</c> proxies are used (written with or without the
/// scheme, with an optional <c>user:password@</c>); another kind is reported and runs connect directly.
/// </summary>
public sealed class UpstreamProxy
{
    private readonly Uri? _https;
    private readonly Uri? _http;
    private readonly string[] _bypass;
    private readonly bool _bypassAll;

    private UpstreamProxy(Uri? https, Uri? http, IEnumerable<string> bypass)
    {
        _https = https;
        _http = http;
        _bypass = [.. bypass.Select(b => b.Trim().TrimStart('*').TrimStart('.').Trim('[', ']').ToLowerInvariant()).Where(b => b.Length > 0)];
        _bypassAll = bypass.Any(b => b.Trim() == "*");
    }

    /// <summary>The proxy from <paramref name="env"/>; null when none is set (runs connect directly).</summary>
    /// <param name="env">The runner's environment.</param>
    /// <param name="problem">Set when a proxy is set that runs can't use (not <c>http://</c>), for the start-up log.</param>
    public static UpstreamProxy? FromEnvironment(Func<string, string?> env, out string? problem)
    {
        problem = null;
        string? Read(string name) => RunnerEnvironment.NonEmpty(env(name)) ?? RunnerEnvironment.NonEmpty(env(name.ToLowerInvariant()));
        var all = Read("ALL_PROXY");
        var https = Parse(Read("HTTPS_PROXY") ?? all, ref problem);
        var http = Parse(Read("HTTP_PROXY") ?? all, ref problem);
        if (https is null && http is null)
        {
            return null;
        }
        return new UpstreamProxy(https, http, (Read("NO_PROXY") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>A proxy for tests.</summary>
    public static UpstreamProxy For(Uri proxy, params string[] noProxy) => new(proxy, proxy, noProxy);

    /// <summary>The proxy for a connection to <paramref name="host"/>, or null to connect directly (<c>NO_PROXY</c>, this computer).</summary>
    /// <param name="host">The host the run asked for.</param>
    /// <param name="tunnel">A <c>CONNECT</c> tunnel (HTTPS, SSH) rather than a plain <c>http://</c> request.</param>
    public Uri? For(string host, bool tunnel)
    {
        var proxy = tunnel ? _https : _http;
        var name = host.Trim().TrimEnd('.').Trim('[', ']').ToLowerInvariant();
        if (proxy is null || _bypassAll || name is "localhost" || (IPAddress.TryParse(name, out var ip) && IPAddress.IsLoopback(ip)))
        {
            return null;
        }
        return _bypass.Any(b => name == b || name.EndsWith("." + b, StringComparison.Ordinal)) ? null : proxy;
    }

    /// <summary>Where runs' connections go, without any password, for the start-up log.</summary>
    public string Describe()
    {
        var shown = new[] { _https, _http }.OfType<Uri>().Select(u => $"{u.Host}:{u.Port}").Distinct();
        return string.Join(" and ", shown);
    }

    /// <summary>The <c>Proxy-Authorization</c> header line (with its line break) for a proxy URL's user and password; empty without one.</summary>
    internal static string AuthorizationLine(Uri proxy)
    {
        if (proxy.UserInfo.Length == 0)
        {
            return "";
        }
        var credentials = Uri.UnescapeDataString(proxy.UserInfo);
        return $"Proxy-Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials))}\r\n";
    }

    private static Uri? Parse(string? value, ref string? problem)
    {
        if (value is null)
        {
            return null;
        }
        var text = value.Contains("://", StringComparison.Ordinal) ? value : "http://" + value;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp && uri.Host.Length > 0)
        {
            return uri;
        }
        problem = $"the runner's proxy ({(Uri.TryCreate(text, UriKind.Absolute, out var other) ? other.Scheme + "://" + other.Host : "unreadable")}) isn't an http:// proxy, so runs connect directly";
        return null;
    }
}
