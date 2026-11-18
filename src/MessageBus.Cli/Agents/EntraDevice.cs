using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using MessageBus.Contracts.Security;

namespace MessageBus.Cli.Agents;

/// <summary>
/// This machine's Entra device id, sent at sign-in so the service can ask Entra (Microsoft Graph)
/// whether the device is managed and compliant (the original issue). Windows only, from <c>dsregcmd /status</c>:
/// <c>DeviceId</c> on an Entra-joined machine, else <c>WorkplaceDeviceId</c> on a registered one.
/// Null on other systems, or when the machine isn't joined to or registered with Entra.
/// </summary>
internal static partial class EntraDevice
{
    [GeneratedRegex(@"^\s*(?<kind>Workplace)?DeviceId\s*:\s*(?<id>[0-9a-fA-F-]{36})\s*$", RegexOptions.Multiline)]
    private static partial Regex DeviceIdLine();

    public static async Task<string?> IdAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var run = await CommandRunner.RunAsync("dsregcmd", ["/status"], ct, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        return run is { ExitCode: 0 } ? Parse(run.Output) : null;
    }

    /// <summary>
    /// Proof that this machine holds the device's Entra certificate key (the original issue), so the service can tell the device id
    /// isn't just copied from another computer: the certificate Entra issued at join or registration (issuer
    /// <c>MS-Organization-Access</c>, subject <c>CN=&lt;device id&gt;</c>) signs the new machine key. It reads the
    /// person's certificates, then the computer's, and changes nothing. Null when there is none this process can use
    /// (an Entra-joined computer's key may belong to the computer), or on other systems.
    /// </summary>
    public static DeviceProof? Prove(string deviceId, string machinePublicKey, DateTimeOffset now)
    {
        if (!OperatingSystem.IsWindows()) return null;
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            using var store = new X509Store(StoreName.My, location);
            try
            {
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            }
            catch (CryptographicException)
            {
                continue;
            }
            foreach (var certificate in store.Certificates)
            {
                using (certificate)
                {
                    if (!IsDeviceCertificate(certificate, deviceId, now)) continue;
                    try
                    {
                        return DeviceProofs.Create(certificate, deviceId, machinePublicKey, now);
                    }
                    catch (CryptographicException)
                    {
                        // A key this process may not use; try the next store.
                    }
                }
            }
        }
        return null;
    }

    /// <summary>Entra's certificate for this device: issued by MS-Organization-Access to <c>CN=&lt;device id&gt;</c>, in date, with its key here.</summary>
    internal static bool IsDeviceCertificate(X509Certificate2 certificate, string deviceId, DateTimeOffset now) =>
        certificate.HasPrivateKey
        && certificate.Issuer.Contains("CN=MS-Organization-Access", StringComparison.OrdinalIgnoreCase)
        && string.Equals(certificate.GetNameInfo(X509NameType.SimpleName, false), deviceId, StringComparison.OrdinalIgnoreCase)
        && certificate.NotBefore <= now && now <= certificate.NotAfter;

    /// <summary>The joined device's id from <c>dsregcmd /status</c>, else the registered one's.</summary>
    internal static string? Parse(string status)
    {
        var ids = DeviceIdLine().Matches(status)
            .Where(m => Guid.TryParse(m.Groups["id"].Value, out _))
            .OrderBy(m => m.Groups["kind"].Success)
            .Select(m => Guid.Parse(m.Groups["id"].Value).ToString());
        return ids.FirstOrDefault();
    }
}
