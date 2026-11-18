using System.Globalization;

namespace MessageBus.Contracts.Releases;

/// <summary>
/// Names shared by the service's release feed and <c>mbus update</c> (the original issue). A release channel is a
/// folder: <see cref="ManifestFile"/> (JSON: version and one entry per platform with file name, size
/// and SHA-256), <see cref="SignatureFile"/> (an ES256 signature of the manifest's exact bytes by
/// the release key) and the binaries the manifest names. The service serves the folder at
/// <see cref="Route"/><c>/{channel}/{file}</c>; for local work <c>mbus</c> reads the folder directly.
/// </summary>
public static class ReleaseFeed
{
    /// <summary>GET <c>/{channel}/{file}</c>: a file of a release channel (anonymous; everything in it is checked against the signature).</summary>
    public const string Route = "/api/releases";

    /// <summary>The channel <c>mbus</c> follows unless told otherwise.</summary>
    public const string DefaultChannel = "stable";

    /// <summary>The version an update asks for when it wants whatever the channel has (<see cref="Runner.UpdateRequest.Version"/>).</summary>
    public const string LatestVersion = "latest";

    /// <summary>The channel of pre-releases (the original issue): every release, stable ones included, so it is never behind stable.</summary>
    public const string PreviewChannel = "preview";

    /// <summary>The channels a workspace can choose (the original issue).</summary>
    public static IReadOnlyList<string> Channels { get; } = [DefaultChannel, PreviewChannel];

    /// <summary>The release manifest in a channel folder.</summary>
    public const string ManifestFile = "manifest.json";

    /// <summary>The detached signature of <see cref="ManifestFile"/>, as base64url text.</summary>
    public const string SignatureFile = "manifest.json.sig";

    /// <summary>
    /// The manifest of one version (the original issue), kept next to <see cref="ManifestFile"/> so a workspace can pin it:
    /// <c>manifest-0.2.0.json</c>, signed like the latest (<c>manifest-0.2.0.json.sig</c>).
    /// </summary>
    /// <param name="version">A version without a <c>+build</c> suffix (<see cref="IsPinnableVersion"/>).</param>
    public static string VersionManifestFile(string version) => $"manifest-{version}.json";

    /// <summary>The signature of <see cref="VersionManifestFile"/>.</summary>
    /// <param name="version">The version.</param>
    public static string VersionSignatureFile(string version) => VersionManifestFile(version) + ".sig";

    /// <summary>Whether <paramref name="version"/> can be pinned: a version (<see cref="ReleaseVersion"/>) whose manifest name is valid.</summary>
    /// <param name="version">The text.</param>
    public static bool IsPinnableVersion(string? version) =>
        ReleaseVersion.IsValid(version) && !version!.Contains('+', StringComparison.Ordinal) && IsValidName(VersionManifestFile(version));

    /// <summary>
    /// A channel or file name the feed serves: letters, digits, <c>.</c>, <c>_</c> and <c>-</c>,
    /// starting with a letter or digit, at most 128 characters (so never a path).
    /// </summary>
    /// <param name="name">The name to check.</param>
    public static bool IsValidName(string? name) =>
        name is { Length: > 0 and <= 128 }
        && char.IsAsciiLetterOrDigit(name[0])
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
        && !name.Contains("..", StringComparison.Ordinal);
}

/// <summary>
/// <c>mbus</c> version numbers: <c>major.minor.patch</c> with an optional <c>-prerelease</c>
/// (compared as semantic versions) and an ignored <c>+build</c> suffix.
/// </summary>
public static class ReleaseVersion
{
    /// <summary>Whether <paramref name="version"/> is a version this type can compare.</summary>
    /// <param name="version">The text.</param>
    public static bool IsValid(string? version) => Parse(version) is not null;

    /// <summary>
    /// Compares two versions: negative when <paramref name="a"/> is older, zero when equal, positive
    /// when newer; null when either isn't a version.
    /// </summary>
    /// <param name="a">One version.</param>
    /// <param name="b">The other.</param>
    public static int? Compare(string? a, string? b)
    {
        if (Parse(a) is not { } x || Parse(b) is not { } y)
        {
            return null;
        }
        for (var i = 0; i < 3; i++)
        {
            if (x.Numbers[i] != y.Numbers[i])
            {
                return x.Numbers[i].CompareTo(y.Numbers[i]);
            }
        }
        // A pre-release comes before its release.
        if (x.Pre is null || y.Pre is null)
        {
            return (x.Pre is null ? 1 : 0) - (y.Pre is null ? 1 : 0);
        }
        var left = x.Pre.Split('.');
        var right = y.Pre.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var ln = long.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var l);
            var rn = long.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var r);
            var order = (ln, rn) switch
            {
                (true, true) => l.CompareTo(r),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.Compare(left[i], right[i], StringComparison.OrdinalIgnoreCase),
            };
            if (order != 0)
            {
                return Math.Sign(order);
            }
        }
        return left.Length.CompareTo(right.Length);
    }

    private static (long[] Numbers, string? Pre)? Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }
        var text = version.Trim();
        if (text.IndexOf('+') is var plus and >= 0)
        {
            text = text[..plus];
        }
        string? pre = null;
        if (text.IndexOf('-') is var dash and >= 0)
        {
            pre = text[(dash + 1)..];
            text = text[..dash];
            if (pre.Length == 0 || pre.Split('.').Any(p => p.Length == 0))
            {
                return null;
            }
        }
        var parts = text.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }
        var numbers = new long[3];
        for (var i = 0; i < 3; i++)
        {
            if (!long.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return null;
            }
        }
        return (numbers, pre);
    }
}
