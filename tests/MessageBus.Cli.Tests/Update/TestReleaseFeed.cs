using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MessageBus.Cli.Update;
using MessageBus.Contracts.Releases;

namespace MessageBus.Cli.Tests.Update;

/// <summary>
/// A release feed folder for one test, signed with a key made for the test (no key is stored
/// anywhere), and a stand-in "mbus" next to it: a script that prints its version, so the updater's
/// real start check can run it (<c>.cmd</c> on Windows, <c>sh</c> elsewhere).
/// </summary>
internal sealed class TestReleaseFeed : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mbus-update-tests", Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public TestReleaseFeed()
    {
        Directory.CreateDirectory(ChannelFolder);
        Directory.CreateDirectory(InstallFolder);
    }

    /// <summary>The folder <c>MBUS_UPDATE_FEED</c> names (channels below it).</summary>
    public string FeedFolder => Path.Combine(_root, "feed");

    public string ChannelFolder => Path.Combine(FeedFolder, ReleaseFeed.DefaultChannel);

    public string InstallFolder => Path.Combine(_root, "install");

    /// <summary>The installed "mbus".</summary>
    public string Target => Path.Combine(InstallFolder, OperatingSystem.IsWindows() ? "mbus.cmd" : "mbus");

    public string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    /// <summary>A script that prints <paramref name="version"/> (and <paramref name="error"/> on stderr) and exits with <paramref name="exitCode"/>.</summary>
    public static string Script(string version, int exitCode = 0, string? error = null) => OperatingSystem.IsWindows()
        ? $"@echo off\r\necho {version}\r\n{(error is null ? "" : $"echo {error} 1>&2\r\n")}exit /b {exitCode}\r\n"
        : $"#!/bin/sh\necho '{version}'\n{(error is null ? "" : $"echo '{error}' >&2\n")}exit {exitCode}\n";

    /// <summary>Puts the "running" mbus in place.</summary>
    public string InstallCurrent(string version = "0.1.0")
    {
        File.WriteAllText(Target, Script(version));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return Target;
    }

    /// <summary>Publishes a release for this platform (<paramref name="script"/> instead of the version script); returns the binary's contents.</summary>
    public string Publish(string version, int startExitCode = 0, ECDsa? signWith = null, string? script = null)
    {
        var contents = script ?? Script(version, startExitCode);
        var name = $"mbus-{version}-{RuntimeInformation.RuntimeIdentifier}";
        var bytes = Encoding.UTF8.GetBytes(contents);
        File.WriteAllBytes(Path.Combine(ChannelFolder, name), bytes);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new ReleaseManifest
        {
            Version = version,
            Channel = ReleaseFeed.DefaultChannel,
            Files =
            [
                new ReleaseFile { Rid = "linux-arm64", Name = "mbus-other", Size = 1, Sha256 = new string('0', 64) },
                new ReleaseFile { Rid = RuntimeInformation.RuntimeIdentifier, Name = name, Size = bytes.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) },
            ],
        }, UpdateJsonContext.Default.ReleaseManifest);
        File.WriteAllBytes(Path.Combine(ChannelFolder, ReleaseFeed.ManifestFile), manifest);
        var signature = (signWith ?? _key).SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        File.WriteAllText(Path.Combine(ChannelFolder, ReleaseFeed.SignatureFile), Base64Url.EncodeToString(signature));
        // Each version's own copy, for pinning (the original issue), as eng/mbus-release/make-feed.py writes it.
        File.WriteAllBytes(Path.Combine(ChannelFolder, ReleaseFeed.VersionManifestFile(version)), manifest);
        File.WriteAllText(Path.Combine(ChannelFolder, ReleaseFeed.VersionSignatureFile(version)), Base64Url.EncodeToString(signature));
        return contents;
    }

    /// <summary>Changes the published manifest after it was signed.</summary>
    public void EditManifest(Func<string, string> edit)
    {
        var path = Path.Combine(ChannelFolder, ReleaseFeed.ManifestFile);
        File.WriteAllText(path, edit(File.ReadAllText(path)));
    }

    /// <summary>Serves one version's signed manifest under another version's name.</summary>
    public void CopyVersionManifest(string from, string to)
    {
        File.Copy(Path.Combine(ChannelFolder, ReleaseFeed.VersionManifestFile(from)), Path.Combine(ChannelFolder, ReleaseFeed.VersionManifestFile(to)), overwrite: true);
        File.Copy(Path.Combine(ChannelFolder, ReleaseFeed.VersionSignatureFile(from)), Path.Combine(ChannelFolder, ReleaseFeed.VersionSignatureFile(to)), overwrite: true);
    }

    /// <summary>Changes the published binary after its hash was signed (same length).</summary>
    public void EditBinary(string version)
    {
        var path = Path.Combine(ChannelFolder, $"mbus-{version}-{RuntimeInformation.RuntimeIdentifier}");
        var bytes = File.ReadAllBytes(path);
        bytes[^3] ^= 0x01;
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>What is next to the target other than the target itself and its versions folder (abandoned downloads, links).</summary>
    public string[] Leftovers() =>
        [.. Directory.EnumerateFileSystemEntries(InstallFolder).Select(Path.GetFileName).Where(f => f != Path.GetFileName(Target) && f != VersionedInstall.FolderName).Order()!];

    /// <summary>The installed versions' folders (the original issue), by name.</summary>
    public string[] Versions() => Directory.Exists(VersionedInstall.VersionsFolder(Target))
        ? [.. Directory.EnumerateFileSystemEntries(VersionedInstall.VersionsFolder(Target)).Select(Path.GetFileName).Order(StringComparer.Ordinal)!]
        : [];

    /// <summary>Where <paramref name="version"/>'s file is installed.</summary>
    public string VersionFile(string version) => Path.Combine(VersionedInstall.VersionsFolder(Target), version, Path.GetFileName(Target));

    public void Dispose()
    {
        _key.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
