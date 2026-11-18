using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Sandbox;
using Xunit;

namespace MessageBus.Cli.Tests.Runner.Sandbox;

/// <summary>
/// the original issue: the Linux run sandbox (bubblewrap). What a run sees at each path, from the mounts in the order bubblewrap
/// applies them, for a real home folder layout; and the fall-back to basic isolation where bubblewrap is missing or
/// the machine blocks it. <see cref="RunIsolationTests"/> runs tasks under the real thing where it works.
/// </summary>
public sealed class LinuxSandboxTests
{
    [Fact]
    public void A_run_sees_its_own_folder_and_the_home_folders_hidden_folders_but_not_personal_files_credentials_or_other_runs()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "bubblewrap's mounts are POSIX paths.");
        var root = MacSandbox.RealPath(Directory.CreateTempSubdirectory("mbus-bwrap-").FullName);
        try
        {
            var home = Path.Combine(root, "home");
            string Make(string relative, bool file = false)
            {
                var path = Path.Combine(home, relative);
                Directory.CreateDirectory(file ? Path.GetDirectoryName(path)! : path);
                if (file)
                {
                    File.WriteAllText(path, "x");
                }
                return path;
            }
            Make(".claude");
            Make(".claude.json", file: true);
            Make(".ssh/id_ed25519", file: true);
            Make(".config/gh/hosts.yml", file: true);
            Make(".config/opencode");
            Make(".bashrc", file: true);
            Make("Documents/taxes.pdf", file: true);
            var tool = Make("tools/bin/agent", file: true);
            var workRoot = Make(".mbus/runner/work");
            var run = Make(".mbus/runner/work/dlv_a-1");
            Make(".mbus/runner/work/dlv_b-1/work");
            var secretTool = Make(".config/secret-tool");
            var sandbox = new LinuxSandbox("/usr/bin/bwrap", home, workRoot, [], [secretTool]);

            var mounts = sandbox.Mounts(run, tool);
            LinuxAccess At(string relative) => LinuxSandbox.AccessTo(mounts, Path.Combine(home, relative));

            Assert.Equal(LinuxAccess.ReadWrite, At(".mbus/runner/work/dlv_a-1/work/Program.cs"));
            Assert.Equal(LinuxAccess.ReadWrite, At(".claude/settings.json"));
            Assert.Equal(LinuxAccess.ReadWrite, At(".claude.json"));
            Assert.Equal(LinuxAccess.ReadWrite, At(".config/opencode/opencode.json"));
            Assert.Equal(LinuxAccess.ReadWrite, At(".mbus/config.json")); // the local mbus server's settings
            Assert.Equal(LinuxAccess.ReadOnly, At(".bashrc"));
            Assert.Equal(LinuxAccess.ReadOnly, At("tools/bin/agent")); // the agent's own folder
            Assert.Equal(LinuxAccess.Hidden, At("tools/other"));
            Assert.Equal(LinuxAccess.Hidden, At("Documents/taxes.pdf"));
            Assert.Equal(LinuxAccess.Hidden, At(".ssh/id_ed25519"));
            Assert.Equal(LinuxAccess.Hidden, At(".config/gh/hosts.yml"));
            Assert.Equal(LinuxAccess.Hidden, At(".config/secret-tool/token"));
            Assert.Equal(LinuxAccess.Hidden, At(".mbus/runner/work/dlv_b-1/work/Program.cs")); // another run
            Assert.Equal(LinuxAccess.ReadOnly, LinuxSandbox.AccessTo(mounts, "/usr/bin/git"));
            Assert.Equal(LinuxAccess.ReadWrite, LinuxSandbox.AccessTo(mounts, "/dev/null"));

            // The agent starts in its worktree, in its own PID namespace, as the same command.
            var launch = sandbox.Wrap(new ProcessLaunch { FileName = tool, Arguments = ["run", "--format", "json"], WorkingDirectory = Path.Combine(run, "work") }, run);
            Assert.Equal("/usr/bin/bwrap", launch.FileName);
            Assert.Contains("--unshare-pid", launch.Arguments);
            Assert.Equal(Path.Combine(run, "work"), launch.Arguments[launch.Arguments.ToList().IndexOf("--chdir") + 1]);
            Assert.Equal(["--", tool, "run", "--format", "json"], launch.Arguments.TakeLast(5));

            // the original issue: a resumed session also reads its own folder, and nothing more of the home folder; never the home folder itself.
            var repo = Path.GetDirectoryName(Make("code/repo/README.md", file: true))!;
            var resumed = sandbox.Mounts(run, tool, repo);
            Assert.Equal(LinuxAccess.ReadOnly, LinuxSandbox.AccessTo(resumed, Path.Combine(repo, "README.md")));
            Assert.Equal(LinuxAccess.Hidden, LinuxSandbox.AccessTo(resumed, Path.Combine(home, "Documents/taxes.pdf")));
            Assert.Equal(LinuxAccess.Hidden, LinuxSandbox.AccessTo(sandbox.Mounts(run, tool, home), Path.Combine(home, "Documents/taxes.pdf")));
        }
        finally
        {
            RunWorkspace.TryDelete(root);
        }
    }

    [Theory]
    [InlineData(1, "The Linux sandbox (bubblewrap) isn't used: its start-up check \"sandbox rules\" couldn't start the sandbox (exit 1: bwrap: setting up uid map: Permission denied)")]
    [InlineData(20, "Codex runs keep Codex's own sandbox instead")]
    [InlineData(0, null)]
    public void Runs_get_the_linux_sandbox_only_where_its_start_up_check_passes_and_say_why_not(int rulesExit, string? limitation)
    {
        var root = Directory.CreateTempSubdirectory("mbus-bwrap-").FullName;
        try
        {
            // Exit 1: Ubuntu 24.04 refuses unprivileged user namespaces like this unless bwrap has an AppArmor profile.
            // Exit 20: bubblewrap works but can't start inside itself.
            var probe = new ScriptedRunner(c => c.Step == SandboxProbe.RulesStep
                ? new ProbeOutcome(rulesExit, rulesExit == 1 ? "bwrap: setting up uid map: Permission denied" : "")
                : new ProbeOutcome(0, ""));
            var options = new RunIsolationOptions { WorkRoot = Path.Combine(root, "work"), HomeDirectory = Path.Combine(root, "home"), MachineName = "test" };

            var isolation = (RunIsolation)RunIsolation.Create(options, TextWriter.Null, macProbe: null, linuxProbe: probe, bubblewrap: "/usr/bin/bwrap");

            Assert.Equal(rulesExit is 0 or 20 ? IsolationLevel.OsSandbox : IsolationLevel.Basic, isolation.Level);
            if (limitation is null)
            {
                Assert.DoesNotContain(isolation.Report.Limitations, l => l.Contains("isn't used", StringComparison.Ordinal) || l.Contains("Codex runs keep", StringComparison.Ordinal));
            }
            else
            {
                Assert.Contains(isolation.Report.Limitations, l => l.StartsWith(limitation, StringComparison.Ordinal));
            }
        }
        finally
        {
            RunWorkspace.TryDelete(root);
        }
    }

    private sealed class ScriptedRunner(Func<ProbeCommand, ProbeOutcome> answer) : IProbeRunner
    {
        public ProbeOutcome Run(ProbeCommand command) => answer(command);
    }
}
