using System.CommandLine;
using MessageBus.Cli.Agents;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Runner;
using MessageBus.Cli.Server;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Runner;
using System.Net.Http.Json;
using Spectre.Console;

namespace MessageBus.Cli.Commands;

/// <summary><c>mbus machine set-location &lt;label&gt;</c>: changes the Location label the dashboard shows.</summary>
internal static class MachineCommand
{
    public static Command Create(CliHost host, CommonOptions options)
    {
        var label = new Argument<string>("location") { Description = "Where this machine is, for example \"Mac mini\"." };
        var setLocation = new Command("set-location", "Change where the dashboard says this machine is.") { label };
        setLocation.SetAction(async (parse, ct) =>
        {
            var context = CommandContext.Load(host, parse, options);
            var location = parse.GetValue(label)!.Trim();
            var config = context.Settings.Stored with { Location = location.Length == 0 ? null : location };
            context.Store.Save(config);

            var settings = CliSettings.Resolve(host, config, parse.GetValue(options.Server));
            if (settings.Server is { } server && settings.CanAuthenticate(context.Store))
            {
                // The service takes the label from the machine's inventory report.
                var inventory = await AgentScanner.BuildInventoryAsync(host, config.MachineId, settings.Location, ct);
                using var client = ServerConnection.CreateAuthenticatedClient(host, settings, context.Store, "cli");
                await ServerConnection.PutInventoryAsync(client, server, inventory, ct);
                context.Console.MarkupLineInterpolated($"[green]Location set to {settings.Location ?? "(none)"}.[/]");
            }
            else
            {
                context.Console.MarkupLineInterpolated($"Location set to {config.Location ?? "(none)"}. The dashboard gets it when this machine signs in.");
            }
            return 0;
        });

        var tool = new Argument<string>("tool") { Description = "The prerequisite to set up (git only).", DefaultValueFactory = _ => "git" };
        var acknowledgeInterrupted = new Option<bool>("--acknowledge-interrupted")
        {
            Description = "Confirm you have finished or closed any Git installer, then clear the local safety pause. Installs nothing.",
        };
        var setup = new Command("setup", "Ask this machine's running runner to install Git after owner approval.") { tool, acknowledgeInterrupted };
        setup.SetAction(async (parse, ct) =>
        {
            if (Security.RunContext.IsInsideRun(host.GetEnvironmentVariable)) throw new CliException("Tool setup cannot run inside an agent task.");
            if (!string.Equals(parse.GetValue(tool), "git", StringComparison.OrdinalIgnoreCase)) throw new CliException("Only Git can be set up. Use mbus machine setup git.");
            if (parse.GetValue(acknowledgeInterrupted))
            {
                ToolSetupRequests.AcknowledgeInterrupted(host.ConfigDirectory);
                await host.Out.WriteLineAsync("You acknowledged that any Git installer has finished or been closed. The runner's safety pause is cleared; no installation was approved or started. Run mbus doctor to check Git.");
                return 0;
            }
            var context = CommandContext.Load(host, parse, options);
            if (context.Settings.Server is not { } server || !context.Settings.CanAuthenticate(context.Store)) throw ServerConnection.NotSignedIn();
            using var client = ServerConnection.CreateAuthenticatedClient(host, context.Settings, context.Store, "cli");
            using var start = await client.PostAsJsonAsync(ServiceRoutes.ToolSetupStart.TrimStart('/'), new RescanRequest(), MessageBusJson.Options, ct);
            if (await SuspensionRefusal.ReadAsync(start, ct) is { } suspension) throw new CliException(suspension);
            if (!start.IsSuccessStatusCode) throw new CliException($"The runner could not start Git setup (HTTP {(int)start.StatusCode}). Make sure this machine's runner is running with {WindowsGitSetup.OptInVariable}=on.");
            var state = await start.Content.ReadFromJsonAsync<ToolSetupReport>(MessageBusJson.Options, ct) ?? throw new CliException("The runner returned no setup state.");
            string? previous = null;
            while (true)
            {
                if (state.Detail != previous)
                {
                    await host.Out.WriteLineAsync(state.Detail ?? state.State);
                    if (state.Plan is { } plan) await host.Out.WriteLineAsync(ToolSetupCatalog.Command(plan));
                    previous = state.Detail;
                }
                if (state.State is not ("planning" or "awaiting-approval" or "installing")) return state.State is "succeeded" or "installed" ? 0 : 1;
                await Task.Delay(TimeSpan.FromSeconds(3), host.Time, ct);
                using var response = await client.GetAsync(ServiceRoutes.ToolSetupStatus.TrimStart('/'), ct);
                if (await SuspensionRefusal.ReadAsync(response, ct) is { } paused) throw new CliException(paused);
                if (!response.IsSuccessStatusCode) throw new CliException($"Couldn't read this runner's Git setup state (HTTP {(int)response.StatusCode}). Check the Runners page.");
                state = await response.Content.ReadFromJsonAsync<ToolSetupReport>(MessageBusJson.Options, ct) ?? throw new CliException("The runner returned no setup state.");
            }
        });
        return new Command("machine", "This machine's settings.") { setLocation, setup };
    }
}
