using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Runner;
using Xunit;

namespace MessageBus.Contracts.Tests;

public sealed class ToolSetupCatalogTests
{
    [Theory]
    [InlineData("Command Line Tools for Xcode-26.1; touch /tmp/injected", "26.1")]
    [InlineData("Command Line Tools for Xcode-26.1\" with administrator privileges", "26.1")]
    [InlineData("Command Line Tools for Xcode-26.1\n", "26.1")]
    [InlineData("Command Line Tools for Xcode-26.1 beta", "26.1")]
    [InlineData("Command Line Tools for Xcode 26.2-26.1", "26.1")]
    [InlineData("Command Line Tools for Xcode-26.1", "26.2")]
    public void Invalid_or_changed_Apple_labels_cannot_become_elevated_commands(string label, string version)
    {
        var plan = new ToolSetupPlan { Os = MachineOs.MacOs, PackageLabel = label, Version = version };
        Assert.False(ToolSetupCatalog.Valid(plan));
        Assert.Throws<ArgumentException>(() => ToolSetupCatalog.AppleAuthorizationScript(plan));
    }

    [Fact]
    public void Changing_the_OS_or_exact_catalog_label_changes_or_invalidates_the_approval_binding()
    {
        var mac = new ToolSetupPlan { Os = MachineOs.MacOs, PackageLabel = "Command Line Tools for Xcode-26.1", Version = "26.1" };
        var alternativeLabel = mac with { PackageLabel = "Command Line Tools for Xcode 26.1-26.1" };
        Assert.NotEqual(ToolSetupCatalog.Digest(mac), ToolSetupCatalog.Digest(alternativeLabel));
        Assert.False(ToolSetupCatalog.Valid(mac with { Os = MachineOs.Windows }));
        Assert.False(ToolSetupCatalog.Valid(mac with { Os = MachineOs.Linux }));
        Assert.NotEqual(ToolSetupCatalog.Digest(mac), ToolSetupCatalog.Digest(new() { Version = "26.1" }));
    }
}
