using System.Text.Json;
using System.Text.Json.Serialization;

namespace MessageBus.Cli.Update;

/// <summary>
/// <c>manifest.json</c> of a release channel: the version and one file per platform. Its exact bytes
/// are signed by the release key (<c>manifest.json.sig</c>), so the hashes here vouch for the binaries.
/// </summary>
/// <example>
/// <code>
/// { "version": "0.2.0", "channel": "stable", "publishedAt": "2026-11-13T09:00:00Z",
///   "files": [ { "rid": "win-x64", "name": "mbus-0.2.0-win-x64.exe", "size": 71234567, "sha256": "…" } ] }
/// </code>
/// </example>
public sealed record ReleaseManifest
{
    /// <summary>The release's version (<c>major.minor.patch[-pre]</c>).</summary>
    public required string Version { get; init; }

    /// <summary>The channel it was published to (informational).</summary>
    public string? Channel { get; init; }

    /// <summary>When it was published (informational).</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>One self-contained <c>mbus</c> per platform.</summary>
    public List<ReleaseFile> Files { get; set; } = [];
}

/// <summary>The <c>mbus</c> binary for one platform.</summary>
public sealed record ReleaseFile
{
    /// <summary>.NET runtime identifier: <c>win-x64</c>, <c>win-arm64</c>, <c>osx-arm64</c>, <c>osx-x64</c>, <c>linux-x64</c>, <c>linux-arm64</c>.</summary>
    public required string Rid { get; init; }

    /// <summary>File name in the channel folder.</summary>
    public required string Name { get; init; }

    /// <summary>Size in bytes.</summary>
    public required long Size { get; init; }

    /// <summary>SHA-256 of the file, hex.</summary>
    public required string Sha256 { get; init; }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReleaseManifest))]
internal sealed partial class UpdateJsonContext : JsonSerializerContext;
