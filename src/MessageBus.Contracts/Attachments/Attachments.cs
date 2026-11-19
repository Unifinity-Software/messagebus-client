using MessageBus.Contracts.Messaging;

namespace MessageBus.Contracts.Attachments;

/// <summary>
/// Ask for a short-lived link to upload one encrypted attachment (the original issue,
/// <see cref="Http.ApiRoutes.AttachmentUploadLink"/>). The service refuses it when the size is over the
/// target role's limit, before anything is uploaded.
/// </summary>
public sealed record UploadLinkRequest
{
    /// <summary>The message's target (its role's attachment limit applies).</summary>
    public required MessageTarget Target { get; init; }

    /// <summary>File name.</summary>
    public required string Name { get; init; }

    /// <summary>How it is packaged.</summary>
    public required AttachmentFormat Format { get; init; }

    /// <summary>Exact size of what will be uploaded (ciphertext), in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Lowercase hex SHA-256 of what will be uploaded.</summary>
    public required string Sha256 { get; init; }

    /// <summary>The thread it will be sent on, if already known.</summary>
    public string? ThreadId { get; init; }

    /// <summary>How it was encrypted (as on <see cref="AttachmentRef.Encryption"/>); null when it isn't end-to-end encrypted.</summary>
    public EncryptionInfo? Encryption { get; init; }
}

/// <summary>A link to upload one attachment. Write-only, for that one blob, valid for minutes.</summary>
public sealed record UploadLink
{
    /// <summary>The attachment id to put in <see cref="AttachmentRef.Id"/>.</summary>
    public required string AttachmentId { get; init; }

    /// <summary>The upload URL (PUT, block blob).</summary>
    public required string Url { get; init; }

    /// <summary>When it stops working.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>The largest size the link accepts.</summary>
    public required long MaxBytes { get; init; }
}

/// <summary>
/// What the local MCP server does with an attachment request in a <c>send</c> call: package it,
/// encrypt it, upload it and add an <see cref="AttachmentRef"/>. Only the local <c>mbus mcp</c>
/// server can act on it (the remote endpoint can't read the caller's files).
/// </summary>
public sealed record AttachSpec
{
    /// <summary>How to package it.</summary>
    public required AttachmentFormat Format { get; init; }

    /// <summary>For a file: its path. For a bundle or patch: the repository directory (default: the current one).</summary>
    public string? Path { get; init; }

    /// <summary>For a bundle or patch: the branch to package (default: the current one).</summary>
    public string? Branch { get; init; }

    /// <summary>For a patch: the required starting commit. For a bundle: the receiver's prerequisite commit, or empty for whole history.
    /// When omitted, a primary task bundle can use the last sent commit and a reply can use the task's received tip.</summary>
    public string? Base { get; init; }

    /// <summary>Local packaging only. For a git bundle, the full 40- or 64-digit hex commit its advertised branch must point to.</summary>
    public string? ExpectedHead { get; init; }
}
