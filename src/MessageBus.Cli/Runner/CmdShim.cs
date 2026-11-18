using System.Text;
using System.Text.RegularExpressions;

namespace MessageBus.Cli.Runner;

/// <summary>
/// The program behind a package manager's Windows <c>.cmd</c> shim (the original issue). npm installs a CLI on Windows as a small
/// batch file that runs <c>node &lt;the package's script&gt; %*</c> (<c>codex.cmd</c>, <c>copilot.cmd</c>, …). Only cmd.exe
/// runs a batch file, and cmd.exe reads <c>"</c> and <c>%</c> in the arguments as its own syntax, so an argument with them
/// can't be passed through it safely (Codex's <c>-c name="value"</c> settings always have quotes). Starting the program the
/// shim names directly passes every argument exactly as it is, with no cmd.exe in between.
/// <para>
/// Understood: npm's shim (<c>SET "_prog=%dp0%\node.exe"</c> when node is beside it, else <c>node</c> from PATH, then
/// <c>"%_prog%" "%dp0%\node_modules\…\cli.js" %*</c>), npm's shim for a package that ships its own program
/// (<c>"%dp0%\…\tool.exe" %*</c>) and pnpm's (<c>"%~dp0\node.exe" "%~dp0\…\cli.js" %*</c>, else <c>node …</c>). For
/// anything else (a hand-written batch file, another variable, a redirection) the result is null and the caller keeps
/// cmd.exe.
/// </para>
/// </summary>
public static partial class CmdShim
{
    /// <summary>What to start instead of the shim: <see cref="Program"/> with <see cref="Arguments"/>, then the caller's own.</summary>
    /// <param name="Program">Full path of an .exe or .com.</param>
    /// <param name="Arguments">The arguments the shim puts before <c>%*</c> (the package's script).</param>
    public sealed record Target(string Program, IReadOnlyList<string> Arguments);

    /// <summary>The program <paramref name="shim"/> runs, or null when the shim isn't one of the known shapes.</summary>
    /// <param name="shim">Full path of the <c>.cmd</c> / <c>.bat</c> file.</param>
    /// <param name="getEnvironmentVariable">Reads PATH, to find <c>node</c> the way the shim would.</param>
    public static Target? Resolve(string shim, Func<string, string?> getEnvironmentVariable)
    {
        string[] lines;
        string folder;
        try
        {
            var file = new FileInfo(shim);
            if (!file.Exists || file.Length > 64 * 1024)
            {
                return null;
            }
            lines = File.ReadAllLines(file.FullName);
            folder = file.DirectoryName!;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }

        string? prog = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimStart('@').Trim();
            if (ProgSetting().Match(line) is { Success: true } setting)
            {
                // npm sets the node beside the shim if there is one, else node from PATH: the first that exists wins.
                if (prog is null && Expand(setting.Groups["value"].Value, folder, null) is { } value)
                {
                    prog = FindProgram(value, getEnvironmentVariable);
                }
                continue;
            }
            if (!line.EndsWith("%*", StringComparison.Ordinal))
            {
                continue;
            }
            if (Command(line[..^2], folder, prog) is not { Count: > 0 } tokens)
            {
                continue;
            }
            if (FindProgram(tokens[0], getEnvironmentVariable) is { } program)
            {
                return new Target(program, tokens[1..]);
            }
        }
        return null;
    }

    /// <summary>The command after the last <c>&amp;</c>, split into expanded tokens; null if it has anything cmd.exe would interpret.</summary>
    private static List<string>? Command(string text, string folder, string? prog)
    {
        var quoted = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '"':
                    quoted = !quoted;
                    break;
                case '&' when !quoted:
                    start = i + 1;
                    break;
            }
        }
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        quoted = false;
        foreach (var c in text[start..])
        {
            if (c == '"')
            {
                quoted = !quoted;
                inToken = true;
            }
            else if (!quoted && c is ' ' or '\t')
            {
                if (inToken && !Add())
                {
                    return null;
                }
                inToken = false;
            }
            else if (!quoted && c is '<' or '>' or '|' or '^' or '(' or ')')
            {
                return null;
            }
            else
            {
                current.Append(c);
                inToken = true;
            }
        }
        return quoted || (inToken && !Add()) ? null : tokens;

        bool Add()
        {
            var token = Expand(current.ToString(), folder, prog);
            current.Clear();
            if (token is null)
            {
                return false;
            }
            tokens.Add(token);
            return true;
        }
    }

    /// <summary>Replaces the shim's folder (<c>%dp0%</c>, <c>%~dp0</c>) and <c>%_prog%</c>; null if any other variable is left.</summary>
    private static string? Expand(string token, string folder, string? prog)
    {
        var dir = folder + Path.DirectorySeparatorChar;
        var relative = token.Contains("%dp0%", StringComparison.OrdinalIgnoreCase) || token.Contains("%~dp0", StringComparison.OrdinalIgnoreCase);
        var template = token.Replace("%dp0%", "", StringComparison.OrdinalIgnoreCase).Replace("%~dp0", "", StringComparison.OrdinalIgnoreCase)
            .Replace("%_prog%", "", StringComparison.OrdinalIgnoreCase);
        if (template.Contains('%') || (prog is null && token.Contains("%_prog%", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        var value = token.Replace("%dp0%", dir, StringComparison.OrdinalIgnoreCase).Replace("%~dp0", dir, StringComparison.OrdinalIgnoreCase)
            .Replace("%_prog%", prog ?? "", StringComparison.OrdinalIgnoreCase);
        try
        {
            return relative ? Path.GetFullPath(value) : value;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// The program cmd.exe would start for <paramref name="name"/>: a full path to an existing .exe or .com, or bare
    /// <c>node</c> found on PATH as one. Any other bare name could be one of cmd.exe's own commands (<c>echo</c>,
    /// <c>call</c>, <c>start</c>), so it isn't a shim this understands.
    /// </summary>
    private static string? FindProgram(string name, Func<string, string?> getEnvironmentVariable)
    {
        if (Path.IsPathFullyQualified(name))
        {
            return File.Exists(name) && (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".com", StringComparison.OrdinalIgnoreCase))
                ? name
                : null;
        }
        // Like npm's shim, which drops .JS from PATHEXT: node.exe, never a script or another batch file.
        return string.Equals(name, "node", StringComparison.OrdinalIgnoreCase)
            ? CommandLocator.Find("node", v => v == "PATHEXT" ? ".COM;.EXE" : getEnvironmentVariable(v), [])
            : null;
    }

    [GeneratedRegex("""^SET\s+"_prog=(?<value>[^"]*)"$""", RegexOptions.IgnoreCase)]
    private static partial Regex ProgSetting();
}
