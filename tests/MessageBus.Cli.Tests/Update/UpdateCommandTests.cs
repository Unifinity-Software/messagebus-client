using System.Security.Cryptography;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Cli.Update;
using Xunit;

namespace MessageBus.Cli.Tests.Update;

/// <summary>
/// <c>mbus update</c> end to end on this machine: a signed feed folder, the real verification, swap
/// and start check, and a stand-in mbus script in a temp folder. The test feed uses versions ahead of the source build.
/// </summary>
public sealed class UpdateCommandTests : IDisposable
{
    private readonly TestMachine _machine = new();
    private readonly TestReleaseFeed _feed = new();
    private readonly string _installed;

    public UpdateCommandTests()
    {
        _installed = File.ReadAllText(_feed.InstallCurrent("1.0.0"));
        _machine.Env[UpdateEnvVars.Feed] = _feed.FeedFolder;
        _machine.Env[UpdateEnvVars.Target] = _feed.Target;
        _machine.Env[ReleaseKey.EnvVar] = _feed.PublicKey;
    }

    // the original issue: a running mbus reads code it hasn't used yet from its own file by path, so an update must never change the
    // file at a path anything may be running from. Each version gets a folder of its own, and mbus names the current one.
    [Fact]
    public async Task Each_release_goes_into_its_own_folder_that_never_changes_and_mbus_names_the_current_one()
    {
        var release = _feed.Publish("1.0.1");

        var run = await _machine.RunAsync("update");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(release, File.ReadAllText(_feed.Target));
        Assert.Equal(_feed.VersionFile("1.0.1"), VersionedInstall.Current(_feed.Target));
        // The plain file this install was moved into the layout from, kept as the version before (this test build's version).
        var before = Assert.Single(_feed.Versions(), v => v != "1.0.1");
        Assert.StartsWith("1.0.0", before);
        Assert.Equal(_installed, File.ReadAllText(_feed.VersionFile(before)));
        // What mbus connect writes into agents' MCP settings and mbus runner install into the service: mbus, not 1.0.1's file.
        Assert.Equal(_feed.Target, VersionedInstall.StableFor(_feed.VersionFile("1.0.1")));
        Assert.Empty(_feed.Leftovers());

        // The version before is still running somewhere (an agent's mbus mcp holds its file, as every mbus does: MarkRunning).
        using (new FileStream(_feed.VersionFile(before), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var next = _feed.Publish("1.1.0");
            Assert.Equal(0, (await _machine.RunAsync("update")).ExitCode);

            Assert.Equal(next, File.ReadAllText(_feed.Target));
            Assert.Equal(release, File.ReadAllText(_feed.VersionFile("1.0.1"))); // what a running 1.0.1 reads is untouched
            Assert.Equal([before, "1.0.1", "1.1.0"], _feed.Versions());
        }

        // Once nothing runs it, a version older than the one before goes.
        _feed.Publish("1.2.0");
        Assert.Equal(0, (await _machine.RunAsync("update")).ExitCode);
        Assert.Equal(["1.1.0", "1.2.0"], _feed.Versions());
        Assert.Empty(_feed.Leftovers());
    }

    public static TheoryData<string> Tampering => ["manifest edited", "binary edited", "signed by another key"];

    [Theory]
    [MemberData(nameof(Tampering))]
    public async Task A_release_that_does_not_match_its_signature_is_refused_and_nothing_changes(string tampering)
    {
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _feed.Publish("1.0.1", signWith: tampering == "signed by another key" ? otherKey : null);
        if (tampering == "manifest edited")
            _feed.EditManifest(json => json.Replace("\"1.0.1\"", "\"1.1.0\""));
        if (tampering == "binary edited")
            _feed.EditBinary("1.0.1");

        var run = await _machine.RunAsync("update");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains(tampering == "binary edited" ? "doesn't match the signed release list" : "isn't signed by the messagebus.ai release key", run.Error);
        Assert.Equal(_installed, File.ReadAllText(_feed.Target));
        Assert.Empty(_feed.Leftovers());
        Assert.Empty(_feed.Versions());
    }

    [Fact]
    public async Task An_older_release_is_refused_unless_a_downgrade_is_allowed()
    {
        var older = _feed.Publish("0.0.9");

        var refused = await _machine.RunAsync("update");
        Assert.NotEqual(0, refused.ExitCode);
        Assert.Equal(_installed, File.ReadAllText(_feed.Target));

        var allowed = await _machine.RunAsync("update", "--allow-downgrade");
        Assert.Equal(0, allowed.ExitCode);
        Assert.Equal(older, File.ReadAllText(_feed.Target));
    }

    [Fact]
    public async Task A_pinned_version_is_installed_even_if_older_but_only_from_its_own_signed_list()
    {
        var pinned = _feed.Publish("0.0.9");
        _feed.Publish("1.0.1");

        var run = await _machine.RunAsync("update", "--version", "0.0.9");
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(pinned, File.ReadAllText(_feed.Target));

        // A genuinely signed list, but of another version, served under the pinned one's name.
        _feed.CopyVersionManifest(from: "1.0.1", to: "0.0.9");
        var swapped = await _machine.RunAsync("update", "--version", "0.0.9");
        Assert.NotEqual(0, swapped.ExitCode);
        Assert.Contains("is for mbus 1.0.1", swapped.Error);
        Assert.Equal(pinned, File.ReadAllText(_feed.Target));
    }

    // A failed start check says what the new mbus did, so the next failure on someone's machine explains itself.
    // Nothing is replaced: the download is checked where it is and then deleted with its folder.
    [Theory]
    [InlineData("exit code")]
    [InlineData("other version")]
    [InlineData("signal")]
    public async Task A_release_that_fails_to_start_is_never_put_in_place_and_says_why(string failure)
    {
        Assert.SkipWhen(failure == "signal" && OperatingSystem.IsWindows(), "Windows programs aren't stopped by signals.");
        var (script, shown) = failure switch
        {
            "exit code" => (TestReleaseFeed.Script("1.0.1", 3, error: "libssl not found"), new[] { "code 3", "libssl not found" }),
            "other version" => (TestReleaseFeed.Script("0.1.9"), ["\"0.1.9\""]),
            _ => ("#!/bin/sh\nkill -KILL $$\n", ["code 137", "SIGKILL"]),
        };
        _feed.Publish("1.0.1", script: script);

        var run = await _machine.RunAsync("update");

        Assert.NotEqual(0, run.ExitCode);
        Assert.Contains("didn't start", run.Error);
        Assert.All(shown, s => Assert.Contains(s, run.Error));
        Assert.Equal(_installed, File.ReadAllText(_feed.Target));
        Assert.Empty(_feed.Leftovers());
        Assert.Empty(_feed.Versions());
    }

    // The bug behind "didn't start" on a Mac: a single-file mbus reads code it hasn't used yet from its own path, so once
    // that path names the new file, the updater itself can't start the check. It must run while the old file is in place.
    [Fact]
    public async Task The_new_mbus_is_started_before_it_replaces_the_running_one()
    {
        _feed.Publish("1.0.1");
        string? installedDuringCheck = null;
        using var updater = new SelfUpdater(new SelfUpdaterOptions
        {
            Source = new FolderReleaseSource(_feed.ChannelFolder),
            PublicKey = _feed.PublicKey,
            CurrentVersion = "1.0.0",
            TargetPath = _feed.Target,
            StartCheck = (path, version, ct) =>
            {
                installedDuringCheck = File.ReadAllText(_feed.Target);
                return VersionStartCheck.RunAsync(path, version, ct);
            },
        });

        Assert.Equal(UpdateResult.Installed, await updater.InstallAsync(allowDowngrade: false, TestContext.Current.CancellationToken));
        Assert.Equal(_installed, installedDuringCheck);
    }

    [Fact]
    public async Task A_new_mbus_that_cannot_be_started_is_reported_not_thrown_so_the_update_rolls_back()
    {
        var problem = await VersionStartCheck.RunAsync(Path.Combine(_feed.InstallFolder, "missing-mbus"), "1.0.1", TestContext.Current.CancellationToken);

        Assert.StartsWith("it couldn't be started", problem);
    }

    [Fact]
    public async Task An_mbus_installed_with_dotnet_tool_leaves_updating_to_dotnet()
    {
        // dotnet's layout (the original issue): <tools>/.store/<id>/<version>/<id>.<rid>/<version>/tools/any/<rid>/mbus.exe, its settings file beside it.
        var tools = Path.Combine(_feed.InstallFolder, "tools");
        var folder = Path.Combine(tools, ".store", "messagebus.ai.cli", "1.0.0", "messagebus.ai.cli.win-x64", "1.0.0", "tools", "any", "win-x64");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, DotnetToolInstall.SettingsFile), "<DotNetCliTool Version=\"2\" />");
        var target = Path.Combine(folder, Path.GetFileName(_feed.Target));
        File.Copy(_feed.Target, target);
        _machine.Env[UpdateEnvVars.Target] = target;
        // NuGet installs must not need a standalone feed or its signing key to explain how to update.
        _machine.Env.Remove(ReleaseKey.EnvVar);
        _machine.Env[UpdateEnvVars.Feed] = Path.Combine(_feed.InstallFolder, "no-feed-exists");

        var source = Path.Combine(_feed.InstallFolder, "nuget");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "MessageBus.Ai.Cli.1.0.1.nupkg"), "test");
        _machine.Env["MBUS_UPDATE_NUGET_SOURCE"] = source;
        _machine.RunnerServices = () => new MessageBus.Cli.Runner.Service.ServiceManager(
            MessageBus.Cli.Runner.Service.ServicePlatform.Other, "test", 0, (_, _) => throw new Exception());
        var check = await _machine.RunAsync("update", "--check");
        var install = await _machine.RunAsync("update");

        Assert.Contains("mbus 1.0.1 is available", check.Output);
        Assert.Equal(0, install.ExitCode);
        Assert.Contains("dotnet tool update --tool-path", install.Output);
        Assert.Contains(tools, install.Output);
        Assert.Equal(_installed, File.ReadAllText(target));
        Assert.Equal(2, Directory.EnumerateFiles(folder).Count()); // nothing downloaded or swapped beside it
    }

    public void Dispose()
    {
        _feed.Dispose();
        _machine.Dispose();
    }
}
