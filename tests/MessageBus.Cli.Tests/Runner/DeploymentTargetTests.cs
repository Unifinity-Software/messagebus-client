using MessageBus.Cli.Agents;
using MessageBus.Contracts.Machines;
using Xunit;

namespace MessageBus.Cli.Tests.Runner;

/// <summary>Where a runner says it runs (the original issue): Container Apps isn't mistaken for Kubernetes, and the owner's setting wins.</summary>
public sealed class DeploymentTargetTests
{
    [Theory]
    [InlineData("", "", false, DeploymentTarget.Pc)]
    [InlineData("", "", true, DeploymentTarget.Docker)]
    [InlineData("10.0.0.1", "", true, DeploymentTarget.Kubernetes)]
    [InlineData("", "runner-1", true, DeploymentTarget.AzureContainerApps)]
    [InlineData("10.0.0.1", "", true, DeploymentTarget.Pc, "pc")]
    public void Detects_where_it_runs(string kubernetesHost, string containerApp, bool dockerEnv, DeploymentTarget expected, string? setting = null)
    {
        var env = new Dictionary<string, string?>
        {
            ["KUBERNETES_SERVICE_HOST"] = kubernetesHost,
            ["CONTAINER_APP_NAME"] = containerApp,
            [DeploymentTargets.OverrideVariable] = setting,
        };
        Assert.Equal(expected, DeploymentTargets.Detect(name => env.GetValueOrDefault(name), path => dockerEnv && path == "/.dockerenv"));
    }
}
