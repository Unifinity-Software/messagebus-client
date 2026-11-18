using System.Text;

namespace MessageBus.Cli.Runner.Sandbox;

/// <summary>Where things are for one run's macOS sandbox profile. Every path is resolved (no symbolic links), without a trailing slash.</summary>
internal sealed record MacSandboxPaths
{
    /// <summary>The machine owner's home folder.</summary>
    public required string Home { get; init; }

    /// <summary>The runner's work folder (one directory per run).</summary>
    public required string WorkRoot { get; init; }

    /// <summary>This run's directory: its worktree, git settings, credential helper and temp folder.</summary>
    public required string RunDirectory { get; init; }

    /// <summary>System places runs may write: <c>/dev</c>, <c>/private/tmp</c>, the per-user cache folders.</summary>
    public IReadOnlyList<string> WritableSystem { get; init; } = [];

    /// <summary>Folders runs may read even where the home folder's rules would stop them (mbus itself, for <c>mbus mcp</c>).</summary>
    public IReadOnlyList<string> Readable { get; init; } = [];

    /// <summary>More folders runs may not read or write (<see cref="SandboxEnvVars.PrivatePaths"/>, and mbus's settings folder, the original issue).</summary>
    public IReadOnlyList<string> PrivatePaths { get; init; } = [];

    /// <summary>Whether the run may connect only to this computer (its network proxy is here, the original issue).</summary>
    public bool LimitNetwork { get; init; }
}

/// <summary>
/// One run's macOS sandbox rules (the original issue, rewritten for the original issue), as data: rendered to a Seatbelt profile
/// for <c>sandbox-exec</c>, and checked path by path in tests with <see cref="Allows"/>.
/// </summary>
/// <remarks>
/// <para>Everything is allowed (programs, the network, system services such as the keychain) except
/// what the deny rules name. Each rule carries its own exceptions, so no rule depends on another
/// coming before or after it.</para>
/// <para>A run may read the system (installed tools: <c>/usr</c>, <c>/opt/homebrew</c>,
/// <c>/usr/local</c>, <c>/Library/Developer/CommandLineTools</c>, <c>/Applications/Xcode.app</c>, temp
/// folders), its own run directory, and the hidden folders of the home folder (<c>~/.claude</c>,
/// <c>~/.claude.json</c>, <c>~/.codex</c>, <c>~/.dotnet</c>, <c>~/.local</c>, <c>~/.npm</c>, <c>~/.mbus</c> …)
/// except credential stores; in Library only caches, the keychains (Claude Code's sign-in, through
/// the <c>security</c> tool and securityd), developer tools' data and a few tool stores. It can't
/// read the home folder's own folders (Documents, Desktop, Downloads, Pictures, your code), the
/// rest of Library (Mail, Messages, Safari, app data), credential stores (<c>.ssh</c>, <c>.aws</c>,
/// <c>.config/gh</c> …) or other runs.</para>
/// <para>It writes only in its run directory, <c>/dev</c>, the shared temp folders, the hidden folders
/// of the home folder (not credential stores or shell start-up files) and the Library folders above.</para>
/// </remarks>
internal sealed class MacSandboxProfile
{
    /// <summary>The <c>sandbox-exec -D</c> parameter naming the started program's own folder, which it may always read.</summary>
    public const string ExecDirParameter = "EXEC_DIR";

    /// <summary>Hidden files and folders with secrets in them. Relative to the home folder.</summary>
    internal static readonly string[] CredentialStores =
    [
        ".ssh", ".aws", ".azure", ".gnupg", ".kube", ".docker", ".config/gh", ".config/gcloud", ".config/op", ".password-store",
        ".netrc", ".git-credentials", ".vault-token", ".terraform.d", ".Trash", ".zsh_history", ".bash_history", ".zsh_sessions",
    ];

    /// <summary>What a shell or git runs or reads on its own later: a run may read these, not change them.</summary>
    internal static readonly string[] StartupFiles =
        [".zshrc", ".zprofile", ".zshenv", ".zlogin", ".zlogout", ".bashrc", ".bash_profile", ".profile", ".gitconfig", ".config/git", "Library/LaunchAgents"];

    /// <summary>The parts of Library runs may read and write: caches, the keychains, developer tools' data, tool stores.</summary>
    internal static readonly string[] LibraryReadWrite =
        ["Library/Caches", "Library/Keychains", "Library/Developer", "Library/pnpm", "Library/Application Support/go"];

    /// <summary>Single files in Library runs may read (the keychain search list and system-wide settings).</summary>
    internal static readonly string[] LibraryReadOnly =
        ["Library/Preferences/com.apple.security.plist", "Library/Preferences/.GlobalPreferences.plist"];

    /// <summary>Toolchain folders that aren't hidden (Go's <c>~/go</c>).</summary>
    internal static readonly string[] HomeToolFolders = ["go"];

    private readonly List<Rule> _rules = [];

    public MacSandboxProfile(MacSandboxPaths paths)
    {
        Paths = paths;
        var home = paths.Home;
        var run = new Subpath(paths.RunDirectory);
        var library = Join(home, "Library");
        Filter[] readableAnyway = [run, new ExecDir(), .. paths.Readable.Select(p => new Subpath(p))];

        _rules.Add(new Rule("Other runs in the work folder.", Read: true, Write: true, [new Subpath(paths.WorkRoot)], [run]));
        foreach (var path in CredentialStores.Select(p => Join(home, p)).Concat(paths.PrivatePaths))
        {
            _rules.Add(new Rule($"Private: {path}", Read: true, Write: true, [new Subpath(path)], [run]));
        }
        _rules.Add(new Rule("The home folder's own folders: Documents, Desktop, Downloads, Pictures, your code.", Read: true, Write: false,
            [new HomeEntry(home, Hidden: false)],
            [new Subpath(library), .. readableAnyway, .. HomeToolFolders.Select(p => new Subpath(Join(home, p)))]));
        _rules.Add(new Rule("Library, except caches, the keychains, developer tools' data and tool stores.", Read: true, Write: false,
            [new Subpath(library)],
            [new Literal(library), .. readableAnyway,
                .. LibraryReadWrite.Select(p => new Subpath(Join(home, p))),
                .. LibraryReadOnly.Select(p => new Literal(Join(home, p)))]));
        _rules.Add(new Rule("Writes only in the run, temp folders, the home folder's hidden folders and the Library folders above.", Read: false, Write: true,
            [],
            [run, .. paths.WritableSystem.Select(p => new Subpath(p)), new HomeEntry(home, Hidden: true),
                .. LibraryReadWrite.Concat(HomeToolFolders).Select(p => new Subpath(Join(home, p)))]));
        foreach (var path in StartupFiles.Select(p => Join(home, p)))
        {
            _rules.Add(new Rule($"Start-up file: {path}", Read: false, Write: true, [new Subpath(path)], [run]));
        }
    }

    public MacSandboxPaths Paths { get; }

    /// <summary>Whether the rules let a program started from <paramref name="execDir"/> read or write <paramref name="path"/> (a resolved path).</summary>
    public bool Allows(bool write, string path, string execDir) =>
        !_rules.Any(r => (write ? r.Write : r.Read) && r.Match.All(f => f.Matches(path, execDir)) && !r.Except.Any(f => f.Matches(path, execDir)));

    /// <summary>The Seatbelt profile.</summary>
    public string Render()
    {
        var p = new StringBuilder()
            .Append("(version 1)\n")
            .Append("; mbus run sandbox, written by MacSandbox.cs for one run. Everything is allowed except\n")
            .Append("; what the deny rules name; each rule carries its own exceptions, so their order doesn't matter.\n")
            .Append("; Reads are denied as file-read-data (contents and listings), so looking a path up still works.\n")
            .Append("(allow default)\n");
        if (Paths.LimitNetwork)
        {
            // the original issue: connections only to this computer, where the run's proxy is (it lets through the run's allow-list).
            // Local sockets (Unix sockets: the run's mbus tools, the system's name lookup) aren't IP and stay allowed.
            // These two go together: the later rule wins for a connection to this computer.
            p.Append("; Network: only to this computer (the run's proxy applies its allow-list).\n")
                .Append("(deny network-outbound (remote ip \"*:*\"))\n")
                .Append("(allow network-outbound (remote ip \"localhost:*\"))\n");
        }
        foreach (var rule in _rules)
        {
            p.Append("; ").Append(OneLine(rule.Comment)).Append('\n')
                .Append("(deny ").Append(rule.Read && rule.Write ? "file-read-data file-write*" : rule.Read ? "file-read-data" : "file-write*").Append(' ');
            if (rule.Match.Count == 1 && rule.Except.Count == 0)
            {
                p.Append(rule.Match[0].Render());
            }
            else
            {
                p.Append("(require-all");
                foreach (var filter in rule.Match)
                {
                    p.Append("\n    ").Append(filter.Render());
                }
                foreach (var filter in rule.Except)
                {
                    p.Append("\n    (require-not ").Append(filter.Render()).Append(')');
                }
                p.Append(')');
            }
            p.Append(")\n");
        }
        return p.ToString();
    }

    /// <summary>A path under a macOS folder (always <c>/</c>, also when tests run elsewhere).</summary>
    internal static string Join(string folder, string relative) => folder.TrimEnd('/') + "/" + relative;

    private static string OneLine(string text) => text.Replace('\n', ' ').Replace('\r', ' ');

    private sealed record Rule(string Comment, bool Read, bool Write, IReadOnlyList<Filter> Match, IReadOnlyList<Filter> Except);

    private abstract record Filter
    {
        public abstract bool Matches(string path, string execDir);

        public abstract string Render();

        protected static bool Under(string path, string folder) =>
            string.Equals(path, folder, StringComparison.Ordinal) || path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal);
    }

    /// <summary>The path and everything under it.</summary>
    private sealed record Subpath(string Path) : Filter
    {
        public override bool Matches(string path, string execDir) => Under(path, Path);

        public override string Render() => $"(subpath {Str(Path)})";
    }

    /// <summary>Exactly the path.</summary>
    private sealed record Literal(string Path) : Filter
    {
        public override bool Matches(string path, string execDir) => string.Equals(path, Path, StringComparison.Ordinal);

        public override string Render() => $"(literal {Str(Path)})";
    }

    /// <summary>The started program's own folder (<see cref="ExecDirParameter"/>).</summary>
    private sealed record ExecDir : Filter
    {
        public override bool Matches(string path, string execDir) => Under(path, execDir);

        public override string Render() => $"(subpath (param \"{ExecDirParameter}\"))";
    }

    /// <summary>Anything in the home folder whose first part is hidden (<c>~/.x…</c>) or isn't (<c>~/Documents…</c>).</summary>
    private sealed record HomeEntry(string Home, bool Hidden) : Filter
    {
        public override bool Matches(string path, string execDir) =>
            path.Length > Home.Length + 1 && path.StartsWith(Home + "/", StringComparison.Ordinal)
            && (Hidden ? path[Home.Length + 1] == '.' : path[Home.Length + 1] is not ('.' or '/'));

        public override string Render() => $"(regex #\"^{Rx(Home)}/{(Hidden ? "\\." : "[^./]")}\")";
    }

    private static string Str(string value)
    {
        if (value.Contains('\n') || value.Contains('\r'))
        {
            throw new ArgumentException("A sandboxed path can't span lines.", nameof(value));
        }
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string Rx(string value)
    {
        var sb = new StringBuilder();
        foreach (var c in value)
        {
            if (c is '"' or '\n' or '\r')
            {
                throw new ArgumentException("A sandboxed path can't contain a double quote or a line break.", nameof(value));
            }
            if (".[]()*+?{}|^$\\".Contains(c))
            {
                sb.Append('\\');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
