using System.CommandLine;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Server;
using MessageBus.Cli.SignIn;
using Spectre.Console;

namespace MessageBus.Cli.Commands;

/// <summary>
/// <c>mbus logout</c>: removes this machine from its workspace on the server (when the server can be
/// reached), ends the person's mbus sign-in there (the original issue: its whole refresh chain), then deletes this
/// machine's key and the saved sign-in, so nothing on it can act for you any more. The server URL,
/// name and location stay for the next <c>mbus login</c>.
/// </summary>
internal static class LogoutCommand
{
    public static Command Create(CliHost host, CommonOptions options)
    {
        var command = new Command("logout", "Sign this machine out: remove it from the server, end your sign-in and delete its key.");
        command.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.Load(host, parse, options);
            var stored = context.Settings.Stored;
            var signedIn = context.Store.LoadSession() is not null;
            var removed = await RemoveFromServerAsync(context, ct);
            var ended = await EndSignInAsync(context, ct);
            context.Store.DeleteKey();
            context.Store.Save(stored with { MachineId = null, WorkspaceId = null, WorkspaceName = null, KeyFingerprint = null, EnrolledAt = null });
            context.Console.MarkupLine(stored.MachineId is null && !signedIn
                ? "This machine wasn't signed in."
                : removed && ended
                    ? "Signed out. This machine is removed from the workspace, your sign-in is ended and its key is deleted."
                    : "Signed out on this machine. If it won't come back, remove it on the dashboard's Runners page and end its sign-in under My account.");
            return 0;
        });
        return command;
    }

    /// <summary>True when the server removed the machine (or there was none); false (with a note) when it couldn't be asked.</summary>
    private static async Task<bool> RemoveFromServerAsync(CommandContext context, CancellationToken ct)
    {
        var stored = context.Settings.Stored;
        if (stored.MachineId is not { } machineId || stored.WorkspaceId is not { } workspaceId
            || context.Settings.Server is not { } server || context.Store.LoadKeyPem() is null)
        {
            return stored.MachineId is null;
        }
        try
        {
            // Sign with the machine key even when MBUS_API_KEY is set: it is the machine being removed.
            var settings = context.Settings with { ApiKey = null };
            using var client = ServerConnection.CreateAuthenticatedClient(context.Host, settings, context.Store, "cli");
            await ServerConnection.RemoveMachineAsync(client, server, workspaceId, machineId, ct);
            return true;
        }
        catch (CliException ex)
        {
            context.Console.MarkupLineInterpolated($"[yellow]Couldn't tell the server: {ex.Message}[/]");
            return false;
        }
    }

    /// <summary>Ends the saved sign-in on the server and forgets it here; true when the server confirmed (or there was none).</summary>
    private static async Task<bool> EndSignInAsync(CommandContext context, CancellationToken ct)
    {
        var ended = true;
        if (context.Settings.Server is { } server)
        {
            string? problem;
            (ended, problem) = await new CliSession(context.Host, context.Store, server).LogoutAsync(ct);
            if (!ended)
            {
                context.Console.MarkupLineInterpolated($"[yellow]Couldn't end your sign-in on the server: {problem}[/]");
            }
        }
        // A sign-in saved for another server is forgotten all the same.
        context.Store.DeleteSession();
        return ended;
    }
}
