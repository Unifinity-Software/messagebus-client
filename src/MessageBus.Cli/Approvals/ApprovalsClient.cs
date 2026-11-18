using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.SignIn;
using MessageBus.Contracts.Http;
using MessageBus.Contracts.Mcp;

namespace MessageBus.Cli.Approvals;

/// <summary>Error codes the approvals calls add to <see cref="McpErrorCodes"/>.</summary>
public static class ApprovalCodes
{
    /// <summary>A high-risk request (the original issue): answer it in the dashboard after signing in again.</summary>
    public const string SignInAgain = "sign-in-again";
}

/// <summary>One approval waiting for the signed-in person's answer, as the server sent it.</summary>
public sealed record PendingApproval(string Id, string Title, string? Rule, bool HighRisk, string? RequestedBy, DateTimeOffset CreatedAt, JsonObject Raw)
{
    internal static PendingApproval? From(JsonNode? node) =>
        node is JsonObject o && Text(o, "approvalId") is { } id
            ? new PendingApproval(id, Text(o, "title") ?? id, Text(o, "rule"), o["highRisk"]?.GetValueKind() == JsonValueKind.True,
                Text(o, "requestedByName"), o["createdAt"]?.GetValueKind() == JsonValueKind.String && DateTimeOffset.TryParse(Text(o, "createdAt"),
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at : default, o)
            : null;

    internal static string? Text(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>
/// The server's answer to an approvals call: the value, or why not (<see cref="Code"/> is an <see cref="McpErrorCodes"/>
/// value, or <see cref="ApprovalCodes.SignInAgain"/> for a high-risk request, with <see cref="DashboardUrl"/> to answer it there).
/// </summary>
public sealed record ApprovalsResult<T>(T? Value, string? Code = null, string? Message = null, string? DashboardUrl = null)
{
    public bool Succeeded => Code is null;

    internal ApprovalsResult<TOther> As<TOther>() => new(default, Code, Message, DashboardUrl);
}

/// <summary>
/// Approvals (the original issue) as the signed-in person (the original issue): the person's own mbus sign-in (<see cref="CliSession"/>), never the
/// machine's key, so only a person answers and the audit log says who. The server keeps the rest of the rules: only
/// the people a request was sent to may answer, and a high-risk one needs a fresh sign-in in the dashboard (the original issue).
/// </summary>
public sealed class ApprovalsClient(CliSession session, string workspaceId)
{
    /// <summary>The client for this machine's server and workspace, or null when nobody is signed in to mbus as themselves.</summary>
    public static ApprovalsClient? For(CommandContext context) =>
        context.Settings.Server is { } server && context.Settings.Stored.WorkspaceId is { } workspace
            && new CliSession(context.Host, context.Store, server) is { Saved: not null } session
            ? new ApprovalsClient(session, workspace)
            : null;

    /// <summary>What to tell someone who isn't signed in as themselves.</summary>
    public const string NotSignedIn = "Sign in to mbus as yourself first: run mbus login. Only a person can answer an approval, not the machine.";

    private string Base => $"api/w/{Uri.EscapeDataString(workspaceId)}/approvals";

    /// <summary>The pending approvals this person may answer now, oldest first (the order a list is numbered in).</summary>
    public async Task<ApprovalsResult<IReadOnlyList<PendingApproval>>> PendingAsync(CancellationToken ct)
    {
        var answer = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Base + "?status=pending"), ct).ConfigureAwait(false);
        if (!answer.Succeeded) return answer.As<IReadOnlyList<PendingApproval>>();
        var items = (answer.Value as JsonArray ?? [])
            .Where(n => n is JsonObject o && o["canDecide"]?.GetValueKind() == JsonValueKind.True)
            .Select(PendingApproval.From).OfType<PendingApproval>()
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id, StringComparer.Ordinal)
            .ToList();
        return new(items);
    }

    /// <summary>Approves or declines one. The server says no to anyone it wasn't sent to and to anything already answered.</summary>
    public async Task<ApprovalsResult<PendingApproval>> DecideAsync(string approvalId, bool approve, string? comment, CancellationToken ct)
    {
        var path = $"{Base}/{Uri.EscapeDataString(approvalId)}/{(approve ? "approve" : "reject")}";
        var body = new JsonObject { ["comment"] = comment }.ToJsonString();
        var answer = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        }, ct).ConfigureAwait(false);
        if (!answer.Succeeded) return answer.As<PendingApproval>();
        return PendingApproval.From(answer.Value) is { } decided
            ? new(decided)
            : new(null, McpErrorCodes.Internal, "The server sent an answer mbus doesn't understand.");
    }

    private async Task<ApprovalsResult<JsonNode>> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        HttpResponseMessage? response;
        try
        {
            response = await session.SendAsync(() =>
            {
                var request = build();
                request.Headers.TryAddWithoutValidation(MbusHeaders.ClientComponent, "cli");
                request.Headers.TryAddWithoutValidation(MbusHeaders.ClientVersion, CliHost.Version);
                return request;
            }, ct).ConfigureAwait(false);
        }
        catch (CliException ex)
        {
            return new(null, McpErrorCodes.Unavailable, ex.Message);
        }
        if (response is null) return new(null, McpErrorCodes.Forbidden, "Your mbus sign-in has ended. " + NotSignedIn);
        using (response)
        {
            JsonNode? json = null;
            try
            {
                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                json = text.Length > 0 ? JsonNode.Parse(text) : null;
            }
            catch (JsonException)
            {
            }
            if (response.IsSuccessStatusCode)
                return json is null ? new(null, McpErrorCodes.Internal, "The server sent an answer mbus doesn't understand.") : new(json);

            if (await MessageBus.Cli.Server.SuspensionRefusal.ReadAsync(response, ct) is { } suspension)
                return new(null, "workspace-suspended", suspension);

            // Two error shapes: {error: message, code?} and {error: code, message}.
            var o = json as JsonObject;
            var message = (o is null ? null : PendingApproval.Text(o, "message")) ?? (o is null ? null : PendingApproval.Text(o, "error"));
            var code = o is null ? null : PendingApproval.Text(o, "code") ?? (PendingApproval.Text(o, "message") is not null ? PendingApproval.Text(o, "error") : null);
            if (code == ApprovalCodes.SignInAgain)
                return new(null, code, message, o is null ? null : PendingApproval.Text(o, "dashboardUrl"));
            code = response.StatusCode switch
            {
                HttpStatusCode.BadRequest => McpErrorCodes.InvalidInput,
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => McpErrorCodes.Forbidden,
                HttpStatusCode.NotFound => McpErrorCodes.NotFound,
                HttpStatusCode.Conflict => McpErrorCodes.Conflict,
                HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout => McpErrorCodes.Unavailable,
                _ => McpErrorCodes.Internal,
            };
            return new(null, code, message ?? $"The server answered {(int)response.StatusCode}.");
        }
    }
}
