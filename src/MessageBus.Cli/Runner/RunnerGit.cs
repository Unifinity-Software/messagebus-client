using System.ComponentModel;
using System.Diagnostics;
using MessageBus.Cli.Runner.Sandbox;

namespace MessageBus.Cli.Runner;

internal static class RunnerGit
{
    internal static bool IsRemoteRepository(string address) => RepositoryUri(address) is not null;

    internal static Uri? RepositoryUri(string? address)
    {
        if (string.IsNullOrEmpty(address) || address.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c == '\\')) return null;
        if (Uri.TryCreate(address, UriKind.Absolute, out var uri)
            && uri.Scheme is "https" or "ssh" && !string.IsNullOrEmpty(uri.Host)) return uri;

        // SCP-style SSH: require the git user and a host, never a drive or relative path.
        if (!address.StartsWith("git@", StringComparison.Ordinal)) return null;
        var colon = address.IndexOf(':', 4);
        return colon > 4 && colon < address.Length - 1
            && Uri.CheckHostName(address[4..colon]) != UriHostNameType.Unknown
            ? new Uri("ssh://" + address[..colon] + "/" + address[(colon + 1)..]) : null;
    }

    // Runner-owned git commands need long paths and must not start inherited hooks or fsmonitor.
    internal static string[] Arguments(IEnumerable<string> args) =>
        ["-c", "core.longpaths=true", "-c", "core.hooksPath=" + (OperatingSystem.IsWindows() ? "NUL" : "/dev/null"),
            "-c", "core.fsmonitor=false", .. args];

    // Local transport clears GIT_CONFIG_PARAMETERS before starting upload-pack. Pass our fixed settings
    // explicitly so the source can read long object paths too; these arguments contain no shell metacharacters.
    internal static string LocalUploadPack => "--upload-pack=git " + string.Join(' ', Arguments(["upload-pack"]));

    internal static string DescribePath(string error, string path, string description)
    {
        if (!OperatingSystem.IsWindows())
            return error.Replace(path, description, StringComparison.Ordinal);

        return error.Replace(path, description, StringComparison.OrdinalIgnoreCase)
            .Replace(path.Replace('\\', '/'), description, StringComparison.OrdinalIgnoreCase);
    }

    internal static Task<bool> IsValidRefAsync(string reference, CancellationToken ct) =>
        RunAsync(null, ["check-ref-format", reference], "", ct);

    internal static async Task<bool> PushBranchAsync(string checkout, string runDirectory, string reference,
        Uri repository, string destination, RepoCredential credential, CancellationToken ct)
    {
        // Use a clean git config when the push process has credentials. Import only the selected ref.
        var staging = Directory.CreateDirectory(Path.Combine(runDirectory, "pull-request-" + Guid.NewGuid().ToString("N"))).FullName;
        if (!await RunAsync(staging, ["init", "--bare", "--template="], "", ct).ConfigureAwait(false)
            || !await RunAsync(staging, ["fetch", "--no-tags", "--no-write-fetch-head", LocalUploadPack,
                "--", checkout, reference + ":" + reference], "file", ct).ConfigureAwait(false))
            return false;

        // As with the run's credential helper, answer only for the requested HTTPS repository.
        // The token is inherited by this push and its helper through the environment only.
        const string helper = """
            !f() {
              [ "$1" = get ] || exit 0;
              protocol= host= path=;
              while IFS= read -r line && [ -n "$line" ]; do
                case "$line" in
                  protocol=*) protocol=${line#protocol=} ;;
                  host=*) host=${line#host=} ;;
                  path=*) path=${line#path=} ;;
                esac;
              done;
              if [ "$protocol" = https ] && [ "$host" = "$MBUS_PUSH_HOST" ] && [ "$path" = "$MBUS_PUSH_PATH" ]; then
                printf 'username=%s\npassword=%s\n' "$MBUS_PUSH_USER" "$MBUS_PUSH_SECRET";
              fi;
            }; f
            """;
        var useHttps = destination == repository.AbsoluteUri;
        var environment = useHttps ? new Dictionary<string, string>
        {
            ["MBUS_PUSH_HOST"] = repository.Authority,
            ["MBUS_PUSH_PATH"] = Uri.UnescapeDataString(repository.AbsolutePath.TrimStart('/')),
            ["MBUS_PUSH_USER"] = credential.UserName,
            ["MBUS_PUSH_SECRET"] = credential.Secret,
        } : null;
        return await RunAsync(staging, ["-c", "credential.helper=", "-c", "credential.helper=" + helper,
            "-c", "credential.useHttpPath=true", "-c", "http.followRedirects=false", "push", "--no-verify",
            "--", destination, reference + ":" + reference], useHttps ? "https" : "file", ct, environment)
            .ConfigureAwait(false);
    }

    private static async Task<bool> RunAsync(string? directory, string[] args, string protocol, CancellationToken ct,
        IReadOnlyDictionary<string, string>? credentials = null)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory ?? "",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in Arguments(args)) start.ArgumentList.Add(arg);
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "never";
        start.Environment["GIT_ALLOW_PROTOCOL"] = protocol;
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        if (credentials is not null)
            foreach (var (key, value) in credentials) start.Environment[key] = value;

        ct.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.Start(start);
            if (process is null) return false;
            start.Environment.Remove("MBUS_PUSH_SECRET");
            process.StandardInput.Close();
            try
            {
                // Git and remotes can emit arbitrary text. Drain both streams without retaining it.
                await Task.WhenAll(process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct),
                    process.StandardError.BaseStream.CopyToAsync(Stream.Null, ct),
                    process.WaitForExitAsync(ct)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                throw;
            }
            return process.ExitCode == 0;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}
