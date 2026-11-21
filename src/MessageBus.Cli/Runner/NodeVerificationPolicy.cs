using System.Text.Json;
using System.Text.RegularExpressions;

namespace MessageBus.Cli.Runner;

internal static class NodeVerificationPolicy
{
    internal const string Registry = "https://registry.npmjs.org/";
    private const int MaximumJsonBytes = 16 * 1024 * 1024;

    internal static bool SupportsCheck(string key) => key is "test" or "typecheck" or "build";

    internal static string? ProjectPrefix(string checkout, IReadOnlyList<string> checks)
    {
        var nested = Path.Combine(checkout, "src", "client");
        var rootPresent = HasProjectFiles(checkout);
        var nestedPresent = HasProjectFiles(nested);
        if (rootPresent == nestedPresent) return null;
        return Allows(checkout, rootPresent ? checkout : nested, checks)
            ? rootPresent ? "/work" : "/work/src/client"
            : null;
    }

    private static bool HasProjectFiles(string directory) =>
        File.Exists(Path.Combine(directory, "package.json")) || File.Exists(Path.Combine(directory, "package-lock.json"))
        || Directory.Exists(Path.Combine(directory, "package.json")) || Directory.Exists(Path.Combine(directory, "package-lock.json"));

    private static bool Allows(string checkout, string project, IReadOnlyList<string> checks)
    {
        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(checkout)]);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in directory.EnumerateFileSystemInfos())
            {
                if (entry.LinkTarget is not null) return false;
                if (entry.Name == ".git" && directory.FullName == checkout) continue;
                if (entry.Name.Equals(".npmrc", StringComparison.OrdinalIgnoreCase)
                    || entry.Name.Equals("npm-shrinkwrap.json", StringComparison.OrdinalIgnoreCase)
                    || entry.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (entry is DirectoryInfo child) pending.Push(child);
            }
        }

        using var manifest = ReadJson(Path.Combine(project, "package.json"));
        using var lockfile = ReadJson(Path.Combine(project, "package-lock.json"));
        if (manifest is null || lockfile is null) return false;
        var package = manifest.RootElement;
        var locked = lockfile.RootElement;
        if (!UniqueObject(package) || !UniqueObject(locked) || package.TryGetProperty("workspaces", out _)
            || package.TryGetProperty("overrides", out _) || locked.TryGetProperty("dependencies", out _)
            || !locked.TryGetProperty("lockfileVersion", out var version) || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var format) || format != 3
            || !package.TryGetProperty("scripts", out var scripts) || !UniqueObject(scripts)
            || !checks.All(key => scripts.TryGetProperty(key, out var script) && script.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(script.GetString()))
            || !AllowsDependencySpecs(package)
            || !locked.TryGetProperty("packages", out var packages) || !UniqueObject(packages)
            || !packages.TryGetProperty("", out _))
            return false;

        foreach (var entry in packages.EnumerateObject())
        {
            if (!UniqueObject(entry.Value) || !AllowsDependencySpecs(entry.Value, archive: entry.Name.Length > 0)
                || entry.Value.TryGetProperty("link", out _) || entry.Value.TryGetProperty("workspaces", out _))
                return false;
            if (entry.Name.Length == 0) continue;
            if (!IsInstallPath(entry.Name)
                || !entry.Value.TryGetProperty("version", out var packageVersion) || !IsPackageVersion(packageVersion)
                || !entry.Value.TryGetProperty("resolved", out var url) || !RegistryTarball(url)
                || !entry.Value.TryGetProperty("integrity", out var integrity) || !Sha512(integrity))
                return false;
        }
        return true;
    }

    private static bool AllowsDependencySpecs(JsonElement package, bool archive = false)
    {
        if (package.TryGetProperty("inBundle", out _)) return false;
        // A bundle declaration names dependencies whose bytes are already covered by the
        // registry archive's hash. Separate lock entries still need their own source and hash.
        foreach (var name in new[] { "bundleDependencies", "bundledDependencies" })
        {
            if (!package.TryGetProperty(name, out var bundled)) continue;
            if (!archive || bundled.ValueKind != JsonValueKind.Array || bundled.EnumerateArray().Any(entry =>
                    entry.ValueKind != JsonValueKind.String || !IsBundledPackageName(entry.GetString()!)))
                return false;
        }
        foreach (var name in new[] { "dependencies", "devDependencies", "optionalDependencies", "peerDependencies" })
        {
            if (!package.TryGetProperty(name, out var dependencies)) continue;
            if (!UniqueObject(dependencies) || dependencies.EnumerateObject().Any(entry =>
                entry.Value.ValueKind != JsonValueKind.String || !RegistryRange(entry.Value.GetString()!)))
                return false;
        }
        return true;
    }

    // Only version-shaped tokens can introduce letters. In particular, npm tags, aliases and
    // Git shorthand cannot pass even when a prerelease identifier contains letters.
    private const string Number = "(?:0|[1-9][0-9]*)";
    private const string Part = "(?:" + Number + "|[xX*])";
    private const string Identifiers = "[0-9A-Za-z-]+(?:[.][0-9A-Za-z-]+)*";
    private const string Version = "(?:v?" + Number + "[.]" + Number + "[.]" + Number
        + "(?:-" + Identifiers + ")?(?:[+]" + Identifiers + ")?|v?" + Part + "(?:[.]" + Part + "){0,2})";
    private const string Comparator = "(?:(?:[<>]=?|[=~^]) *)?" + Version;
    private const string Range = "(?:" + Version + " +- +" + Version + "|" + Comparator + "(?: +" + Comparator + ")*)";
    private static readonly Regex RegistryRangePattern = new(
        @"\A *" + Range + "(?: *[|][|] *" + Range + @")* *\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static bool RegistryRange(string value) => RegistryRangePattern.IsMatch(value);

    private static bool IsInstallPath(string path)
    {
        var segments = path.Split('/');
        for (var i = 0; i < segments.Length;)
        {
            if (segments[i++] != "node_modules" || i == segments.Length) return false;
            if (segments[i].StartsWith('@') && (!IsPackageName(segments[i++][1..]) || i == segments.Length))
                return false;
            if (!IsPackageName(segments[i++])) return false;
        }
        return true;
    }

    private static bool IsPackageName(string name) => name.Length > 0 && name[0] is not ('.' or '_')
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static bool IsBundledPackageName(string name)
    {
        if (!name.StartsWith('@')) return IsPackageName(name);
        var slash = name.IndexOf('/');
        return slash > 1 && IsPackageName(name[1..slash]) && IsPackageName(name[(slash + 1)..]);
    }

    private static bool IsPackageVersion(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } version
            || !version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '+'))
            return false;
        var core = version.Split(['-', '+'], 2)[0].Split('.');
        return core.Length == 3 && core.All(part => part.Length > 0 && part.All(char.IsAsciiDigit));
    }

    private static bool RegistryTarball(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } raw
            || !raw.StartsWith(Registry, StringComparison.Ordinal)
            || !Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.AbsolutePath.EndsWith(".tgz", StringComparison.Ordinal))
            return false;
        return string.Equals(uri.Host, "registry.npmjs.org", StringComparison.Ordinal)
            && !raw.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c is '\\' or '%');
    }

    private static bool Sha512(JsonElement value)
    {
        Span<byte> hash = stackalloc byte[64];
        return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: 95 } text
            && text.StartsWith("sha512-", StringComparison.Ordinal)
            && Convert.TryFromBase64Chars(text.AsSpan(7), hash, out var count) && count == hash.Length;
    }

    private static bool UniqueObject(JsonElement element) => element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count()
            == element.EnumerateObject().Count();

    private static JsonDocument? ReadJson(string file) =>
        File.Exists(file) && new FileInfo(file).Length <= MaximumJsonBytes ? JsonDocument.Parse(File.ReadAllBytes(file)) : null;
}
