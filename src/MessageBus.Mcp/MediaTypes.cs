namespace MessageBus.Mcp;

/// <summary>The media type a <c>format: file</c> attachment gets from its file name (the original issue), so the receiver knows a screenshot is an image.</summary>
public static class MediaTypes
{
    /// <summary>What anything not listed gets.</summary>
    public const string Binary = "application/octet-stream";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".pdf"] = "application/pdf",
        [".json"] = "application/json",
        [".md"] = "text/markdown",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".html"] = "text/html",
        [".zip"] = "application/zip",
    };

    /// <summary>The media type for <paramref name="fileName"/>'s extension; <see cref="Binary"/> when it has none or an unlisted one.</summary>
    public static string ForFileName(string fileName) =>
        ByExtension.TryGetValue(Path.GetExtension(fileName), out var type) ? type : Binary;
}
