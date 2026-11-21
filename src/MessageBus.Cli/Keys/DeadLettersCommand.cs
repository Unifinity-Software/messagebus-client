using System.CommandLine;
using System.Net.Http.Headers;
using System.Text;
using MessageBus.Attachments.Client;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Hosting;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Crypto;
using MessageBus.Mcp;
using Spectre.Console;

namespace MessageBus.Cli.Keys;

/// <summary>
/// <c>mbus deadletters</c> (the original issue). The dashboard lists, replays and discards dead letters; this is for what only
/// a member's computer can do: send an end-to-end encrypted message to another role. Its body is sealed for its
/// own role's key, which the service never holds, so this computer (which holds that key) opens it and seals it
/// again for the new role's key.
/// <list type="bullet">
/// <item><c>list [--role r]</c>: the dead letters, newest first, with the id <c>replay</c> takes.</item>
/// <item><c>replay --to-role r (--from-role r | id…)</c>: sends them to role <c>r</c>, sealed again where needed.</item>
/// </list>
/// </summary>
internal static class DeadLettersCommand
{
    public static Command Create(CliHost host, CommonOptions options)
    {
        var listRole = new Option<string?>("--role") { Description = "Only this role's dead letters." };
        var list = new Command("list", "Show the dead letters, newest first.") { listRole };
        list.SetAction((parse, ct) => KeysCommand.WithKeysAsync(host, parse, options, async (keys, http, context) =>
        {
            var letters = await ListAsync(http, keys.WorkspaceId, parse.GetValue(listRole), ct);
            if (letters.Count == 0)
            {
                context.Console.WriteLine("No dead letters.");
                return 0;
            }
            var table = new Table().Border(TableBorder.Rounded).AddColumn("Title").AddColumn("Role").AddColumn("Why").AddColumn("When").AddColumn("Id");
            foreach (var letter in letters)
                table.AddRow(new Text(letter.Envelope.Title ?? letter.Envelope.Type), new Text(letter.Role ?? "-"),
                    new Text(MessageBusJson.Serialize(letter.Reason).Trim('"')), new Text($"{letter.DeadLetteredAt:yyyy-MM-dd HH:mm}"), new Text(letter.Id ?? ""));
            context.Console.Write(table);
            return 0;
        }, ct));

        var toRole = new Option<string>("--to-role") { Description = "The role that gets them.", Required = true };
        var fromRole = new Option<string?>("--from-role") { Description = "Replay every dead letter of this role." };
        var ids = new Argument<string[]>("id") { Description = "Dead letters to replay (ids from mbus deadletters list).", Arity = ArgumentArity.ZeroOrMore };
        var replay = new Command("replay", "Send dead letters to another role, encrypted again for its key where needed.") { toRole, fromRole, ids };
        replay.SetAction((parse, ct) => KeysCommand.WithKeysAsync(host, parse, options, async (keys, http, context) =>
        {
            var to = parse.GetValue(toRole)!.Trim();
            var named = parse.GetValue(ids) ?? [];
            var from = parse.GetValue(fromRole);
            if (named.Length == 0 && from is null) throw new CliException("Name the dead letters to replay, or a role with --from-role.");
            var letters = (await ListAsync(http, keys.WorkspaceId, from, ct))
                .Where(l => named.Length == 0 || named.Contains(l.Id, StringComparer.Ordinal)).ToList();
            if (named.FirstOrDefault(id => letters.All(l => l.Id != id)) is { } missing)
                throw new CliException($"There's no dead letter {missing}{(from is null ? "" : $" in {from}")}. See: mbus deadletters list");
            if (letters.Count == 0)
            {
                context.Console.WriteLine($"{from} has no dead letters.");
                return 0;
            }

            var failed = 0;
            using var links = new HttpClient { Timeout = TimeSpan.FromHours(1) };
            var attachments = new AttachmentClient(http, links, keys.WorkspaceId);
            foreach (var letter in letters)
            {
                var title = letter.Envelope.Title ?? letter.Envelope.Type;
                try
                {
                    var target = new RoleTarget { Role = to };
                    var recipient = await TaskReseal.PrepareReplayAsync(keys, http, letter.Envelope, target, ct);
                    var (sealedBody, files) = await TaskReseal.ResealAsync(keys, http, attachments, letter.Envelope, target, ct, recipientKey: recipient);
                    var input = new ReplayDeadLetterInput { ToRole = to, SealedBody = sealedBody, Attachments = files };
                    using var content = new StringContent(MessageBusJson.Serialize(input), Encoding.UTF8);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                    using var response = await http.PostAsync($"api/w/{Uri.EscapeDataString(keys.WorkspaceId)}/deadletters/{Uri.EscapeDataString(letter.Id!)}/replay", content, ct);
                    if (!response.IsSuccessStatusCode) throw new CliException(await ErrorTextAsync(response, ct));
                    context.Console.MarkupLineInterpolated($"[green]Sent \"{title}\" to {to}.[/]");
                }
                catch (Exception e) when (e is CliException or MissingKeyException or McpToolException or AttachmentTransferException)
                {
                    failed++;
                    var why = e switch
                    {
                        MissingKeyException => "this computer doesn't hold every key it was encrypted for. Run this on a computer holding the original role/workspace keys.",
                        McpToolException m => m.Error.Message,
                        AttachmentTransferException a => "an attachment couldn't be encrypted again: " + a.Message,
                        _ => e.Message,
                    };
                    context.Console.MarkupLineInterpolated($"[red]Couldn't send \"{title}\":[/] {why}");
                }
            }
            return failed == 0 ? 0 : 1;
        }, ct));

        return new Command("deadletters", "Dead letters: list them, and send encrypted ones to another role.") { list, replay };
    }

    private static async Task<IReadOnlyList<DeadLetter>> ListAsync(HttpClient http, string workspaceId, string? role, CancellationToken ct)
    {
        var query = role is null ? "?max=1000" : $"?max=1000&role={Uri.EscapeDataString(role)}";
        using var response = await http.GetAsync($"api/w/{Uri.EscapeDataString(workspaceId)}/deadletters{query}", ct);
        if (!response.IsSuccessStatusCode) throw new CliException("Couldn't list the dead letters: " + await ErrorTextAsync(response, ct));
        return MessageBusJson.Deserialize<IReadOnlyList<DeadLetter>>(await response.Content.ReadAsStringAsync(ct)) ?? [];
    }

    /// <summary>The service's own explanation when it sent one (an <see cref="McpError"/>), else the status.</summary>
    private static async Task<string> ErrorTextAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (await MessageBus.Cli.Server.SuspensionRefusal.ReadAsync(response, ct) is { } suspension) return suspension;
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            if (MessageBusJson.Deserialize<McpError>(text) is { Message.Length: > 0 } error) return error.Message;
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return $"the service answered {(int)response.StatusCode}.";
    }
}
