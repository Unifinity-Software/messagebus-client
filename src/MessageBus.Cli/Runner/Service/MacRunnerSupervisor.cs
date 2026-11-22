using MessageBus.Cli.Update;

namespace MessageBus.Cli.Runner.Service;

/// <summary>
/// Keeps the launch agent alive across runner failures and updates. In a GUI domain's on-demand-only mode,
/// launchd can defer KeepAlive respawns indefinitely, even after a failed exit. Only an intentional stop ends
/// this supervisor; launchd's KeepAlive remains a fallback if the supervisor itself fails.
/// </summary>
internal static class MacRunnerSupervisor
{
    internal const string ArgumentMarker = "mbus-runner-supervisor";
    internal const int RestartDelaySeconds = 5;

    internal static string Script(string directory)
    {
        var stop = DotnetToolUpdater.Sh(Path.Combine(directory, RunnerInstance.StopFile));
        var hook = DotnetToolUpdater.Sh(Path.Combine(directory, "update", "wrapper.sh"));
        // The actual mbus command is passed as positional arguments, never evaluated as shell source.
        // Run children in the background so wait is interruptible and TERM reaches the active child.
        // This includes the updater and retry delay, so bootout cannot leave a supervisor restarting work.
        // Traps record intent: a child can run before child=$! publishes its PID. Stop and wait from normal flow.
        return $$"""
            child=
            stopping=
            stop_supervisor() {
                trap '' TERM INT
                if [ -n "$child" ]; then
                    kill -TERM "$child" 2>/dev/null || :
                    wait "$child" 2>/dev/null || :
                fi
                exit 0
            }
            trap 'stopping=1' TERM INT
            run_child() {
                [ -n "$stopping" ] && stop_supervisor
                "$@" &
                child=$!
                [ -n "$stopping" ] && stop_supervisor
                wait "$child"
                result=$?
                [ -n "$stopping" ] && stop_supervisor
                child=
            }
            while [ ! -f {{stop}} ]; do
                if [ -f {{hook}} ]; then
                    run_child /bin/sh {{hook}}
                fi
                [ -f {{stop}} ] && exit 0
                run_child "$@"
                [ "$result" -eq 0 ] && exit 0
                [ -f {{stop}} ] && exit 0
                printf 'mbus runner supervisor: runner exited with code %s; restarting in {{RestartDelaySeconds}} seconds.\n' "$result" >&2
                run_child sleep {{RestartDelaySeconds}}
            done
            exit 0
            """.ReplaceLineEndings("\n");
    }
}
