using MessageBus.Cli.Agents;
using MessageBus.Cli.Runner;
using MessageBus.Contracts.Roles;

namespace MessageBus.Cli.Tests.Runner;

// A real child wrapper reads the fixture key from a private file, while its
// ProcessLaunch environment contains no key. Exercise the same redaction boundary
// as the ACI helper without needing Linux for the output-reader regression.
internal sealed class FileEnvironmentSandbox(string key, bool emitDiagnostic = false) : IRunSandbox
{
    public ProcessLaunch Wrap(ProcessLaunch launch, RunWorkspace workspace, PermissionProfile profile)
    {
        if (launch.FileName != RunnerHarness.FakeAgentPath) return launch;
        var wrapper = Path.Combine(workspace.Root, OperatingSystem.IsWindows() ? "fixture-agent.cmd" : "fixture-agent.sh");
        var diagnostic = "{\"type\":\"turn.failed\",\"error\":{\"message\":\"fixture provider rejected " + key + "\"}}";
        File.WriteAllText(wrapper, OperatingSystem.IsWindows()
            ? $"@echo off\r\nset \"CODEX_API_KEY={key}\"\r\nset \"fixture_agent_path={launch.FileName}\"\r\n"
                + (emitDiagnostic ? "echo " + diagnostic + "\r\n" : "") + "\"%fixture_agent_path%\" %*\r\n"
            : $"#!/bin/sh\nexport CODEX_API_KEY='{key}'\n"
                + (emitDiagnostic ? "printf '%s\\n' '" + diagnostic + "'\n" : "") + $"exec '{launch.FileName}' \"$@\"\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var host = new Dictionary<string, string?>(launch.Environment) { [CodexAuthentication.CodexKey] = null };
        // The wrapper's host arguments are separate from the agent's settings,
        // as with a plan-file helper. The fake agent only needs its JSON mode.
        return launch with { FileName = wrapper, Arguments = ["exec", "--json"], Environment = host };
    }
}
