namespace MessageBus.Mcp;

/// <summary>Where the runner keeps an answer when a settle cannot return every attachment.</summary>
public static class KeptAnswer
{
    /// <summary>The kept answer's file name, next to the run's settle file.</summary>
    public const string KeptFileName = "settle-kept.json";

    /// <summary>Where a refused settle's answer is kept: beside <paramref name="settleFile"/>.</summary>
    public static string KeptFile(string settleFile) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settleFile))!, KeptFileName);
}
