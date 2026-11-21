using MessageBus.Contracts.Security;

namespace MessageBus.Cli.Server;

/// <summary>A selected API endpoint. Origin trust and exact credential base scope are separate.</summary>
public sealed record ServerEndpoint
{
    private ServerEndpoint(string origin, string basePath) { Origin = origin; BasePath = basePath; }
    /// <summary>Normalized HTTP transport origin.</summary>
    public string Origin { get; }
    /// <summary>Escaped base path, with a trailing separator.</summary>
    public string BasePath { get; }
    /// <summary>Normalized exact credential scope.</summary>
    public string Value => Origin + BasePath;

    /// <summary>Parses an explicit HTTP(S) endpoint without guessing a scheme or accepting URI credentials.</summary>
    public static ServerEndpoint Parse(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\')
            || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new ServerBindingException("Invalid selected server endpoint.");
        var start = text.IndexOf("://", StringComparison.Ordinal);
        if (start < 0) throw new ServerBindingException("An absolute selected endpoint is required.");
        var end = text.IndexOf('/', start + 3);
        var rawOrigin = end < 0 ? text : text[..end];
        if (!SafeScopePath(uri.AbsolutePath)) throw new ServerBindingException("Ambiguous escaped API base path.");
        try { return new ServerEndpoint(ProtocolOrigin.Normalize(rawOrigin), uri.AbsolutePath.TrimEnd('/') + "/"); }
        catch (ArgumentException ex) { throw new ServerBindingException("Invalid selected server origin.", ex); }
    }

    /// <summary>Checks a final absolute destination against this origin and base-path boundary.</summary>
    public bool Contains(Uri destination)
    {
        if (!destination.IsAbsoluteUri || destination.UserInfo.Length != 0 || destination.Fragment.Length != 0
            || destination.OriginalString.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\')) return false;
        try
        {
            var text = destination.OriginalString;
            var start = text.IndexOf("://", StringComparison.Ordinal);
            if (start < 0) return false;
            var end = text.IndexOfAny(['/', '?', '#'], start + 3);
            if (ProtocolOrigin.Normalize(end < 0 ? text : text[..end]) != Origin) return false;
            return SafeScopePath(destination.AbsolutePath) && destination.AbsolutePath.StartsWith(BasePath, StringComparison.Ordinal);
        }
        catch (ArgumentException) { return false; }
    }

    // Scope comparison must not grant access through separators or traversal revealed by server path decoding.
    // This check never rewrites the raw target passed to signing.
    private static bool SafeScopePath(string path)
    {
        try { MachineRequestSigningV2.ValidateRawTarget(path); }
        catch (ArgumentException) { return false; }
        if (path.Contains("%2f", StringComparison.OrdinalIgnoreCase) || path.Contains("%5c", StringComparison.OrdinalIgnoreCase)
            || path.Contains("%25", StringComparison.OrdinalIgnoreCase)) return false;
        return !path.Split('/').Any(segment => Uri.UnescapeDataString(segment) is "." or "..");
    }
}
