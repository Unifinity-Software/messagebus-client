using System.Globalization;

namespace MessageBus.Contracts.Security;

/// <summary>Exact HTTP origin normalization shared by isolated binding and signing. Never resolves DNS.</summary>
public static class ProtocolOrigin
{
    /// <summary>Normalizes an absolute HTTP(S) origin, rejecting userinfo, non-root paths and ambiguous input.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\')
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != "https" && uri.Scheme != "http")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/")
            throw new ArgumentException("An exact HTTP(S) origin is required.", nameof(text));

        var start = text.IndexOf("://", StringComparison.Ordinal);
        if (start < 0) throw new ArgumentException("An absolute origin is required.", nameof(text));
        var rest = text[(start + 3)..];
        var slash = rest.IndexOf('/');
        if (slash >= 0 && rest[slash..] != "/")
            throw new ArgumentException("Origin cannot contain a non-root path.", nameof(text));
        var authority = slash < 0 ? rest : rest[..slash];
        if (authority.Length == 0 || authority.Contains('%') || authority.Contains('@') || authority.EndsWith(':')
            || authority.Contains('/') || authority.Contains('?') || authority.Contains('#'))
            throw new ArgumentException("Ambiguous origin authority.", nameof(text));
        string host;
        try { host = uri.IdnHost.ToLowerInvariant(); }
        catch (UriFormatException ex) { throw new ArgumentException("Invalid IDNA origin host.", nameof(text), ex); }
        if (host.Length == 0 || host.EndsWith('.') || uri.HostNameType == UriHostNameType.Unknown)
            throw new ArgumentException("Invalid origin host.", nameof(text));
        if (uri.HostNameType == UriHostNameType.Dns && (host.Length > 253 || host.Split('.').Any(label =>
            label.Length is 0 or > 63 || label.StartsWith('-') || label.EndsWith('-')
            || label.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))))
            throw new ArgumentException("Malformed DNS origin host.", nameof(text));
        // Reject shorthand, octal and integer IPv4 forms accepted by some URI implementations.
        if (uri.HostNameType == UriHostNameType.IPv4 && authority.Split(':')[0] != uri.Host)
            throw new ArgumentException("An exact IPv4 literal is required.", nameof(text));
        if (uri.HostNameType == UriHostNameType.IPv6 && !host.StartsWith('[')) host = "[" + host + "]";
        return uri.Scheme.ToLowerInvariant() + "://" + host
            + (uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Requires HTTPS, except HTTP to an exact loopback origin under an explicit local fixture policy.</summary>
    public static string NormalizeIsolated(string text, bool allowLoopbackHttp = false)
    {
        var origin = Normalize(text);
        var uri = new Uri(origin);
        if (uri.Scheme != "https" && !(allowLoopbackHttp && uri.IsLoopback))
            throw new ArgumentException("Isolated authority requires HTTPS.", nameof(text));
        return origin;
    }
}
