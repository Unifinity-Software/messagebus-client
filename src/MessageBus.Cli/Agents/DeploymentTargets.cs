using MessageBus.Contracts.Machines;

namespace MessageBus.Cli.Agents;

/// <summary>
/// Where this <c>mbus</c> runs (the original issue), for the Runners page's target filter. <c>MBUS_DEPLOYMENT_TARGET</c>
/// (<c>pc</c>, <c>docker</c>, <c>azure-container-apps</c>, <c>aci</c>, <c>kubernetes</c>) wins; otherwise the variables
/// each platform sets in every container it starts, then the marker files Docker and Podman leave.
/// </summary>
public static class DeploymentTargets
{
    /// <summary>Overrides detection.</summary>
    public const string OverrideVariable = "MBUS_DEPLOYMENT_TARGET";

    /// <summary>Detects the target from <paramref name="env"/> and <paramref name="fileExists"/>.</summary>
    public static DeploymentTarget Detect(Func<string, string?> env, Func<string, bool> fileExists)
    {
        switch (env(OverrideVariable)?.Trim().ToLowerInvariant())
        {
            case "pc": return DeploymentTarget.Pc;
            case "docker": return DeploymentTarget.Docker;
            case "azure-container-apps" or "aca": return DeploymentTarget.AzureContainerApps;
            // Preserve the original wire category for services predating optional deployment metadata.
            case "azure-container-instances" or "aci": return DeploymentTarget.Docker;
            case "kubernetes" or "k8s": return DeploymentTarget.Kubernetes;
        }
        if (!string.IsNullOrEmpty(env("KUBERNETES_SERVICE_HOST"))) return DeploymentTarget.Kubernetes;
        // Container Apps runs on Kubernetes too, but hides KUBERNETES_SERVICE_HOST and sets its own names.
        if (!string.IsNullOrEmpty(env("CONTAINER_APP_NAME"))) return DeploymentTarget.AzureContainerApps;
        if (fileExists("/.dockerenv") || fileExists("/run/.containerenv")) return DeploymentTarget.Docker;
        return DeploymentTarget.Pc;
    }

    /// <summary>The target of the current process.</summary>
    public static DeploymentTarget Current(Func<string, string?> env) => Detect(env, File.Exists);

    /// <summary>Optional ACI deployment metadata. The ARM resource ID is a setup hint, never a cloud authorization.</summary>
    public static MachineDeployment? Details(Func<string, string?> env)
    {
        if (env(OverrideVariable)?.Trim().ToLowerInvariant() is not ("azure-container-instances" or "aci")) return null;
        var resource = env("MBUS_AZURE_RESOURCE_ID")?.Trim();
        return new MachineDeployment
        {
            Target = DeploymentTarget.AzureContainerInstances,
            AzureResourceId = resource is { Length: > 0 and <= 2048 } ? resource : null,
        };
    }
}
