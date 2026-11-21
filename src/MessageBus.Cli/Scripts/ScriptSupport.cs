using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Server;
using MessageBus.Contracts.Mcp;
using MessageBus.Mcp;

namespace MessageBus.Cli.Scripts;

/// <summary>
/// Exit codes of the script commands (the original issue), stable for scripts: one per kind of tool error, plus two for
/// <c>mbus send --wait</c>. 0 and 1 mean what they mean for every mbus command.
/// </summary>
public static class ScriptExitCodes
{
    /// <summary>Done.</summary>
    public const int Ok = 0;

    /// <summary>Something else went wrong; the message says what.</summary>
    public const int Failed = 1;

    /// <summary>An argument is wrong or missing (the tool's <c>invalid-input</c>), including a body that looks like it holds a secret.</summary>
    public const int InvalidInput = 2;

    /// <summary>The thread, role, attachment or target doesn't exist.</summary>
    public const int NotFound = 3;

    /// <summary>Not signed in, or this machine or person may not do that.</summary>
    public const int Forbidden = 4;

    /// <summary>The thread is in a state that doesn't allow it.</summary>
    public const int Conflict = 5;

    /// <summary>The server can't be reached or asked to wait (limit reached): try again later.</summary>
    public const int Unavailable = 6;

    /// <summary>The body or an attachment is over the limit.</summary>
    public const int TooLarge = 7;

    /// <summary><c>--wait</c>: the task ended without succeeding (failed, cancelled or dead-lettered).</summary>
    public const int TaskDidNotSucceed = 8;

    /// <summary><c>--wait</c>: the time ran out while the task was still open.</summary>
    public const int TimedOut = 9;

    public const int WorkspaceSuspended = 10;

    /// <summary>The exit code for a tool error code.</summary>
    public static int For(string code) => code switch
    {
        "workspace-suspended" => WorkspaceSuspended,
        McpErrorCodes.InvalidInput => InvalidInput,
        McpErrorCodes.NotFound => NotFound,
        McpErrorCodes.Forbidden or "unauthenticated" => Forbidden,
        McpErrorCodes.Conflict or "stale-review" or "base-conflict" or "already-terminal" or "idempotency-conflict" => Conflict,
        McpErrorCodes.Unavailable or McpErrorCodes.LimitReached => Unavailable,
        McpErrorCodes.TooLarge => TooLarge,
        _ => Failed,
    };
}

/// <summary>One script command's run: the session with the tools, and its output (JSON or text).</summary>
internal sealed class Script(CliHost host, ScriptSession session, bool json, string command)
{
    public ScriptSession Session => session;

    public CliHost Host => host;

    public bool Json => json;

    /// <summary>A path the person gave, from where mbus was started.</summary>
    public string FullPath(string path) => Path.GetFullPath(path, host.CurrentDirectory);

    /// <summary>The result's JSON with <c>--json</c>, else <paramref name="text"/>'s lines; on error the error and its exit code.</summary>
    public async Task<int> ReportAsync(ScriptResult result, Action<JsonElement> text)
    {
        if (result.Error is { } error) return await FailAsync(error);
        if (json) await WriteJsonAsync(result.Value);
        else text(result.Value);
        return ScriptExitCodes.Ok;
    }

    /// <summary>Writes an error (to stderr; with <c>--json</c> also <c>{"error": …}</c> to stdout) and returns its exit code.</summary>
    public async Task<int> FailAsync(McpError error)
    {
        await host.Error.WriteLineAsync($"mbus {command}: {error.Message}");
        if (json)
        {
            var element = JsonSerializer.SerializeToElement(error, MessageBus.Contracts.Json.MessageBusJson.Options.GetTypeInfo(typeof(McpError)));
            await WriteJsonAsync(new JsonObject { ["error"] = JsonNode.Parse(element.GetRawText()) });
        }
        return ScriptExitCodes.For(error.Code);
    }

    public Task WriteJsonAsync(JsonElement value) => host.Out.WriteLineAsync(Indented(value));

    public Task WriteJsonAsync(JsonNode value)
    {
        using var doc = JsonDocument.Parse(value.ToJsonString());
        return WriteJsonAsync(doc.RootElement);
    }

    public void Line(string text) => host.Out.WriteLine(text);

    private static string Indented(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            value.WriteTo(writer);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // ---- reading results for text output ----

    public static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static int Number(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    public static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [];

    public static JsonElement? Child(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    /// <summary>A target as text (<c>role:developer</c>) from its object form.</summary>
    public static string? Target(JsonElement e, string name)
    {
        if (Child(e, name) is not { } target) return Text(e, name);
        return Text(target, "kind") switch
        {
            "role" => "role:" + Text(target, "role"),
            "agent" => "agent:" + Text(target, "agentId"),
            "inbox" => "inbox:" + Text(target, "inboxId"),
            "capabilities" => "capabilities:" + string.Join(',', Items(target, "capabilities").Select(c => c.GetString())),
            var kind => kind,
        };
    }
}

/// <summary>Starting a script command: the signed-in machine's settings, its server client and the tools.</summary>
internal static class ScriptRunner
{
    /// <summary>
    /// The longest one <c>--wait</c> may block. The MCP server caps a call at its client's limit (the original issue); a script has
    /// no such client, so its own timeout decides.
    /// </summary>
    private const int MaxWaitSeconds = 7 * 24 * 3600;

    public static Option<bool> JsonOption() => new("--json") { Description = "Print the result as JSON (stable for scripts) instead of text." };

    public static async Task<int> RunAsync(CliHost host, ParseResult parse, CommonOptions options, string command, bool json,
        Func<Script, CancellationToken, Task<int>> body, CancellationToken ct)
    {
        var context = CommandContext.Load(host, parse, options);
        var settings = context.Settings;
        if (settings.Server is null || !settings.CanAuthenticate(context.Store) || settings.Stored.WorkspaceId is not { } workspace)
        {
            var notSignedIn = new McpError { Code = McpErrorCodes.Forbidden, Message = ServerConnection.NotSignedIn().Message };
            await host.Error.WriteLineAsync($"mbus {command}: {notSignedIn.Message}");
            if (json) await host.Out.WriteLineAsync("{\"error\":{\"code\":\"forbidden\",\"message\":" + JsonValue.Create(notSignedIn.Message).ToJsonString() + "}}");
            return ScriptExitCodes.Forbidden;
        }

        using var http = ServerConnection.CreateAuthenticatedClient(host, settings, context.Store, "cli");
        await using var session = new ScriptSession(new LocalServerOptions
        {
            Http = http,
            WorkspaceId = workspace,
            MachineId = settings.Stored.MachineId,
            ConfigDirectory = context.Store.Directory,
            MachineKeyPem = context.Store.LoadKeyPem,
            Input = Stream.Null,
            Output = Stream.Null,
            Log = host.Error,
            ClientVersion = CliHost.Version,
            WorkingDirectory = host.CurrentDirectory,
            ProposalAuthority = LocalServer.HasRunMarkers(host.GetEnvironmentVariable) ? ProposalAuthority.Unknown : ProposalAuthority.Interactive,
            Time = host.Time,
            MaxCallSeconds = MaxWaitSeconds,
        });
        return await body(new Script(host, session, json, command), ct);
    }
}
