using System.Text;
using System.Text.RegularExpressions;
using MessageBus.Cli.Connect;
using MessageBus.Cli.Tests.Infrastructure;
using Xunit;

namespace MessageBus.Cli.Tests;

/// <summary>
/// The orchestrate skill (the original issue) as mbus carries it: embedded from <c>skills/mbus-orchestrate/**</c> at
/// build time and installed by <c>mbus connect</c> into Claude Code's and Codex's skill folders, in a
/// shape both agents load.
/// </summary>
public sealed partial class OrchestrateSkillTests
{
    [Fact]
    public async Task Connect_installs_the_embedded_skill_in_a_form_Claude_Code_and_Codex_load()
    {
        var source = new EmbeddedSkillSource();
        var files = source.GetFiles().ToDictionary(f => f.RelativePath, f => Encoding.UTF8.GetString(f.Content), StringComparer.Ordinal);
        Assert.True(files.TryGetValue("SKILL.md", out var skill), "SKILL.md isn't embedded: " + string.Join(", ", files.Keys));

        // Both agents read YAML frontmatter with name (= folder) and description; Codex expects LF,
        // only its known keys, and no angle brackets in the description.
        var frontmatter = Frontmatter().Match(skill!);
        Assert.True(frontmatter.Success, "SKILL.md must start with ---\\n frontmatter ---\\n (LF line endings).");
        var fields = frontmatter.Groups[1].Value.Split('\n')
            .Select(l => l.Split(':', 2))
            .ToDictionary(p => p[0].Trim(), p => p.Length > 1 ? p[1].Trim().Trim('"') : "", StringComparer.Ordinal);
        Assert.Equal(["description", "name"], fields.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(source.Name, fields["name"]);
        Assert.InRange(fields["description"].Length, 1, 1024);
        Assert.DoesNotContain('<', fields["description"]);
        Assert.DoesNotContain('>', fields["description"]);
        Assert.Contains("orchestrate", fields["description"], StringComparison.OrdinalIgnoreCase);

        // Every file a skill page links to ships with it.
        foreach (var (path, text) in files.Where(f => f.Key.EndsWith(".md", StringComparison.Ordinal)))
        {
            var folder = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
            foreach (Match link in RelativeLink().Matches(text))
            {
                var target = Path.GetRelativePath(".", Path.Combine(folder, link.Groups[1].Value)).Replace('\\', '/');
                Assert.True(files.ContainsKey(target), $"{path} links to {link.Groups[1].Value}, which isn't in the skill.");
            }
        }

        using var machine = new TestMachine { Skills = source };
        machine.InstallAgent("claude", "2.1.3 (Claude Code)");
        machine.InstallAgent("codex", "codex-cli 0.50.0");
        Assert.Equal(0, (await machine.RunAsync("connect", "--yes")).ExitCode);

        foreach (var home in new[] { Path.Combine(machine.Home, ".claude", "skills"), Path.Combine(machine.Home, ".codex", "skills") })
        {
            var installed = Path.Combine(home, source.Name);
            foreach (var (path, text) in files)
            {
                Assert.Equal(text, File.ReadAllText(Path.Combine(installed, path)));
            }
        }
    }

    [GeneratedRegex(@"\A---\n(.*?)\n---\n", RegexOptions.Singleline)]
    private static partial Regex Frontmatter();

    [GeneratedRegex(@"\]\((?!https?:)([^)#\s]+)(?:#[^)]*)?\)")]
    private static partial Regex RelativeLink();
}
