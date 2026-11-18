using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Sandbox;
using Xunit;

namespace MessageBus.Cli.Tests.Runner.Sandbox;

/// <summary>
/// the original issue: the macOS run sandbox's rules for a given run layout, path by path, and the start-up check
/// that falls back to basic isolation when the sandbox would break git or an agent. These run on
/// every OS (no sandbox-exec involved); <see cref="RunIsolationTests"/> has the test that runs under
/// the real sandbox on a Mac.
/// </summary>
public sealed class MacSandboxTests
{
    private const string Home = "/Users/jo";
    private const string Run = "/Users/jo/.mbus/runs/dlv_a-1";

    private static readonly MacSandboxProfile Profile = new(new MacSandboxPaths
    {
        Home = Home,
        WorkRoot = "/Users/jo/.mbus/runs",
        RunDirectory = Run,
        WritableSystem = ["/dev", "/private/tmp", "/private/var/tmp", "/private/var/folders/xy/abc123/C", "/private/var/folders/xy/abc123/0"],
        Readable = ["/Users/jo/code/messagebus.ai/out"],
        // MBUS_RUNNER_PRIVATE_PATHS, and mbus's settings folder (the original issue: the runner serves the run's mbus tools), which holds the work folder.
        PrivatePaths = ["/Users/jo/.config/secret-tool", "/Users/jo/.mbus"],
    });

    [Fact]
    public void A_run_can_read_and_write_what_git_and_the_agents_need()
    {
        string[] readable =
        [
            // The run's own folder: git settings (the original issue), credential helper, temp folder, worktree, a git bundle.
            $"{Run}/gitconfig", $"{Run}/git-credential.sh", $"{Run}/git-hooks/pre-push", $"{Run}/tmp/x", $"{Run}/work/src/a.cs", $"{Run}/bundle.bundle",
            // Agents' settings and sign-in, per-user toolchains.
            "/Users/jo/.claude/settings.json", "/Users/jo/.claude.json", "/Users/jo/.codex/auth.json",
            "/Users/jo/.dotnet/dotnet", "/Users/jo/.local/bin/claude", "/Users/jo/.npm/_cacache/x", "/Users/jo/.nuget/packages/x", "/Users/jo/go/pkg/mod/x",
            "/Users/jo/Library/Keychains/login.keychain-db", "/Users/jo/Library/Caches/ms-playwright/x", "/Users/jo/Library/Preferences/com.apple.security.plist",
            // mbus itself, wherever it was put.
            "/Users/jo/code/messagebus.ai/out/mbus",
            // The system and installed tools.
            "/usr/bin/git", "/opt/homebrew/bin/node", "/usr/local/bin/dotnet", "/Library/Developer/CommandLineTools/usr/bin/git",
            "/Applications/Xcode.app/Contents/Developer/usr/bin/git", "/private/var/folders/xy/abc123/T/x", "/dev/null", "/dev/tty", "/etc/hosts",
        ];
        string[] writable =
        [
            $"{Run}/gitconfig", $"{Run}/tmp/x", $"{Run}/work/src/a.cs", "/dev/null", "/dev/tty", "/private/tmp/x", "/private/var/folders/xy/abc123/C/x",
            "/Users/jo/.claude/projects/x", "/Users/jo/.claude.json", "/Users/jo/.codex/sessions/x", "/Users/jo/.npm/_cacache/x", "/Users/jo/.nuget/packages/x",
            "/Users/jo/Library/Caches/x", "/Users/jo/Library/Keychains/login.keychain-db",
        ];

        var wrong = readable.Where(p => !Profile.Allows(write: false, p, "/usr/bin")).Select(p => $"can't read {p}")
            .Concat(writable.Where(p => !Profile.Allows(write: true, p, "/usr/bin")).Select(p => $"can't write {p}"))
            .ToList();
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        AssertWellFormed(Profile.Render());
    }

    [Fact]
    public void A_run_cannot_reach_other_runs_personal_folders_or_credentials()
    {
        string[] unreadable =
        [
            "/Users/jo/.mbus/runs/dlv_b-1/work/secret.txt", "/Users/jo/.mbus/runs",
            "/Users/jo/Documents/taxes.pdf", "/Users/jo/Desktop/x", "/Users/jo/Downloads/x", "/Users/jo/Pictures/x", "/Users/jo/code/other-repo/.env", "/Users/jo/notes.txt",
            "/Users/jo/Library/Mail/x", "/Users/jo/Library/Messages/chat.db", "/Users/jo/Library/Application Support/Google/Chrome/Default/Cookies",
            "/Users/jo/Library/Mobile Documents/x", "/Users/jo/Library/Preferences/com.example.app.plist",
            "/Users/jo/.ssh/id_ed25519", "/Users/jo/.aws/credentials", "/Users/jo/.config/gh/hosts.yml", "/Users/jo/.git-credentials", "/Users/jo/.Trash/x",
            "/Users/jo/.config/secret-tool/token",
            // mbus's settings, sign-in and machine key (the original issue).
            "/Users/jo/.mbus/machine-key.pem", "/Users/jo/.mbus/config.json", "/Users/jo/.mbus/cli-session.json",
        ];
        string[] unwritable =
        [
            "/Users/jo/.mbus/runs/dlv_b-1/work/x", "/Users/jo/Documents/x", "/Users/jo/.zshrc", "/Users/jo/.gitconfig", "/Users/jo/.config/git/config",
            "/Users/jo/Library/LaunchAgents/evil.plist", "/Users/jo/Library/Preferences/x.plist", "/Users/jo/.ssh/authorized_keys",
            "/opt/homebrew/bin/x", "/usr/local/bin/x", "/private/var/folders/xy/abc123/T/x", "/etc/hosts",
        ];

        // Even a program started from a folder that is opened up for it doesn't open up credential stores or other runs.
        var wrong = unreadable.Where(p => Profile.Allows(write: false, p, "/usr/bin")).Select(p => $"can read {p}")
            .Concat(unwritable.Where(p => Profile.Allows(write: true, p, "/usr/bin")).Select(p => $"can write {p}"))
            .Concat(new[] { "/Users/jo/.ssh/id_ed25519", "/Users/jo/.mbus/runs/dlv_b-1/x" }.Where(p => Profile.Allows(false, p, "/Users/jo/.ssh")).Select(p => $"exec dir opens {p}"))
            .ToList();
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Theory]
    [InlineData(SandboxProbe.RulesStep, 11, 0)]
    [InlineData(SandboxProbe.RulesStep, 13, 0)]
    [InlineData(SandboxProbe.GitConfigStep, 128, 0)]
    [InlineData(SandboxProbe.GitCloneStep, 128, 0)]
    [InlineData("claude --version", 1, 0)]
    public void A_check_step_the_sandbox_breaks_falls_back_to_basic_and_says_which(string failing, int insideExit, int outsideExit)
    {
        var (isolation, log, _) = CreateWithFakeProbe(failing, insideExit, outsideExit);

        Assert.Equal(IsolationLevel.Basic, isolation.Level);
        var line = log.Split('\n')[0];
        Assert.StartsWith("Run isolation: basic", line);
        Assert.Contains($"\"{failing}\"", line);
    }

    [Fact]
    public void A_check_step_that_fails_outside_the_sandbox_too_is_skipped_not_held_against_the_sandbox()
    {
        var (isolation, _, _) = CreateWithFakeProbe("codex --version", insideExit: 1, outsideExit: 1);

        Assert.Equal(IsolationLevel.OsSandbox, isolation.Level);
        Assert.Contains(isolation.Limitations, l => l.Contains("\"codex --version\"", StringComparison.Ordinal));
    }

    [Fact]
    public void The_checks_git_commands_pass_with_real_git_when_nothing_blocks_them()
    {
        var git = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []);
        Assert.SkipWhen(git is null, "git isn't installed.");
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mbus-probe-tests", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            // Every command runs for real without the sandbox; only the rules script (sh + sandbox-exec) is answered here.
            var runner = new RecordingRunner(c => c.Step == SandboxProbe.RulesStep ? new ProbeOutcome(0, "") : Real.Run(c with { Launch = c.Unwrapped }));
            var isolation = (RunIsolation)RunIsolation.Create(Options(root, git!), TextWriter.Null, runner);

            Assert.True(isolation.Level == IsolationLevel.OsSandbox, isolation.SandboxProblem ?? string.Join(" ", isolation.Limitations));
            Assert.Equal(
                [SandboxProbe.RulesStep, SandboxProbe.GitVersionStep, SandboxProbe.GitConfigStep, SandboxProbe.GitCloneStep],
                runner.Commands.Where(c => c.Sandboxed).Select(c => c.Step));
            Assert.DoesNotContain(runner.Commands, c => !c.Sandboxed && c.Step != SandboxProbe.PrepareBundleStep);
            Assert.False(Directory.Exists(Path.Combine(root, "runs", $".isolation-probe-{Environment.ProcessId}")), "The check leaves nothing behind.");
        }
        finally
        {
            RunWorkspace.TryDelete(root);
        }
    }

    [Theory]
    [InlineData("nc: connectx to 1.1.1.1 port 443 (tcp) failed: Operation not permitted", 1, true)]
    [InlineData("Connection to 1.1.1.1 port 443 [tcp/https] succeeded!", 0, false)]
    [InlineData("nc: connectx to 1.1.1.1 port 443 (tcp) failed: Network is unreachable", 1, false)]
    public void The_network_limit_is_claimed_only_when_the_check_saw_the_sandbox_block_a_connection(string output, int exit, bool enforced)
    {
        // the original issue: the runner says runs reach only their allow-list only after watching the sandbox refuse a connection off the Mac.
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mbus-probe-tests", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var runner = new RecordingRunner(c => c.Step == SandboxProbe.NetworkStep
                ? new ProbeOutcome(exit, output)
                : new ProbeOutcome(0, $"file:{c.Launch.WorkingDirectory}/gitconfig\t{SandboxProbe.ConfigMarker}"));
            var isolation = (RunIsolation)RunIsolation.Create(Options(root, "/usr/bin/git") with { LimitNetwork = true }, TextWriter.Null, runner);

            Assert.Equal(IsolationLevel.OsSandbox, isolation.Level);
            Assert.Equal(enforced, isolation.NetworkEnforced);
            Assert.Equal(enforced, isolation.Limitations.Contains(RunIsolation.NetworkLimitEnforced));
            Assert.Equal(!enforced, isolation.Limitations.Any(l => l.Contains($"\"{SandboxProbe.NetworkStep}\"", StringComparison.Ordinal)));
            Assert.True(runner.Commands.Single(c => c.Step == SandboxProbe.NetworkStep).Sandboxed);
        }
        finally
        {
            RunWorkspace.TryDelete(root);
        }
        AssertWellFormed(new MacSandboxProfile(new MacSandboxPaths { Home = Home, WorkRoot = "/Users/jo/.mbus/runs", RunDirectory = Run, LimitNetwork = true }).Render());
    }

    private static readonly ProcessProbeRunner Real = new();

    private static (RunIsolation Isolation, string Log, RecordingRunner Runner) CreateWithFakeProbe(string failing, int insideExit, int outsideExit)
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mbus-probe-tests", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            // Every step works (git config shows the run's settings), except the failing one: inside the
            // sandbox always, outside it when outsideExit says so.
            var runner = new RecordingRunner(c => c.Step == failing && (c.Sandboxed || outsideExit != 0)
                ? new ProbeOutcome(c.Sandboxed ? insideExit : outsideExit, "fatal: unable to access 'gitconfig': Operation not permitted")
                : new ProbeOutcome(0, $"file:{c.Launch.WorkingDirectory}/gitconfig\t{SandboxProbe.ConfigMarker}"));
            var log = new StringWriter();
            var isolation = (RunIsolation)RunIsolation.Create(Options(root, "/usr/bin/git") with
            {
                ProbeAgents = [new SandboxProbeAgent("claude", "/usr/local/bin/claude"), new SandboxProbeAgent("codex", "/usr/local/bin/codex")],
            }, log, runner);
            return (isolation, log.ToString().ReplaceLineEndings("\n"), runner);
        }
        finally
        {
            RunWorkspace.TryDelete(root);
        }
    }

    private static RunIsolationOptions Options(string root, string? git) => new()
    {
        WorkRoot = Path.Combine(root, "runs"),
        HomeDirectory = Directory.CreateDirectory(Path.Combine(root, "home")).FullName,
        Git = git,
        MachineName = "test-machine",
    };

    /// <summary>Balanced parentheses and closed strings: sandbox-exec refuses a profile that isn't, and the Mac would quietly fall back.</summary>
    private static void AssertWellFormed(string profile)
    {
        var depth = 0;
        var inString = false;
        for (var i = 0; i < profile.Length; i++)
        {
            var c = profile[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == ';')
            {
                i = profile.IndexOf('\n', i) is var end and >= 0 ? end : profile.Length;
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                Assert.True(--depth >= 0, $"Unbalanced ')' at {i}.");
            }
        }
        Assert.False(inString, "A string isn't closed.");
        Assert.Equal(0, depth);
    }

    private sealed class RecordingRunner(Func<ProbeCommand, ProbeOutcome> answer) : IProbeRunner
    {
        public List<ProbeCommand> Commands { get; } = [];

        public ProbeOutcome Run(ProbeCommand command)
        {
            Commands.Add(command);
            return answer(command);
        }
    }
}
