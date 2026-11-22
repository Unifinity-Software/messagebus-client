using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner.Resources;

internal static class ResourceSamplerFactory
{
    public static ISystemResourceSampler Create()
    {
        if (OperatingSystem.IsWindows()) return new WindowsResourceSampler();
        if (OperatingSystem.IsMacOS()) return new MacResourceSampler();
        if (OperatingSystem.IsLinux()) return new LinuxResourceSampler();
        return new UnsupportedResourceSampler();
    }

    private sealed class UnsupportedResourceSampler : ISystemResourceSampler
    {
        public ValueTask<ResourceSamplingResult> SampleAsync(ResourceSamplingContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ResourceSamplingResult(
                context.TimeProvider.GetUtcNow(), null, ResourceSampleStatus.Unsupported, "platform-unsupported"));
        }
    }
}
