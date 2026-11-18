using System.Runtime.InteropServices;
using MessageBus.Cli.Commands;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Server;
using Spectre.Console;

namespace MessageBus.Cli.SignIn;

/// <summary>
/// <c>mbus login</c>'s sign-in (the original issue), like the Groundhog CLI's: mbus asks the server for a short code,
/// opens the dashboard's approval page (or prints its address, in a shell without a browser), the
/// person signs in to the dashboard as usual, checks the code matches the terminal and approves;
/// mbus then collects its own sign-in from the server. Nothing here talks to Microsoft.
/// </summary>
internal static class BrowserSignIn
{
    public static async Task<CliSessionFile> SignInAsync(CommandContext context, CliSession session, ServerInfoResponse info,
        string machineName, string? location, bool openBrowser, CancellationToken ct)
    {
        var host = context.Host;
        var console = context.Console;
        var serverText = CommandContext.ServerText(context.Settings.Server!);
        if (string.IsNullOrWhiteSpace(info.SignIn?.CliApprovalUrl) || !Uri.TryCreate(info.SignIn.CliApprovalUrl, UriKind.Absolute, out var approval))
        {
            throw new CliException($"{serverText} can't sign mbus in through the browser yet (it needs updating). "
                + "Use a runner enrollment token (mbus login --token <token>).");
        }

        var started = await session.StartAsync(new CliStartBody($"mbus {CliHost.Version}", machineName, location, DescribeOs()), ct);
        var url = new Uri($"{approval.AbsoluteUri.TrimEnd('/')}?code={Uri.EscapeDataString(started.UserCode)}");

        console.WriteLine();
        console.MarkupLine("To sign in, open this page, sign in to messagebus.ai and approve mbus:");
        console.MarkupLineInterpolated($"  [link]{url.AbsoluteUri}[/]");
        console.MarkupLineInterpolated($"Check the page shows this code: [bold]{started.UserCode}[/]");
        var opened = openBrowser && await host.Browser.OpenAsync(url, ct);
        console.MarkupLine(opened
            ? "Opened your browser. Waiting for you to approve..."
            : "Open it on any device where you can sign in. Waiting for you to approve...");

        var deadline = host.Time.GetUtcNow() + TimeSpan.FromSeconds(Math.Max(started.ExpiresIn, 1));
        var interval = TimeSpan.FromSeconds(Math.Max(started.Interval, 1));
        while (true)
        {
            var poll = await session.PollAsync(started.DeviceCode, ct);
            switch (poll.State)
            {
                case "approved":
                    var saved = await session.SaveApprovedAsync(poll, ct);
                    console.MarkupLineInterpolated($"[green]Approved[/] by {saved.AccountName ?? saved.AccountEmail ?? "you"}.");
                    return saved;
                case "denied":
                    throw new CliException("Sign-in was denied in the browser. Run mbus login again if that was a mistake.");
                case "expired":
                    throw new CliException("The sign-in code expired before it was approved. Run mbus login again for a new one.");
            }
            if (poll.Interval is > 0 and var seconds) interval = TimeSpan.FromSeconds(seconds);
            if (host.Time.GetUtcNow() + interval >= deadline)
            {
                throw new CliException("The sign-in code expired before it was approved. Run mbus login again for a new one.");
            }
            await Task.Delay(interval, host.Time, ct);
        }
    }

    /// <summary>"macOS arm64", shown on the approval page so the person recognises the machine.</summary>
    private static string DescribeOs()
    {
        var os = OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "Unknown OS";
        return $"{os} {RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
    }
}
