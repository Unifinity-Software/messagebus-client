namespace MessageBus.Cli.Configuration;

/// <summary>
/// The person's mbus sign-in, approved in the dashboard (<c>mbus login</c>, the original issue), in
/// <c>~/.mbus/cli-session.json</c> (owner-only, like the machine key). It proves who the person is
/// when this machine enrolls; the machine's own requests are signed with its key, not with these.
/// </summary>
public sealed record CliSessionFile
{
    /// <summary>Exact endpoint and isolated floor; retained through token rotation.</summary>
    public Server.ServerBindingSnapshot? ServerBinding { get; init; }

    /// <summary>The server that issued it (no trailing slash).</summary>
    public required string Server { get; init; }

    /// <summary>Short-lived access token (<c>Authorization: MbusCli …</c>).</summary>
    public required string AccessToken { get; init; }

    /// <summary>When <see cref="AccessToken"/> stops working.</summary>
    public required DateTimeOffset AccessExpiresAt { get; init; }

    /// <summary>Gets the next access token; changes on every use (the old one stops working).</summary>
    public required string RefreshToken { get; init; }

    /// <summary>The sign-in's id, as My account lists it.</summary>
    public string? SessionId { get; init; }

    /// <summary>Who signed in, for messages.</summary>
    public string? AccountName { get; init; }

    /// <summary>Their email address, for messages.</summary>
    public string? AccountEmail { get; init; }

    /// <summary>When the person approved it.</summary>
    public DateTimeOffset SignedInAt { get; init; }

    /// <summary>The file's name.</summary>
    public const string FileName = "cli-session.json";

    /// <summary>The lock file mbus processes take before refreshing or replacing the sign-in.</summary>
    public const string LockFileName = "cli-session.lock";
}
