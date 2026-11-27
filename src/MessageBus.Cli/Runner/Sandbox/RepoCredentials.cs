using System.Globalization;
using System.Text;

namespace MessageBus.Cli.Runner.Sandbox;

/// <summary>
/// Issues a run's repository credential: short-lived and good for one repository only. Minting real
/// tokens from the customer's GitHub App or Azure DevOps identity is the integrations work (the original issue);
/// until one is registered, <see cref="NoRepoCredentials"/> gives runs none, and they work from git
/// bundles with no remote (as the acceptance run does).
/// </summary>
public interface IRepoCredentialSource
{
    /// <summary>What runs get, for the Runners page's Credentials line (one short sentence).</summary>
    string Description { get; }

    /// <summary>A credential for <see cref="RepoCredentialRequest.Repository"/>, or null when this source has none for it.</summary>
    Task<RepoCredential?> IssueAsync(RepoCredentialRequest request, CancellationToken cancellationToken);
}

/// <summary>What a run asks for.</summary>
public sealed record RepoCredentialRequest
{
    /// <summary>The delivery the run is for.</summary>
    public required string DeliveryId { get; init; }

    /// <summary>The workspace it came from.</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>The task's repository (an https clone URL).</summary>
    public required Uri Repository { get; init; }

    /// <summary>Whether the run may push; when false the token should be read-only.</summary>
    public bool CanPush { get; init; }
}

/// <summary>A repository credential for one run.</summary>
public sealed record RepoCredential
{
    /// <summary>User name git sends (for example <c>x-access-token</c> for a GitHub App token).</summary>
    public required string UserName { get; init; }

    /// <summary>The token.</summary>
    public required string Secret { get; init; }

    /// <summary>When the issuer expires it; the run's credential helper stops answering then too.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>No repository credentials: runs use git bundles, or public repositories.</summary>
public sealed class NoRepoCredentials : IRepoCredentialSource
{
    /// <inheritdoc />
    public string Description => "None: runs get code from git bundles, not from your repositories.";

    /// <inheritdoc />
    public Task<RepoCredential?> IssueAsync(RepoCredentialRequest request, CancellationToken cancellationToken) => Task.FromResult<RepoCredential?>(null);
}

/// <summary>
/// A run's own git settings, in its run directory: no system or global git settings (so none of the
/// machine owner's credential helpers or tokens), the owner's name and email for commits, and a
/// credential helper that answers only for the task's repository and only while its credential
/// file exists. The runner deletes that file when the run ends.
/// </summary>
/// <remarks>
/// When the role can't push (the original issue), every push is sent to an address git refuses to use, however
/// the command is typed (<c>git push</c>, <c>git -C dir push</c>, from a script, with
/// <c>--no-verify</c>, or to a URL instead of a remote), and a pre-push hook refuses it as a second
/// layer. The run's repository credential is read-only too (the service issues it that way). An
/// agent that rewrites git's settings on purpose can get past the first two, not the third.
/// Pushes over SSH (the original issue), which would use the machine owner's SSH agent and keys rather than the run's
/// credential (on Windows the agent's named pipe is reachable from every run), also go through
/// <see cref="SshWrapperFileName"/>: git runs it for every SSH connection (<c>GIT_SSH_COMMAND</c>, which beats
/// any <c>core.sshCommand</c> or <c>-c</c> setting), and it refuses to start git's push program
/// (<c>git-receive-pack</c>) while fetches go through as before. Only clearing that variable gets past it.
/// </remarks>
internal static class RunGitConfig
{
    public const string ConfigFileName = "gitconfig";
    public const string HelperFileName = "git-credential.sh";
    public const string CredentialFileName = "git-credential";
    public const string HooksDirectoryName = "git-hooks";
    public const string SshWrapperFileName = "git-ssh.sh";

    /// <summary>
    /// Where pushes go when the role can't push: a transport git is told never to use, named so that
    /// git's error says why (<c>fatal: transport 'this-role-cannot-push' not allowed</c>).
    /// </summary>
    public const string NoPushScheme = "this-role-cannot-push";

    /// <summary>Writes the run's git settings and, when given one, its credential.</summary>
    /// <param name="runRoot">The run's directory.</param>
    /// <param name="identity">Who commits are by.</param>
    /// <param name="repository">The task's repository, for the credential helper.</param>
    /// <param name="credential">The run's repository credential, if any.</param>
    /// <param name="canPush">The role's permission profile allows pushing.</param>
    /// <param name="ownerGitConfig">
    /// The owner's own git settings file, for a run that uses the owner's git sign-in (the original issue): it is included, and the
    /// system settings (where Git for Windows keeps its credential manager) stay on, so their credential helper answers.
    /// The no-push rules below still apply on top.
    /// </param>
    /// <returns>The variables that point git at them.</returns>
    public static IReadOnlyDictionary<string, string?> Write(string runRoot, GitIdentity identity, Uri? repository, RepoCredential? credential, bool canPush,
        string? sshProxy = null, string? ownerGitConfig = null)
    {
        var config = new StringBuilder();
        if (ownerGitConfig is not null)
        {
            config.Append("# mbus: git settings for this run. The machine's owner lets their own projects use their git sign-in.\n")
                .Append("[include]\n")
                .Append("\tpath = ").Append(ConfigValue(ShellPath(ownerGitConfig))).Append('\n');
        }
        else
        {
            config.Append("# mbus: git settings for this run only. The machine's own git settings and credentials aren't used.\n");
        }
        config.Append("[user]\n")
            .Append("\tname = ").Append(ConfigValue(identity.Name)).Append('\n')
            .Append("\temail = ").Append(ConfigValue(identity.Email)).Append('\n');
        if (ownerGitConfig is null)
        {
            config.Append("[credential]\n")
                .Append("\thelper =\n");
        }
        if (ownerGitConfig is null && repository is not null && credential is not null)
        {
            var helper = Path.Combine(runRoot, HelperFileName);
            var file = Path.Combine(runRoot, CredentialFileName);
            File.WriteAllText(file, string.Create(CultureInfo.InvariantCulture,
                $"{credential.ExpiresAt.ToUnixTimeSeconds()}\nusername={credential.UserName}\npassword={credential.Secret}\n"));
            RestrictToOwner(file);
            File.WriteAllText(helper, HelperScript(repository, file));
            config.Append("\thelper = ").Append(ConfigValue("!sh " + ShellQuote(ShellPath(helper)))).Append('\n')
                .Append("\tuseHttpPath = true\n");
        }
        if (!canPush)
        {
            var hooks = Directory.CreateDirectory(Path.Combine(runRoot, HooksDirectoryName)).FullName;
            var prePush = Path.Combine(hooks, "pre-push");
            File.WriteAllText(prePush, """
                #!/bin/sh
                # mbus: this role can't push.
                echo "mbus: this role's permission profile doesn't allow pushing. Hand the work back with the mbus tools instead." >&2
                exit 1

                """.ReplaceLineEndings("\n"));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(prePush, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            // An empty pushInsteadOf matches every push URL (a remote's or one typed out; https, ssh
            // or a local folder), and git refuses the transport it is rewritten to.
            config.Append("[url \"").Append(NoPushScheme).Append("://push-is-turned-off/\"]\n")
                .Append("\tpushInsteadOf = \"\"\n")
                .Append("[protocol \"").Append(NoPushScheme).Append("\"]\n")
                .Append("\tallow = never\n")
                .Append("[core]\n")
                .Append("\thooksPath = ").Append(ConfigValue(ShellPath(hooks))).Append('\n');
        }
        var configFile = Path.Combine(runRoot, ConfigFileName);
        File.WriteAllText(configFile, config.ToString());
        var variables = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_GLOBAL"] = configFile,
            ["GIT_CONFIG_NOSYSTEM"] = ownerGitConfig is null ? "1" : null,
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
        };
        if (!canPush || sshProxy is not null)
        {
            var wrapper = Path.Combine(runRoot, SshWrapperFileName);
            File.WriteAllText(wrapper, SshWrapperScript(!canPush, sshProxy));
            // git runs GIT_SSH_COMMAND with its own sh (Git for Windows brings one), so this works on every system.
            variables["GIT_SSH_COMMAND"] = "sh " + ShellQuote(ShellPath(wrapper));
            // Without it git guesses from "sh" that the command isn't OpenSSH, and wouldn't pass a port or protocol v2.
            variables["GIT_SSH_VARIANT"] = "ssh";
        }
        return variables;
    }

    /// <summary>
    /// Every SSH connection git makes in a run whose role can't push (the original issue): git passes the remote command
    /// (<c>git-receive-pack '&lt;repo&gt;'</c> for a push, <c>git-upload-pack</c> for a fetch) as an argument. A push
    /// is refused; anything else runs <c>ssh</c> as git would have.
    /// </summary>
    /// <summary>
    /// The run's ssh for git: refuses git's push program when the role can't push (the original issue; pushes over SSH would use this
    /// machine's SSH agent and keys), and with <paramref name="proxy"/> connects through the run's network proxy (the original issue),
    /// which the macOS sandbox requires.
    /// </summary>
    private static string SshWrapperScript(bool refusePush, string? proxy)
    {
        var script = new StringBuilder("#!/bin/sh\n");
        if (refusePush)
        {
            script.Append("""
                # mbus: this role can't push. Pushes over SSH would use this machine's SSH agent and keys.
                for arg in "$@"; do
                  case "$arg" in
                    *receive-pack*)
                      echo "mbus: this role's permission profile doesn't allow pushing. Hand the work back with the mbus tools instead." >&2
                      exit 1 ;;
                  esac
                done

                """.ReplaceLineEndings("\n"));
        }
        script.Append(proxy is null
            ? "exec ssh \"$@\"\n"
            : $"# mbus: through this run's network proxy.\nexec ssh -o {ShellQuote($"ProxyCommand=/usr/bin/nc -X connect -x {proxy} %h %p")} \"$@\"\n");
        return script.ToString();
    }

    /// <summary>Withdraws the run's credential: its helper finds no file and answers nothing.</summary>
    public static void Withdraw(string runRoot)
    {
        try
        {
            File.Delete(Path.Combine(runRoot, CredentialFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// git's credential protocol on stdin (<c>protocol=</c>, <c>host=</c>, <c>path=</c> lines, then a
    /// blank line); the answer on stdout. POSIX sh, which git also brings with it on Windows.
    /// </summary>
    private static string HelperScript(Uri repository, string credentialFile)
    {
        var host = repository.IsDefaultPort ? repository.Host : repository.Authority;
        var path = RepoPath(repository.AbsolutePath);
        return $$"""
            #!/bin/sh
            # mbus: this run's repository credential. It answers only for {{repository}},
            # and only while the run lasts: the runner deletes the credential file when the run ends.
            [ "$1" = get ] || exit 0
            file={{ShellQuote(ShellPath(credentialFile))}}
            [ -f "$file" ] || exit 0
            protocol= host= path=
            cr=$(printf '\r')
            while IFS= read -r line; do
              line=${line%"$cr"}
              case "$line" in
                '') break ;;
                protocol=*) protocol=${line#protocol=} ;;
                host=*) host=${line#host=} ;;
                path=*) path=${line#path=} ;;
              esac
            done
            path=${path%/}
            case "$path" in *.[gG][iI][tT]) path=${path%????} ;; esac
            host=$(printf '%s' "$host" | tr '[:upper:]' '[:lower:]')
            [ "$protocol" = https ] && [ "$host" = {{ShellQuote(host)}} ] && [ "$path" = {{ShellQuote(path)}} ] || exit 0
            expires=$(sed -n 1p "$file")
            [ "$(date +%s)" -lt "$expires" ] || exit 0
            sed -n '2,3p' "$file"

            """.ReplaceLineEndings("\n");
    }

    private static string RepoPath(string absolutePath)
    {
        var path = Uri.UnescapeDataString(absolutePath).Trim('/');
        return path.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? path[..^4] : path;
    }

    /// <summary>A path as git's sh reads it: forward slashes (it accepts <c>C:/…</c> on Windows).</summary>
    private static string ShellPath(string path) => OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string ConfigValue(string value)
    {
        if (value.Contains('\n') || value.Contains('\r'))
        {
            throw new ArgumentException("A git setting can't span lines.", nameof(value));
        }
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static void RestrictToOwner(string file)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

/// <summary>Who commits made in a run are by.</summary>
internal sealed record GitIdentity(string Name, string Email);
