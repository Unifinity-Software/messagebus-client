using MessageBus.Cli.Hosting;

namespace MessageBus.Cli.Configuration;

/// <summary>
/// The settings a command actually uses: the stored settings with the <c>MBUS_*</c> environment
/// variables on top (a container needs no <c>mbus login</c>), and a <c>--server</c> option on top of
/// both.
/// </summary>
public sealed record CliSettings
{
    /// <summary>Stored settings, as read.</summary>
    public required CliConfig Stored { get; init; }

    /// <summary>The server URL to use, if any.</summary>
    public Uri? Server { get; init; }

    /// <summary>An API key from <c>MBUS_API_KEY</c>.</summary>
    public string? ApiKey { get; init; }

    /// <summary>The machine's display name.</summary>
    public required string MachineName { get; init; }

    /// <summary>The machine's Location label.</summary>
    public string? Location { get; init; }

    /// <summary>Whether requests can be authenticated (an API key, or an enrolled machine key).</summary>
    public bool CanAuthenticate(ConfigStore store) =>
        ApiKey is not null || (Stored.MachineId is not null && store.HasKey);

    /// <summary>Builds the settings for one command.</summary>
    /// <param name="host">The machine.</param>
    /// <param name="stored">Stored settings.</param>
    /// <param name="serverOption">The <c>--server</c> value, if given.</param>
    public static CliSettings Resolve(CliHost host, CliConfig stored, string? serverOption)
    {
        var serverText = NonEmpty(serverOption) ?? NonEmpty(host.GetEnvironmentVariable(EnvVars.Server)) ?? stored.Server;
        var apiKey = NonEmpty(host.GetEnvironmentVariable(EnvVars.ApiKey));
        // the original issue: a runner that enrolled itself with this (runner) key signs as its machine instead.
        if (apiKey is not null && stored.MachineId is not null && stored.EnrolledWithApiKey == Runner.RunnerKeyEnrollment.Fingerprint(apiKey))
        {
            apiKey = null;
        }
        return new CliSettings
        {
            Stored = stored,
            Server = serverText is null ? null : ParseServer(serverText),
            ApiKey = apiKey,
            MachineName = NonEmpty(host.GetEnvironmentVariable(EnvVars.MachineName)) ?? stored.MachineName ?? host.MachineName,
            Location = NonEmpty(host.GetEnvironmentVariable(EnvVars.Location)) ?? stored.Location,
        };
    }

    /// <summary>
    /// Parses a server URL: http or https, no query or fragment. The result has no trailing slash on
    /// its path, so <c>https://host/</c> and <c>https://host</c> are the same server.
    /// </summary>
    public static Uri ParseServer(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new CliException($"'{text}' isn't a server URL. Use the server's address, for example https://app.messagebus.ai.", 2);
        }
        return new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/'));
    }

    /// <summary><paramref name="server"/> as a base address for relative request paths.</summary>
    public static Uri BaseAddress(Uri server) => new(server.AbsoluteUri.TrimEnd('/') + "/");

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
