namespace MessageBus.Cli.Runner;

/// <summary>Keeps throwaway task homes from adding global tool folders to the user's PATH.</summary>
internal static class RunnerDotnetEnvironment
{
    internal const string AddGlobalToolsToPath = "DOTNET_ADD_GLOBAL_TOOLS_TO_PATH";

    internal static string GlobalToolsPathValue(Func<string, string?> getEnvironmentVariable) =>
        getEnvironmentVariable(AddGlobalToolsToPath) is { Length: > 0 } value ? value : "0";
}
