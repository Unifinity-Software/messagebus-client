using System.Diagnostics;

namespace MessageBus.Cli.SignIn;

/// <summary>Opens a page in the person's browser for <c>mbus login</c>.</summary>
public interface IBrowser
{
    /// <summary>Opens <paramref name="url"/>; false when there is no browser to open (an SSH session, a container).</summary>
    Task<bool> OpenAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>
/// The machine's default browser: <c>open</c> on macOS, <c>xdg-open</c> on Linux with a desktop,
/// the shell on Windows. Over SSH or without a display it opens nothing, and mbus just prints the
/// address for the person to open on another device.
/// </summary>
internal sealed class SystemBrowser(Func<string, string?> environment) : IBrowser
{
    public Task<bool> OpenAsync(Uri url, CancellationToken cancellationToken)
    {
        if (url.Scheme is not ("http" or "https") || Headless()) return Task.FromResult(false);
        try
        {
            var start = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }
                : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { ArgumentList = { url.AbsoluteUri }, UseShellExecute = false };
            using var process = Process.Start(start);
            return Task.FromResult(process is not null || OperatingSystem.IsWindows());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return Task.FromResult(false);
        }
    }

    private bool Headless()
    {
        if (!string.IsNullOrEmpty(environment("SSH_CONNECTION")) || !string.IsNullOrEmpty(environment("SSH_TTY"))) return true;
        return OperatingSystem.IsLinux()
            && string.IsNullOrEmpty(environment("DISPLAY")) && string.IsNullOrEmpty(environment("WAYLAND_DISPLAY"));
    }
}
