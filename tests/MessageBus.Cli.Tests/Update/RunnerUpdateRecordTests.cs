using System.Text.Json;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Cli.Update;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Releases;
using Xunit;

namespace MessageBus.Cli.Tests.Update;

public sealed class RunnerUpdateRecordTests
{
    [Theory]
    [InlineData("0.1.0", "0.2.0", "0.1.0", RunnerUpdateOutcome.Updated, RunnerUpdateOutcome.RolledBack)]
    [InlineData("0.1.0", "0.2.0", "0.2.0+build", RunnerUpdateOutcome.Updated, RunnerUpdateOutcome.Updated)]
    [InlineData("0.1.0", "0.2.0", "0.3.0", RunnerUpdateOutcome.Updated, null)]
    [InlineData("0.1.0", "0.2.0", "0.3.0", RunnerUpdateOutcome.RolledBack, null)]
    [InlineData("0.1.0", "0.2.0", "0.3.0", RunnerUpdateOutcome.NotInstalled, null)]
    [InlineData("0.1.0", "0.2.0", "0.2.0-preview.3", RunnerUpdateOutcome.Updated, RunnerUpdateOutcome.RolledBack)]
    [InlineData("0.1.0", "0.2.0", "unknown", RunnerUpdateOutcome.Updated, RunnerUpdateOutcome.Updated)]
    [InlineData("0.3.0", "0.2.0", "0.3.0+build", RunnerUpdateOutcome.Updated, RunnerUpdateOutcome.RolledBack)]
    [InlineData("0.3.0", "0.2.0", "0.2.0", RunnerUpdateOutcome.Updated, RunnerUpdateOutcome.Updated)]
    [InlineData("0.3.0", "0.2.0", "0.3.0", RunnerUpdateOutcome.NotInstalled, RunnerUpdateOutcome.NotInstalled)]
    [InlineData("0.3.0", "0.2.0", "0.3.0", RunnerUpdateOutcome.RolledBack, RunnerUpdateOutcome.RolledBack)]
    [InlineData("0.3.0", "0.2.0", "0.2.1", RunnerUpdateOutcome.NotInstalled, RunnerUpdateOutcome.NotInstalled)]
    [InlineData("0.3.0", "0.2.0", "0.4.0", RunnerUpdateOutcome.NotInstalled, null)]
    [InlineData("0.3.0", "0.2.0", "0.4.0", RunnerUpdateOutcome.RolledBack, null)]
    [InlineData("0.1.0", "0.1.1-dev.1", "0.1.1-preview.3", RunnerUpdateOutcome.RolledBack, null)]
    [InlineData("0.1.0", null, "0.3.0", RunnerUpdateOutcome.NotInstalled, RunnerUpdateOutcome.NotInstalled)]
    public void Reports_rollbacks_and_clears_only_versions_newer_than_both_recorded_versions(
        string fromVersion, string? toVersion, string runningVersion, RunnerUpdateOutcome outcome, RunnerUpdateOutcome? expected)
    {
        using var machine = new TestMachine();
        var record = RunnerUpdateRecord.In(machine.MbusFolder);
        record.Save(new RunnerUpdateReport
        {
            Outcome = outcome, FromVersion = fromVersion, ToVersion = toVersion, At = machine.Time.GetUtcNow(),
        }, TextWriter.Null, toolInstall: null);

        var report = RunnerUpdateRecord.In(machine.MbusFolder).Load(runningVersion, toolInstall: null);

        Assert.Equal(expected, report?.Outcome);
        if (expected == RunnerUpdateOutcome.RolledBack && outcome == RunnerUpdateOutcome.Updated)
            Assert.Contains($"runner started again on mbus {runningVersion}", report!.Reason);
        Assert.Equal(expected is not null, File.Exists(Path.Combine(machine.MbusFolder, RunnerUpdateRecord.FileName)));
    }

    [Theory]
    [InlineData("standalone", "standalone", true)]
    [InlineData("standalone", "dotnet-tool", false)]
    [InlineData("dotnet-tool", "dotnet-tool", true)]
    [InlineData("dotnet-tool", "standalone", false)]
    [InlineData(null, "standalone", true)]
    [InlineData(null, "dotnet-tool", false)]
    public void A_failed_first_update_survives_a_restart_only_for_the_same_install_kind(
        string? savedInstall, string runningInstall, bool keepReport)
    {
        using var machine = new TestMachine();
        var record = RunnerUpdateRecord.In(machine.MbusFolder);
        var original = new RunnerUpdateReport
        {
            Outcome = RunnerUpdateOutcome.NotInstalled, FromVersion = "0.1.0", ToVersion = "0.2.0",
            Reason = "The download's signature did not match.", At = machine.Time.GetUtcNow(),
        };
        var path = Path.Combine(machine.MbusFolder, RunnerUpdateRecord.FileName);
        if (savedInstall is null)
            machine.WriteHomeFile(Path.Combine(".mbus", RunnerUpdateRecord.FileName), MessageBusJson.Serialize(original));
        else
        {
            record.Save(original, TextWriter.Null, Tool(savedInstall));
            using var stored = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(savedInstall, stored.RootElement.GetProperty("install").GetString());
        }

        var report = RunnerUpdateRecord.In(machine.MbusFolder).Load("0.1.0", Tool(runningInstall));

        Assert.Equal(keepReport ? original : null, report);
        Assert.Equal(keepReport, File.Exists(path));
    }

    private static DotnetToolInstall? Tool(string kind) => kind == "dotnet-tool" ? new(null, false) : null;
}
