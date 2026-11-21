using System.ComponentModel;
using System.Diagnostics;

namespace MessageBus.Cli.Runner;

internal interface IVerificationProcess
{
    Task<VerificationProcess.CommandResult> RunAsync(string? checkout, string executable, string[] args, CancellationToken ct,
        bool network = false, bool maskGit = false, string? input = null, string? output = null, TimeSpan? timeout = null, string? npmCache = null);
}

/// <summary>The fixed mount and process boundary for the Node recipe.</summary>
internal sealed class VerificationProcess(TimeProvider time) : IVerificationProcess
{
    private const string Bubblewrap = "/usr/bin/bwrap";
    internal const string Git = "/usr/bin/git";
    internal const string Node = "/usr/bin/node";
    internal const string Npm = "/usr/bin/npm";
    private const string Certificates = "/etc/ssl/certs/ca-certificates.crt";

    internal async Task<bool> ProbeAsync(CancellationToken ct)
    {
        // Check the outer UID separately. A root runner must not qualify even if it can create namespaces.
        if (!OperatingSystem.IsLinux() || !File.Exists(Bubblewrap)
            || (File.GetUnixFileMode(Bubblewrap) & (UnixFileMode.SetUser | UnixFileMode.SetGroup)) != 0
            || !File.Exists(Certificates))
            return false;
        var uid = await ExecuteAsync("/usr/bin/id", ["-u"], TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        if (uid.ExitCode != 0 || !uint.TryParse(uid.Output.Trim(), out var number) || number == 0)
            return false;

        foreach (var executable in new[] { Git, Node, Npm })
        {
            if ((await RunAsync(null, executable, ["--version"], ct).ConfigureAwait(false)).ExitCode != 0)
                return false;
        }
        return (await RunAsync(null, "/bin/sh", ["-c", "test -r /etc/resolv.conf && test -r /etc/ssl/certs/ca-certificates.crt"],
            ct, network: true).ConfigureAwait(false)).ExitCode == 0;
    }

    public Task<CommandResult> RunAsync(string? checkout, string executable, string[] args, CancellationToken ct,
        bool network = false, bool maskGit = false, string? input = null, string? output = null, TimeSpan? timeout = null, string? npmCache = null)
    {
        var arguments = new List<string>
        {
            "--unshare-user", "--disable-userns", "--unshare-pid", "--unshare-ipc", "--unshare-uts", "--new-session",
            "--ro-bind", "/usr/bin", "/usr/bin",
            "--ro-bind", "/usr/lib", "/usr/lib",
            "--ro-bind-try", "/usr/lib64", "/usr/lib64",
            "--ro-bind-try", "/usr/libexec", "/usr/libexec",
            "--ro-bind-try", "/usr/share/nodejs", "/usr/share/nodejs",
            "--ro-bind-try", "/usr/share/git-core", "/usr/share/git-core",
            "--ro-bind", "/bin", "/bin",
            "--ro-bind", "/lib", "/lib",
            "--ro-bind-try", "/lib64", "/lib64",
            "--ro-bind-try", "/etc/ld.so.cache", "/etc/ld.so.cache",
            "--ro-bind", Certificates, Certificates,
            "--dev", "/dev", "--proc", "/proc", "--remount-ro", "/proc", "--tmpfs", "/tmp",
            "--dir", "/tmp/home",
            "--clearenv",
            "--setenv", "HOME", "/tmp/home",
            "--setenv", "TMPDIR", "/tmp",
            "--setenv", "PATH", "/usr/bin:/bin",
            "--setenv", "LANG", "C",
            "--setenv", "CI", "true",
            "--setenv", "GIT_SSL_CAINFO", Certificates,
            "--setenv", "GIT_CONFIG_NOSYSTEM", "1",
            "--setenv", "GIT_CONFIG_GLOBAL", "/dev/null",
            "--setenv", "GIT_TERMINAL_PROMPT", "0",
            "--setenv", "GIT_NO_REPLACE_OBJECTS", "1",
            "--setenv", "GIT_NO_LAZY_FETCH", "1",
            "--setenv", "GIT_ALLOW_PROTOCOL", executable == Git ? (network ? "https" : "file") : "",
        };
        if (network)
        {
            arguments.AddRange(["--ro-bind", "/etc/resolv.conf", "/etc/resolv.conf",
                "--ro-bind-try", "/etc/nsswitch.conf", "/etc/nsswitch.conf"]);
        }
        else
        {
            arguments.Add("--unshare-net");
        }
        if (checkout is not null)
            arguments.AddRange(["--bind", checkout, "/work"]);
        if (maskGit)
            arguments.AddRange(["--tmpfs", "/work/.git", "--remount-ro", "/work/.git"]);
        if (input is not null)
            arguments.AddRange(["--ro-bind", input, "/input"]);
        if (output is not null)
            arguments.AddRange(["--bind", output, "/output"]);
        if (npmCache is not null)
            arguments.AddRange(["--bind", npmCache, "/tmp/npm-cache"]);
        arguments.AddRange(["--remount-ro", "/", "--chdir", checkout is null ? "/tmp" : "/work", "--", executable, .. args]);
        return ExecuteAsync(Bubblewrap, arguments, timeout ?? TimeSpan.FromSeconds(30), ct);
    }

    private async Task<CommandResult> ExecuteAsync(string executable, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(timeout, time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = "/", UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.Environment.Clear();
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return new(-1, "");
        }
        catch (Win32Exception)
        {
            return new(-1, "");
        }

        process.StandardInput.Close();
        var output = ReadAsync(process.StandardOutput, capture: true, stop.Token);
        var error = ReadAsync(process.StandardError, capture: false, stop.Token);
        try
        {
            await process.WaitForExitAsync(stop.Token).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return new(-1, "", TimedOut: true);
        }
    }

    private static async Task<string> ReadAsync(StreamReader reader, bool capture, CancellationToken ct)
    {
        // Drain arbitrary script output without forwarding it to the service or keeping it in memory.
        const int limit = 2 * 1024 * 1024;
        var result = new System.Text.StringBuilder();
        var buffer = new char[4096];
        var overflow = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            if (!capture || overflow) continue;
            if (result.Length + count > limit) overflow = true;
            else result.Append(buffer, 0, count);
        }
        return overflow ? "" : result.ToString();
    }

    internal readonly record struct CommandResult(int ExitCode, string Output, bool TimedOut = false);
}
