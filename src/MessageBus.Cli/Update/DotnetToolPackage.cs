using System.IO.Compression;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using NuGet.Versioning;

namespace MessageBus.Cli.Update;

internal sealed record DotnetToolPackage(string Id, string Version, string StoreEntry)
{
    internal const string IdentityUnknown = "The running tool's package ID and version could not be verified in the dotnet tool store.";

    internal static string StorePath(string tools, string id) => Path.Combine(tools, ".store", id.ToLowerInvariant());

    internal static string ArchiveName(string id, string version) => $"{id}.{version}.nupkg";

    internal static int? Compare(string? a, string? b) =>
        NuGetVersion.TryParse(a, out var first) && NuGetVersion.TryParse(b, out var second) ? first.CompareTo(second) : null;

    // Called by the detached applier, which runs outside the store. A healthy shim and a single
    // installed entry are both required, since the shim's informational version can be unrelated.
    internal static string? InstalledVersion(string tools, string id)
    {
        var store = StorePath(tools, id);
        if (!Directory.Exists(store)) return null;
        var entries = Directory.EnumerateDirectories(store).Take(2).ToArray();
        return entries.Length == 1 && NuGetVersion.TryParse(Path.GetFileName(entries[0]), out var version)
            ? version.ToNormalizedString() : null;
    }

    internal FileStream OpenRecovery(string entryAssembly, out string sha512)
    {
        var packageDirectory = Path.Combine(StoreEntry, Id.ToLowerInvariant(), Version.ToLowerInvariant());
        var archivePath = Path.Combine(packageDirectory, ArchiveName(Id, Version).ToLowerInvariant());
        if (!File.Exists(archivePath))
            throw new IOException($"The recovery package for {Id} {Version} is missing from the dotnet tool store.");
        var checksumPath = archivePath + ".sha512";
        if (!File.Exists(checksumPath))
            throw new IOException($"The recovery package for {Id} {Version} has no NuGet SHA-512 checksum in the dotnet tool store.");
        var expected = File.ReadAllText(checksumPath).Trim();
        var stream = File.OpenRead(archivePath);
        try
        {
            sha512 = Checksum(stream);
            stream.Position = 0;
            var entryPoint = Path.GetRelativePath(packageDirectory, entryAssembly).Replace(Path.DirectorySeparatorChar, '/');
            VerifyArchive(stream, packageDirectory, entryPoint, expected, sha512);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    internal static string Checksum(Stream stream) => Convert.ToBase64String(SHA512.HashData(stream));

    private void VerifyArchive(Stream stream, string packageDirectory, string entryPoint, string expected, string sha512)
    {
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var manifest = archive.Entries.SingleOrDefault(item => !item.FullName.Contains('/')
                && item.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            if (manifest is null) throw new InvalidDataException("The archive has no package manifest.");
            using var manifestStream = manifest.Open();
            var package = XDocument.Load(manifestStream).Root;
            var metadata = package?.Element(package.Name.Namespace + "metadata");
            if (metadata is null || (string?)metadata.Element(metadata.Name.Namespace + "id") is not { } manifestId
                || !Id.Equals(manifestId, StringComparison.OrdinalIgnoreCase)
                || Compare(Version, (string?)metadata.Element(metadata.Name.Namespace + "version")) != 0
                || archive.GetEntry(entryPoint) is null)
                throw new InvalidDataException("The archive does not match the installed tool.");
            if (sha512 == expected) return;

            // Some dotnet SDKs hash only the nuspec and write a second, independent archive
            // during extraction. In that layout both the manifest and the full copies must agree.
            using var nuspec = manifest.Open();
            if (Checksum(nuspec) != expected)
                throw new IOException($"The recovery package for {Id} {Version} could not be verified. Its SHA-512 does not match the stored checksum.");
            var extractedPath = Path.Combine(packageDirectory, manifestId + ".nupkg");
            if (!File.Exists(extractedPath))
                throw new IOException($"The recovery package for {Id} {Version} has a manifest-only checksum and the dotnet tool store's second archive is missing.");
            using var extracted = File.OpenRead(extractedPath);
            if (Checksum(extracted) != sha512)
                throw new IOException($"The recovery package for {Id} {Version} does not match the dotnet tool store's second archive.");
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or XmlException)
        {
            throw new IOException($"The recovery package for {Id} {Version} could not be verified.", ex);
        }
    }
}
