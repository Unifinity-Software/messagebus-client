using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using MessageBus.Contracts.Releases;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Update;

/// <summary>What <see cref="SelfUpdater"/> works with.</summary>
public sealed record SelfUpdaterOptions
{
    /// <summary>The release channel's files.</summary>
    public required IReleaseSource Source { get; init; }

    /// <summary>The release public key (<see cref="ReleaseKey.Resolve"/>).</summary>
    public required string PublicKey { get; init; }

    /// <summary>The version running now.</summary>
    public required string CurrentVersion { get; init; }

    /// <summary>The <c>mbus</c> file to replace; null when there is none (run from a build folder): then only checking works.</summary>
    public required string? TargetPath { get; init; }

    /// <summary>
    /// A version to install instead of the channel's latest (the original issue: the workspace pins it, or <c>mbus update --version</c>):
    /// its own signed manifest (<see cref="ReleaseFeed.VersionManifestFile"/>) is read, and installing it may go back
    /// to an older version. Null follows the channel.
    /// </summary>
    public string? PinnedVersion { get; init; }

    /// <summary>This platform's runtime identifier (default: the running one, for example <c>win-x64</c>).</summary>
    public string Rid { get; init; } = RuntimeInformation.RuntimeIdentifier;

    /// <summary>
    /// Starts the downloaded binary, before it replaces anything, to see that it works (default:
    /// <see cref="VersionStartCheck.RunAsync"/>: <c>&lt;path&gt; --version</c> must exit 0 and print the expected version
    /// within 30 seconds). Returns null when it did, else why not, in a few words for the person
    /// (<c>it exited with code 1 after 0.2 s; errors: "…"</c>); then nothing is replaced.
    /// </summary>
    public Func<string, string, CancellationToken, Task<string?>> StartCheck { get; init; } = VersionStartCheck.RunAsync;

    /// <summary>Progress lines.</summary>
    public TextWriter Log { get; init; } = TextWriter.Null;
}

/// <summary>The channel's latest release as checked against this mbus.</summary>
/// <param name="Manifest">The signed manifest.</param>
/// <param name="File">This platform's file in it.</param>
/// <param name="Comparison">Negative: the release is older than this mbus; zero: the same; positive: newer.</param>
/// <param name="MinimumVersion">The oldest mbus the server accepts, when it said.</param>
public sealed record ReleaseCheck(ReleaseManifest Manifest, ReleaseFile File, int Comparison, string? MinimumVersion);

/// <summary>How an update ended.</summary>
public enum UpdateResult
{
    /// <summary>The new version is in place.</summary>
    Installed,
    /// <summary>This mbus already is the channel's version.</summary>
    UpToDate,
}

/// <summary>A release that mbus won't install (bad signature, hash, platform, downgrade, failed start). Nothing changed.</summary>
public sealed class UpdateRefusedException(string message, Exception? inner = null, bool rolledBack = false) : Exception(message, inner)
{
    /// <summary>The new mbus didn't start on this machine, so the old one stays in place (the original issue).</summary>
    public bool RolledBack { get; } = rolledBack;
}

/// <summary>
/// <c>mbus update</c> and the runner's update: reads the channel's manifest, checks its signature with
/// the release key, picks this platform's file, refuses a downgrade unless allowed, downloads the file
/// into a folder of its own beside the installed one checking its size and SHA-256, starts it once there to see it works,
/// and only then makes <c>mbus</c> start it (<see cref="VersionedInstall"/>).
/// </summary>
/// <param name="options">Source, key, versions and paths.</param>
public sealed class SelfUpdater(SelfUpdaterOptions options) : IDisposable
{
    /// <summary>The largest manifest or signature file read (they are a few hundred bytes).</summary>
    private const int MaxSmallFile = 256 * 1024;

    /// <summary>Whether there is an mbus file to replace (not run from a build folder).</summary>
    public bool CanInstall => options.TargetPath is not null;

    /// <summary>The dotnet tool install the mbus file belongs to (the original issue): then dotnet updates it, never this.</summary>
    public DotnetToolInstall? DotnetTool { get; } = DotnetToolInstall.Find(options.TargetPath);

    /// <summary>The version being installed, once the signed release list said (else the pinned one); null before that.</summary>
    public string? TargetVersion { get; private set; }

    /// <summary>Releases the source's connection.</summary>
    public void Dispose() => (options.Source as IDisposable)?.Dispose();

    /// <summary>Reads and verifies the channel's manifest and compares it with this mbus.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public async Task<ReleaseCheck> CheckAsync(CancellationToken cancellationToken)
    {
        var pinned = options.PinnedVersion;
        TargetVersion = pinned;
        if (pinned is not null && !ReleaseFeed.IsPinnableVersion(pinned))
        {
            throw new UpdateRefusedException($"'{pinned}' isn't an mbus version (write it like 0.2.0).");
        }
        byte[] manifestBytes;
        string signature;
        try
        {
            manifestBytes = await ReadSmallAsync(pinned is null ? ReleaseFeed.ManifestFile : ReleaseFeed.VersionManifestFile(pinned), cancellationToken).ConfigureAwait(false);
            signature = System.Text.Encoding.UTF8.GetString(await ReadSmallAsync(
                pinned is null ? ReleaseFeed.SignatureFile : ReleaseFeed.VersionSignatureFile(pinned), cancellationToken).ConfigureAwait(false));
        }
        catch (FileNotFoundException ex)
        {
            throw new UpdateRefusedException($"No release found at {options.Source.Description}: {ex.Message}", ex);
        }

        bool valid;
        try
        {
            valid = ReleaseKey.Verify(options.PublicKey, manifestBytes, signature);
        }
        catch (CryptographicException ex)
        {
            throw new UpdateRefusedException($"The release key this mbus trusts can't be used: {ex.Message}", ex);
        }
        if (!valid)
        {
            throw new UpdateRefusedException($"The release list at {options.Source.Description} isn't signed by the messagebus.ai release key. Nothing was installed.");
        }

        ReleaseManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(manifestBytes, UpdateJsonContext.Default.ReleaseManifest);
        }
        catch (JsonException ex)
        {
            throw new UpdateRefusedException("The signed release list can't be read.", ex);
        }
        if (manifest is null || ReleaseVersion.Compare(manifest.Version, options.CurrentVersion) is not { } comparison)
        {
            throw new UpdateRefusedException($"The signed release list doesn't give a version this mbus ({options.CurrentVersion}) can compare.");
        }
        // A signed list of another version, served under this one's name, is refused: pinning means exactly this version.
        if (pinned is not null && ReleaseVersion.Compare(manifest.Version, pinned) != 0)
        {
            throw new UpdateRefusedException($"The signed release list for {pinned} is for mbus {manifest.Version}. Nothing was installed.");
        }
        TargetVersion = manifest.Version;
        var file = manifest.Files.FirstOrDefault(f => string.Equals(f.Rid, options.Rid, StringComparison.OrdinalIgnoreCase))
            ?? throw new UpdateRefusedException($"mbus {manifest.Version} has no build for this platform ({options.Rid}).");
        if (!ReleaseFeed.IsValidName(file.Name) || file.Size <= 0 || file.Sha256.Length != 64)
        {
            throw new UpdateRefusedException($"The release list's entry for {options.Rid} is incomplete.");
        }
        return new ReleaseCheck(manifest, file, comparison, options.Source.MinimumVersion);
    }

    /// <summary>Installs the channel's release over this mbus.</summary>
    /// <param name="allowDowngrade">Install an older release than this one.</param>
    /// <param name="cancellationToken">Cancels the download (before the swap).</param>
    /// <exception cref="UpdateRefusedException">Nothing was changed.</exception>
    public async Task<UpdateResult> InstallAsync(bool allowDowngrade, CancellationToken cancellationToken)
    {
        // Replacing mbus.exe inside dotnet's tool store would leave dotnet thinking the old version is installed.
        if (DotnetTool is { } tool)
        {
            throw new UpdateRefusedException(tool.RefusalMessage(options.PinnedVersion));
        }
        var check = await CheckAsync(cancellationToken).ConfigureAwait(false);
        var version = check.Manifest.Version;
        if (check.Comparison == 0)
        {
            return UpdateResult.UpToDate;
        }
        if (check.Comparison < 0 && !allowDowngrade && options.PinnedVersion is null)
        {
            throw new UpdateRefusedException($"The release ({version}) is older than this mbus ({options.CurrentVersion}). To install it anyway: mbus update --allow-downgrade");
        }

        var target = options.TargetPath
            ?? throw new UpdateRefusedException("This mbus runs from a build folder (dotnet run), so there is no mbus file to replace. Build it again instead.");
        if (!File.Exists(target))
        {
            throw new UpdateRefusedException($"There is no mbus at {target} to replace.");
        }
        await options.Log.WriteLineAsync($"Downloading mbus {version} ({check.File.Size / (1024.0 * 1024.0):0.0} MB) from {options.Source.Description}").ConfigureAwait(false);
        // the original issue: the new version gets a folder of its own beside mbus and stays at that path: a running mbus reads code it
        // hasn't used yet from its own file by path, so no file anything may be running from is ever replaced or moved.
        var file = VersionedInstall.NewVersionFile(target, version);
        try
        {
            await DownloadAsync(check.File, file, cancellationToken).ConfigureAwait(false);
            VersionedInstall.MakeRunnable(file, target);
        }
        catch
        {
            VersionedInstall.Discard(file);
            throw;
        }

        // Started where it will run from, before mbus names it (the original issue: a check after the switch was the updater's own first
        // use of code it hadn't loaded, which failed whenever the running file had been replaced).
        await options.Log.WriteLineAsync($"Checking that mbus {version} starts: {file} --version").ConfigureAwait(false);
        if (await options.StartCheck(file, version, CancellationToken.None).ConfigureAwait(false) is { } problem)
        {
            VersionedInstall.Discard(file);
            throw new UpdateRefusedException($"mbus {version} didn't start on this machine, so mbus {options.CurrentVersion} stays in place: {problem}.", rolledBack: true);
        }

        string? previous;
        try
        {
            previous = VersionedInstall.Switch(target, file, options.CurrentVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            VersionedInstall.Discard(file);
            throw new UpdateRefusedException($"Couldn't make {target} start the new mbus: {ex.Message}", ex);
        }
        VersionedInstall.CleanUp(target, file, previous);
        await options.Log.WriteLineAsync($"Installed mbus {version} (was {options.CurrentVersion}) in {Path.GetDirectoryName(file)}."
            + (previous is null ? "" : $" The one before stays in {Path.GetDirectoryName(previous)}.")).ConfigureAwait(false);
        return UpdateResult.Installed;
    }

    private async Task DownloadAsync(ReleaseFile file, string path, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long written = 0;
        var source = await options.Source.OpenAsync(file.Name, cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await using (output.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    written += read;
                    if (written > file.Size)
                    {
                        throw new UpdateRefusedException($"{file.Name} is larger than the signed release list says. Nothing was installed.");
                    }
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (written != file.Size || !string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdateRefusedException($"{file.Name} doesn't match the signed release list (its SHA-256 or size differs). Nothing was installed.");
        }
    }

    private async Task<byte[]> ReadSmallAsync(string name, CancellationToken cancellationToken)
    {
        var stream = await options.Source.OpenAsync(name, cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxSmallFile)
                {
                    throw new UpdateRefusedException($"{name} at {options.Source.Description} is too large to be a release list.");
                }
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
    }
}

/// <summary>The runner's part (the original issue): once every task has finished, install what the service asked for.</summary>
public interface IRunnerUpdater
{
    /// <summary>Installs the update; true when a new mbus is in place and the runner should exit to be restarted.</summary>
    /// <param name="request">What the service asked for (<see cref="RunnerUpdates.Latest"/> after a 4426 close).</param>
    /// <param name="cancellationToken">Stops the download.</param>
    Task<bool> InstallAsync(UpdateRequest request, CancellationToken cancellationToken);

    /// <summary>How the last <see cref="InstallAsync"/> ended (the original issue), for the Runners page; null when it says nothing.</summary>
    RunnerUpdateReport? LastResult => null;
}

/// <summary>Names for the runner's update requests.</summary>
public static class RunnerUpdates
{
    /// <summary>The version the runner asks itself for when the service closed its connection with 4426: whatever the channel has.</summary>
    public const string Latest = ReleaseFeed.LatestVersion;
}

/// <summary><see cref="IRunnerUpdater"/> with <see cref="SelfUpdater"/>: never a downgrade; failures are logged and leave the old mbus in place.</summary>
/// <param name="create">Makes the updater for the request: its channel, mirror and (unless <see cref="RunnerUpdates.Latest"/>) pinned version.</param>
/// <param name="log">Status lines.</param>
/// <param name="record">Where the result is kept for the runner to report when it next starts (the original issue); null keeps none.</param>
/// <param name="currentVersion">The version running now (for the record).</param>
/// <param name="time">The clock (for the record).</param>
public sealed class RunnerSelfUpdater(Func<UpdateRequest, SelfUpdater> create, TextWriter log, RunnerUpdateRecord? record = null,
    string? currentVersion = null, TimeProvider? time = null) : IRunnerUpdater
{
    /// <summary>Whether an update was installed (the runner then exits 75, or starts the new version itself: <see cref="Runner.RunnerRestart"/>).</summary>
    public bool Installed { get; private set; }

    /// <inheritdoc />
    public RunnerUpdateReport? LastResult { get; private set; }

    /// <inheritdoc />
    public async Task<bool> InstallAsync(UpdateRequest request, CancellationToken cancellationToken)
    {
        SelfUpdater? updater = null;
        var asked = request.Version is { Length: > 0 } v && v != RunnerUpdates.Latest ? v : null;
        try
        {
            // "latest": the channel's signed release, never older than this one. Any other version: the workspace pins
            // it (the original issue), so exactly that signed release is installed, older or not.
            updater = create(request);
            var result = await updater.InstallAsync(allowDowngrade: false, cancellationToken).ConfigureAwait(false);
            if (result == UpdateResult.UpToDate)
            {
                await log.WriteLineAsync("This mbus already is the release feed's version; nothing to install.").ConfigureAwait(false);
                // the original issue: said on the Runners page too, so asking for an update never ends in silence.
                var running = currentVersion ?? Hosting.CliHost.Version;
                Record(RunnerUpdateOutcome.NotInstalled, updater.TargetVersion ?? running, $"mbus {running} is already the newest release for this workspace.");
                return false;
            }
            Record(RunnerUpdateOutcome.Updated, updater.TargetVersion ?? asked, null);
            Installed = true;
            return true;
        }
        catch (Exception ex) when (ex is UpdateRefusedException or CliException or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            await log.WriteLineAsync($"Update not installed: {ex.Message}").ConfigureAwait(false);
            Record(ex is UpdateRefusedException { RolledBack: true } ? RunnerUpdateOutcome.RolledBack : RunnerUpdateOutcome.NotInstalled,
                updater?.TargetVersion ?? asked, ex.Message);
            return false;
        }
        finally
        {
            updater?.Dispose();
        }
    }

    private void Record(RunnerUpdateOutcome outcome, string? version, string? reason)
    {
        LastResult = new RunnerUpdateReport
        {
            Outcome = outcome,
            FromVersion = currentVersion ?? Hosting.CliHost.Version,
            ToVersion = version,
            Reason = reason,
            At = (time ?? TimeProvider.System).GetUtcNow(),
        };
        record?.Save(LastResult, log);
    }
}
