using System.CommandLine;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Releases;

namespace MessageBus.Cli.Update;

/// <summary>Environment variables for updates.</summary>
public static class UpdateEnvVars
{
    /// <summary>
    /// A release feed to use instead of the server's: a folder (local work, a file share) or an
    /// http(s) URL (a mirror), laid out like the server's: one sub-folder per channel.
    /// </summary>
    public const string Feed = "MBUS_UPDATE_FEED";

    /// <summary>The release channel (default <c>stable</c>).</summary>
    public const string Channel = "MBUS_UPDATE_CHANNEL";

    /// <summary>The mbus file to replace, when it isn't the running program (development and tests).</summary>
    public const string Target = "MBUS_UPDATE_TARGET";
}

/// <summary><c>mbus update [--check] [--allow-downgrade] [--channel name]</c>.</summary>
internal static class UpdateCommand
{
    public static Command Create(CliHost host, CommonOptions options)
    {
        var yes = new Option<bool>("--yes", "-y") { Description = "Update the dotnet tool without asking." };
        var check = new Option<bool>("--check") { Description = "Only say whether a newer mbus is available." };
        var allowDowngrade = new Option<bool>("--allow-downgrade") { Description = "Install the release even if it is older than this mbus." };
        var channel = new Option<string?>("--channel") { Description = "Release channel to follow: stable (the default) or preview." };
        var version = new Option<string?>("--version") { Description = "Install this version (for example 0.2.0) instead of the channel's latest, older or not." };
        var command = new Command("update", "Update mbus through dotnet tool or the signed standalone release feed.")
        {
            check, allowDowngrade, channel, version, yes,
        };
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.Load(host, parse, options);
            var tool = DotnetToolInstall.Find(UpdateSetup.TargetPath(host)) ?? DotnetToolInstall.Running;
            if (tool is not null)
            {
                var requested = parse.GetValue(version)?.Trim().TrimStart('v', 'V');
                if (!string.IsNullOrEmpty(requested) && !DotnetToolUpdater.IsReleaseVersion(requested))
                    throw new CliException($"'{requested}' isn't an mbus version.", 2);
                return await DotnetToolUpdateCommand.RunAsync(host, tool, requested, parse.GetValue(channel),
                    parse.GetValue(check), parse.GetValue(yes), ct);
            }
            using var updater = UpdateSetup.Create(host, context.Settings, parse.GetValue(channel), host.Error, mirror: null,
                version: parse.GetValue(version)?.Trim().TrimStart('v', 'V') is { Length: > 0 } pinned ? pinned : null);
            try
            {
                if (parse.GetValue(check))
                {
                    var found = await updater.CheckAsync(ct);
                    await host.Out.WriteLineAsync(Describe(found, updater.DotnetTool));
                    return 0;
                }
                var result = await updater.InstallAsync(parse.GetValue(allowDowngrade), ct);
                await host.Out.WriteLineAsync(result == UpdateResult.UpToDate
                    ? $"mbus {CliHost.Version} is up to date."
                    : "Restart mbus runner and your agents to use the new version everywhere.");
                return 0;
            }
            catch (UpdateRefusedException ex)
            {
                throw new CliException(ex.Message);
            }
            catch (HttpRequestException ex)
            {
                throw new CliException($"Couldn't reach the release feed: {ex.Message}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new CliException($"Couldn't read the release or replace mbus: {ex.Message}");
            }
        });
        return command;
    }

    private static string Describe(ReleaseCheck found, DotnetToolInstall? tool)
    {
        var version = found.Manifest.Version;
        var text = found.Comparison switch
        {
            > 0 => $"mbus {version} is available (this is {CliHost.Version}). Run: {tool?.UpdateCommand() ?? "mbus update"}",
            0 => $"mbus {CliHost.Version} is up to date.",
            _ => $"This mbus ({CliHost.Version}) is newer than the release feed's {version}.",
        };
        if (found.MinimumVersion is { } minimum && ReleaseVersion.Compare(CliHost.Version, minimum) < 0)
        {
            text += $"{Environment.NewLine}The server needs mbus {minimum} or later: runners won't connect until this one is updated.";
        }
        return text;
    }
}

/// <summary>Builds a <see cref="SelfUpdater"/> from the settings, for <c>mbus update</c> and <c>mbus runner</c>.</summary>
public static class UpdateSetup
{
    /// <summary>
    /// The updater for <paramref name="channel"/> (else <c>MBUS_UPDATE_CHANNEL</c>, else stable) from
    /// <c>MBUS_UPDATE_FEED</c>, else <paramref name="mirror"/> (the workspace's, the original issue), else the server's feed.
    /// </summary>
    /// <param name="host">The CLI host.</param>
    /// <param name="settings">The settings (the server's address).</param>
    /// <param name="channel">The channel; null for the configured one.</param>
    /// <param name="log">Progress lines.</param>
    /// <param name="mirror">An http(s) feed to use instead of the server's.</param>
    /// <param name="version">A version to install instead of the channel's latest.</param>
    /// <exception cref="CliException">No release key, or nowhere to get releases from.</exception>
    public static SelfUpdater Create(CliHost host, CliSettings settings, string? channel, TextWriter log, string? mirror = null, string? version = null)
    {
        if (version is not null && !ReleaseFeed.IsPinnableVersion(version))
        {
            throw new CliException($"'{version}' isn't an mbus version (write it like 0.2.0).", 2);
        }
        var key = ReleaseKey.Resolve(host.GetEnvironmentVariable)
            ?? throw new CliException($"This mbus is a development build without the release key, so it can't check releases. Set {ReleaseKey.EnvVar} to the public key your test releases are signed with.");
        var target = TargetPath(host);
        var name = NonEmpty(channel) ?? NonEmpty(host.GetEnvironmentVariable(UpdateEnvVars.Channel)) ?? ReleaseFeed.DefaultChannel;
        if (!ReleaseFeed.IsValidName(name))
        {
            throw new CliException($"'{name}' isn't a release channel name.", 2);
        }
        return new SelfUpdater(new SelfUpdaterOptions
        {
            Source = Source(host, settings, name, mirror),
            PinnedVersion = version,
            PublicKey = key,
            CurrentVersion = CliHost.Version,
            TargetPath = target,
            Log = log,
        });
    }

    private static IReleaseSource Source(CliHost host, CliSettings settings, string channel, string? mirror)
    {
        // The machine's own feed setting wins over the workspace's mirror: either way only signed files are installed.
        if ((NonEmpty(host.GetEnvironmentVariable(UpdateEnvVars.Feed)) ?? NonEmpty(mirror)) is { } feed)
        {
            if (Uri.TryCreate(feed, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp))
            {
                HttpReleaseSource.ValidateChannelUrl(url);
                return Http(host, new Uri(url.AbsoluteUri.TrimEnd('/') + "/" + channel + "/"));
            }
            if (feed.Contains("://", StringComparison.Ordinal))
                throw new CliException("The release feed must be an HTTP(S) URL without credentials, query or fragment, or a folder.");
            return new FolderReleaseSource(Path.Combine(Path.GetFullPath(feed), channel));
        }
        var server = settings.Server
            ?? throw new CliException($"Say where to get mbus from: mbus update --server <url>, or set {UpdateEnvVars.Feed} to a release folder.");
        return Http(host, new Uri(CliSettings.BaseAddress(server), $"{ReleaseFeed.Route.TrimStart('/')}/{channel}/"));
    }

    private static HttpReleaseSource Http(CliHost host, Uri channelUrl)
    {
        var client = host.CreateReleaseHttpClient();
        return new HttpReleaseSource(client, channelUrl);
    }

    /// <summary>
    /// The mbus an update installs for: <c>MBUS_UPDATE_TARGET</c>, else the running mbus, as it is on PATH (not its
    /// version's own file, the original issue); null under <c>dotnet run</c>.
    /// </summary>
    /// <param name="host">The CLI host.</param>
    public static string? TargetPath(CliHost host) =>
        NonEmpty(host.GetEnvironmentVariable(UpdateEnvVars.Target)) is { } custom ? Path.GetFullPath(custom) : RunningMbus();

    /// <summary>The running program when it is mbus itself (not <c>dotnet</c> running a build folder).</summary>
    private static string? RunningMbus() =>
        Environment.ProcessPath is { } path && string.Equals(Path.GetFileNameWithoutExtension(path), "mbus", StringComparison.OrdinalIgnoreCase)
            ? VersionedInstall.StableFor(path) ?? path
            : null;

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
