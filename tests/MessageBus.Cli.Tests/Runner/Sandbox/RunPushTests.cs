using System.Diagnostics;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Runner.Sandbox;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Tests.Shared;
using Xunit;

namespace MessageBus.Cli.Tests.Runner.Sandbox;

/// <summary>
/// the original issue: a role that can't push is stopped by the run's own git settings, not by a command pattern
/// the agent can type around. Real git, a local bare repository as the remote.
/// </summary>
public sealed class RunPushTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Every_way_of_pushing_fails_when_the_role_cannot_push_and_works_when_it_can(bool canPush)
    {
        Assert.SkipWhen(canPush && InsideRun.Now, InsideRun.PushReason);
        await WindowsRunCapabilities.RequireGitShellAsync();
        var git = CommandLocator.Find("git", Environment.GetEnvironmentVariable, []);
        Assert.SkipWhen(git is null, "git isn't installed.");
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "mbus-push-tests", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            // The remote, with one commit on main, made with plain settings.
            var setupConfig = Path.Combine(root, "setup-gitconfig");
            File.WriteAllText(setupConfig, "[user]\n\tname = Setup\n\temail = setup@example.invalid\n[init]\n\tdefaultBranch = main\n");
            var setup = new Dictionary<string, string?> { ["GIT_CONFIG_GLOBAL"] = setupConfig, ["GIT_CONFIG_NOSYSTEM"] = "1" };
            var bare = Path.Combine(root, "remote.git");
            var seed = Path.Combine(root, "seed");
            Ok(git!, setup, root, "init", "--quiet", "--bare", bare);
            Ok(git!, setup, root, "init", "--quiet", seed);
            Ok(git!, setup, seed, "commit", "--quiet", "--allow-empty", "-m", "seed");
            Ok(git!, setup, seed, "push", "--quiet", bare, "HEAD:refs/heads/main");

            // The run: its own git settings, a clone of the remote.
            var run = Directory.CreateDirectory(Path.Combine(root, "run")).FullName;
            var env = new Dictionary<string, string?>(RunGitConfig.Write(run, new GitIdentity("Run", "run@example.invalid"), null, null, canPush));
            var work = Path.Combine(run, "work");
            Ok(git!, env, run, "clone", "--quiet", bare, work);

            var shell = OperatingSystem.IsWindows()
                ? (File: Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", Args: new[] { "/c", "git push" })
                : (File: "/bin/sh", Args: new[] { "-c", "git push" });

            // the original issue: an agent that rewrote its git settings on purpose (its own, with no push rewrite and no hook) and pushes
            // over SSH, which would use this machine's SSH agent. A stand-in for ssh on PATH runs git's remote command here.
            var stub = Directory.CreateDirectory(Path.Combine(root, "stub-bin")).FullName;
            File.WriteAllText(Path.Combine(stub, "ssh"), StubSsh);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(Path.Combine(stub, "ssh"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var agentConfig = Path.Combine(root, "agent-gitconfig");
            File.WriteAllText(agentConfig, "[user]\n\tname = Agent\n\temail = agent@example.invalid\n");
            var stubPath = stub;
            var sshGit = git!;
            var agentEnv = new Dictionary<string, string?>(env) { ["GIT_CONFIG_GLOBAL"] = agentConfig };
            if (OperatingSystem.IsWindows())
            {
                // the original issue: Git for Windows puts its own usr\bin (which has an ssh) ahead of PATH, so the stand-in never ran, unless
                // it starts the way Git Bash starts it: git.exe itself (not the Git\cmd launcher) with MSYSTEM set. Then it uses
                // PATH as given. Git's usr\bin goes after the stand-in: git needs its sh to run the stand-in and the run's wrapper.
                var execPath = Ok(git!, setup, root, "--exec-path");
                sshGit = Path.Combine(execPath, "git.exe");
                stubPath += Path.PathSeparator + Path.GetFullPath(Path.Combine(execPath, "..", "..", "..", "usr", "bin"));
                agentEnv["MSYSTEM"] = "MINGW64";
            }
            agentEnv["PATH"] = stubPath + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            var overSsh = "ssh://stub/" + (OperatingSystem.IsWindows() ? "/" + char.ToLowerInvariant(bare[0]) + bare[2..].Replace('\\', '/') : bare).TrimStart('/');
            var attempts = new (string How, Func<(int Exit, string Output)> Push)[]
            {
                ("git push", () => Run(git!, env, work, "push")),
                ("git -C <work> push", () => Run(git!, env, run, "-C", work, "push")),
                ($"{Path.GetFileName(shell.File)} {string.Join(' ', shell.Args)}", () => Run(shell.File, env, work, shell.Args)),
                ("git push --no-verify", () => Run(git!, env, work, "push", "--no-verify")),
                ("git push <url> HEAD:main", () => Run(git!, env, work, "push", bare, "HEAD:refs/heads/main")),
                // A push URL the agent set itself isn't rewritten; the hook still refuses it.
                ("git remote set-url --push, then git push", () =>
                {
                    Ok(git!, env, work, "remote", "set-url", "--push", "origin", bare);
                    return Run(git!, env, work, "push");
                }),
                ("git push over ssh with settings the agent rewrote", () => Run(sshGit, agentEnv, work, "push", overSsh, "HEAD:refs/heads/main")),
            };

            var wrong = new List<string>();
            foreach (var (how, push) in attempts)
            {
                Ok(git!, env, work, "commit", "--quiet", "--allow-empty", "-m", how);
                var before = Ok(git!, setup, root, "--git-dir", bare, "rev-parse", "refs/heads/main");
                var commit = Ok(git!, env, work, "rev-parse", "HEAD");
                var (exit, output) = push();
                var after = Ok(git!, setup, root, "--git-dir", bare, "rev-parse", "refs/heads/main");
                if (canPush && (exit != 0 || after != commit))
                {
                    wrong.Add($"{how}: should have pushed (exit {exit}): {output}");
                }
                else if (!canPush && (exit == 0 || after != before))
                {
                    wrong.Add($"{how}: should have been refused (exit {exit}, remote moved: {after != before}): {output}");
                }
            }
            // Fetching over SSH still works when pushing doesn't.
            var (fetchExit, fetched) = Run(sshGit, agentEnv, work, "ls-remote", overSsh, "refs/heads/main");
            if (fetchExit != 0 || !fetched.Contains("refs/heads/main", StringComparison.Ordinal))
            {
                wrong.Add($"git ls-remote over ssh: should have worked (exit {fetchExit}): {fetched}");
            }
            Assert.True(wrong.Count == 0, string.Join("\n", wrong));
        }
        finally
        {
            RunWorkspace.TryDelete(root);
        }
    }

    /// <summary>Stands in for ssh: skips its options and the host, then runs git's remote command (git-receive-pack '…') here.</summary>
    private const string StubSsh = "#!/bin/sh\n"
        + "while [ $# -gt 0 ]; do\n"
        + "  case \"$1\" in\n"
        + "    -o|-p|-i|-l|-F) shift 2 ;;\n"
        + "    -*) shift ;;\n"
        + "    *) break ;;\n"
        + "  esac\n"
        + "done\n"
        + "shift\n"
        + "exec sh -c \"$*\"\n";

    private static string Ok(string file, IReadOnlyDictionary<string, string?> env, string cwd, params string[] args)
    {
        var (exit, output) = Run(file, env, cwd, args);
        Assert.True(exit == 0, $"{Path.GetFileName(file)} {string.Join(' ', args)} failed (exit {exit}): {output}");
        return output.Trim();
    }

    private static (int Exit, string Output) Run(string file, IReadOnlyDictionary<string, string?> env, string cwd, params string[] args)
    {
        var info = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cwd,
        };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        // A run these tests themselves run in (the original issue) has git settings of its own: only the ones each step names apply.
        foreach (var inherited in new[] { "GIT_CONFIG_GLOBAL", "GIT_SSH_COMMAND", "GIT_SSH_VARIANT" })
        {
            info.Environment.Remove(inherited);
        }
        foreach (var (name, value) in env)
        {
            info.Environment[name] = value;
        }
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + error.Result);
    }
}
