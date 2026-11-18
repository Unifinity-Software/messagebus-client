using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;

namespace MessageBus.Mcp;

// Independent of inbox lifetime. Hold the file handle across read/report/commit, including network I/O.
internal sealed class ConversationUsage : IDisposable
{
    private readonly string _path;
    private readonly FileStream _lock;
    public JsonObject State { get; }

    private ConversationUsage(string path, FileStream handle)
    {
        _path = path;
        _lock = handle;
        State = File.Exists(path) ? (JsonNode.Parse(File.ReadAllText(path)) ?? throw new JsonException("Empty usage journal.")).AsObject() : new();
        // Validate the checkpoint before a request can use it to assign another interval.
        _ = Total;
        if (Pending is not null && State["boundary"]?.Deserialize<Usage>(MessageBusJson.Options) is null)
            throw new JsonException("Pending usage has no boundary.");
    }

    public static async Task<ConversationUsage> OpenAsync(string directory, string kind, string session, TimeProvider time, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(directory);
        else System.IO.Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(session)));
        var path = Path.Combine(directory, $"usage-{kind}-{key}.json");
        var started = time.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            FileStream handle;
            try { handle = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33 or 11 or 35)
            {
                if (time.GetElapsedTime(started) >= TimeSpan.FromSeconds(30))
                    throw new IOException("Usage journal lock could not be acquired.", e);
                await Task.Delay(TimeSpan.FromMilliseconds(50), time, ct);
                continue;
            }
            try { return new(path, handle); }
            catch { handle.Dispose(); throw; }
        }
    }

    public Usage? Total => State["total"]?.Deserialize<Usage>(MessageBusJson.Options);
    public CompleteProjectInput? Pending => State["pending"]?.Deserialize<CompleteProjectInput>(MessageBusJson.Options);
    public Usage? Report(string id) => State["reports"]?[id]?.Deserialize<Usage>(MessageBusJson.Options);
    public void Prepare(CompleteProjectInput input, Usage total)
    {
        State["pending"] = JsonSerializer.SerializeToNode(input, MessageBusJson.Options);
        State["boundary"] = JsonSerializer.SerializeToNode(total, MessageBusJson.Options);
        Save();
    }
    public void Commit()
    {
        var pending = Pending!;
        State["reports"] ??= new JsonObject();
        State["reports"]![pending.ProjectId] = JsonSerializer.SerializeToNode(pending.Usage, MessageBusJson.Options);
        State["total"] = State["boundary"]?.DeepClone();
        Reject();
    }
    // A failed replay does not prove that the original request failed. Never reassign this interval.
    public void Reserve() { State["total"] = State["boundary"]?.DeepClone(); Reject(); }
    public void Reject() { State.Remove("pending"); State.Remove("boundary"); Save(); }
    private void Save()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, State.ToJsonString());
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, _path, true);
    }
    public void Dispose() => _lock.Dispose();

    public static bool AtLeast(Usage a, Usage b) => a.TokensIn >= b.TokensIn && a.TokensOut >= b.TokensOut
        && a.CachedTokensIn >= b.CachedTokensIn && a.CacheWriteTokensIn >= b.CacheWriteTokensIn
        && a.CacheWrite1hTokensIn >= b.CacheWrite1hTokensIn && a.ActiveSeconds >= b.ActiveSeconds;

    public static bool SameCounts(Usage a, Usage b) => a.TokensIn == b.TokensIn && a.TokensOut == b.TokensOut
        && a.CachedTokensIn == b.CachedTokensIn && a.CacheWriteTokensIn == b.CacheWriteTokensIn
        && a.CacheWrite1hTokensIn == b.CacheWrite1hTokensIn && a.ActiveSeconds == b.ActiveSeconds;

    public static Usage? Delta(Usage total, Usage previous)
    {
        var delta = total with
        {
            TokensIn = total.TokensIn - previous.TokensIn, TokensOut = total.TokensOut - previous.TokensOut,
            CachedTokensIn = total.CachedTokensIn - previous.CachedTokensIn,
            CacheWriteTokensIn = total.CacheWriteTokensIn - previous.CacheWriteTokensIn,
            CacheWrite1hTokensIn = total.CacheWrite1hTokensIn - previous.CacheWrite1hTokensIn,
            ActiveSeconds = total.ActiveSeconds - previous.ActiveSeconds,
            Models = total.Models?.Select(m => Delta(m, previous.Models?.FirstOrDefault(p => p.Model == m.Model) ?? new Usage()))
                .OfType<Usage>().ToList(),
        };
        if (delta.TokensIn < 0 || delta.TokensOut < 0 || delta.CachedTokensIn < 0 || delta.CacheWriteTokensIn < 0
            || delta.CacheWrite1hTokensIn < 0 || delta.ActiveSeconds < 0) return null;
        if (delta.Models is { } models && (models.Sum(m => m.TokensIn) != delta.TokensIn
            || models.Sum(m => m.TokensOut) != delta.TokensOut || models.Sum(m => m.CachedTokensIn) != delta.CachedTokensIn
            || models.Sum(m => m.CacheWriteTokensIn) != delta.CacheWriteTokensIn
            || models.Sum(m => m.CacheWrite1hTokensIn) != delta.CacheWrite1hTokensIn)) return null;
        delta = delta with { Model = delta.Models is { Count: > 0 } parts ? parts.MaxBy(m => m.TokensIn + m.TokensOut)!.Model : delta.Model };
        return delta.TokensIn == 0 && delta.TokensOut == 0 && delta.CachedTokensIn == 0
            && delta.CacheWriteTokensIn == 0 && delta.CacheWrite1hTokensIn == 0 && delta.ActiveSeconds == 0 ? null : delta;
    }
}
