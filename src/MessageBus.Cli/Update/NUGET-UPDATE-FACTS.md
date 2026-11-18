# Real dotnet-tool update facts

## macOS — 2026-11-17, a Mac mini

macOS 27.2 (26B123), arm64; `dotnet --version`: **10.0.404**; runtime
`Microsoft.NETCore.App/10.0.15`. These are observations for this SDK, not promises for other SDKs.
Windows findings are recorded in the separate section below.

All installs used a disposable `--tool-path`, including a path containing spaces, `&`, `'` and
parentheses. No global install or registered service was changed. The real runner remained running.
Scratch/build folders are left for the runner's cleanup policy, as required by this task's runner.

### Repeat the probe

From the repository root:

```sh
MBUS_REAL_DOTNET_TESTS=1 dotnet test --project tests/MessageBus.Cli.Tests \
  --filter FullyQualifiedName~RealDotnetUpdateTests --output Detailed --show-stdout All
```

`tests/MessageBus.Cli.Tests/Update/RealDotnetUpdateTests.cs` is an xunit manual harness. It skips
unless explicitly opted in, and still skips when a recognized CI environment marker is enabled.
A normal test run skips it. Each process has a bounded wait. It strips inherited `MBUS_*` values,
uses its own settings and a synthetic API key, and points MCP at the closed loopback port
`http://127.0.0.1:1`; it never contacts the real messagebus service or machine key store.
Packing may restore build dependencies using the checkout's normal NuGet configuration; **tool
install/update/recovery** use a generated local-only config and isolated `NUGET_PACKAGES`.

With `$probe` denoting its printed scratch directory, the commands are equivalent to:

```sh
dotnet pack src/MessageBus.Cli.Tool -c Release -o "$probe/feed" -p:Version=0.1.3-test.1
dotnet pack src/MessageBus.Cli.Tool -c Release -o "$probe/feed" -p:Version=0.1.3-test.2
dotnet tool install MessageBus.Ai.Cli --tool-path "$probe/tools & ' (probe)" \
  --version 0.1.3-test.1 --configfile "$probe/NuGet.Config" --verbosity normal
dotnet tool update MessageBus.Ai.Cli --tool-path "$probe/tools & ' (probe)" \
  --version 0.1.3-test.2 --configfile "$probe/NuGet.Config" --verbosity normal
```

The generated XML is escaped with `XDocument` and contains only:

```xml
<configuration><packageSources><clear />
  <add key="local" value="ABSOLUTE_PROBE_FEED" />
</packageSources></configuration>
```

### Update while files are in use

Both without MCP and with `"$probe/tools & ' (probe)/mbus" mcp` held open on redirected stdin:

```text
Tool 'messagebus.ai.cli' was successfully updated from version '0.1.3-test.1' to version '0.1.3-test.2'.
Exit: 0
mbus --version: 0.1.3-test.2
Old store directory exists: False
```

There was **no file-in-use failure** on this Mac. A deterministic missing-version update
(`--version 0.1.3-test.404`) exited **1**:

```text
Version 0.1.3-test.404 of package messagebus.ai.cli is not found in NuGet feeds …/feed.
```

`mbus --version` still returned `0.1.3-test.1` after that failure. This verifies the missing-version
failure case, not every failure during replacement. Retain recovery for partial-install failures.

### Does the old MCP process still work?

Yes for the exercised path. Before updating, the harness sends MCP `initialize`,
`notifications/initialized` and `tools/list`. After updating, the **same PID** answers another
`tools/list` and its **first** `tools/call` for `fetch_attachment` with synthetic identifiers.
The first attachment call reaches networking and returns the expected protocol result:

```text
{"code":"unavailable","message":"Couldn't reach messagebus.ai: Connection refused",
 "retryable":true,"retryAfterSeconds":5}
```

MCP stderr was empty. No `Could not load file or assembly` error occurred.
`/usr/sbin/lsof -p <mcp-pid>` before and after the update/call showed these newly mapped assemblies
(absent from the first snapshot):

```text
~/.dotnet/shared/Microsoft.NETCore.App/10.0.15/System.Net.NameResolution.dll
~/.dotnet/shared/Microsoft.NETCore.App/10.0.15/System.Net.Sockets.dll
~/.dotnet/shared/Microsoft.NETCore.App/10.0.15/System.Diagnostics.StackTrace.dll
~/.dotnet/shared/Microsoft.NETCore.App/10.0.15/System.Reflection.Metadata.dll
~/.dotnet/shared/Microsoft.NETCore.App/10.0.15/System.Runtime.Intrinsics.dll
~/.dotnet/shared/Microsoft.NETCore.App/10.0.15/System.Threading.Thread.dll
```

**Limit:** these fresh assemblies are framework dependencies outside the removed tool folder.
The package's `MessageBus.*`, MCP, console and telemetry assemblies were already mapped before
updating; lsof showed those mappings under the renamed `.store/.stage/<random>/…` path afterwards.
The stage directory was empty after the probe. This is evidence that an old process and a fresh
framework load survive; it is **not** evidence that a previously unloaded assembly from the removed
package can be loaded. Keep the plan's conservative holder wait on macOS. This probe did not
reproduce the old in-place replacement failure (a running mbus reading a replaced file) and does not rule it out on another command path or future package layout.

### Offline recovery and package location

With `NUGET_PACKAGES="$probe/packages"`, installing the tool left **no `.nupkg`** in that isolated
global packages directory. The installed package was present at:

```text
<tools>/.store/messagebus.ai.cli/0.1.3-test.1/messagebus.ai.cli/0.1.3-test.1/messagebus.ai.cli.0.1.3-test.1.nupkg
<tools>/.store/messagebus.ai.cli/0.1.3-test.1/messagebus.ai.cli/0.1.3-test.1/MessageBus.Ai.Cli.nupkg
```

The harness copies the versioned `.nupkg` to `$probe/recovery` **before updating**. After updating,
it uses a second `<clear/>` config containing **only that recovery directory**, with
`--version 0.1.3-test.1 --allow-downgrade`, and verifies the old `mbus --version` again. This works
without the original feed or nuget.org being a configured source. A production updater should
keep this package outside `.store`, e.g. in the settings folder's update cache. Do not rely on the
global packages cache or the old store surviving an update.

### Non-interactive options and source isolation

`dotnet tool update --help` says:

```text
--interactive  Allows the command to stop and wait for user input or action … [default: False]
--allow-downgrade  Allow package downgrade when installing a .NET tool package. [default: False]
```

**`--interactive false` is rejected with exit 1 by SDK 10.0.404** on both `tool install` and
`tool update`: `Unrecognized command or argument 'false'.`. Omit `--interactive` to remain non-interactive;
it is an opt-in switch. The harness omits it, sets `DOTNET_NOLOGO=1` and
`DOTNET_CLI_TELEMETRY_OPTOUT=1`, redirects stdin and imposes a timeout. No prompt was observed.
The local-only config successfully installed, updated, and recovered; the missing-version error
listed only the local feed. This establishes configured source isolation; it is not a packet capture
of every SDK network operation. Build/pack restores are deliberately separate from this claim.

`--version 0.1.3-test.1 --allow-downgrade` successfully updated `test.2` back to `test.1`, exit 0.

### Process image and arguments

The harness calls the actual `ProcessTable.ForThisMachine().Get(mcp.Id)` used by
`Runner/Service/RunnerProcesses.cs`. Observed record (temporary prefix shortened):

```text
Pid: 23910
Name: mbus
ParentPid: 23865
ExecutablePath: <probe>/tools & ' (probe)/mbus
CommandLine: "<probe>/tools & ' (probe)/mbus" mcp
Owner: 501
```

The image is the **tools-folder apphost/shim**, not a `.store` executable and not `dotnet <dll>`.
A holder finder must still support those other forms for other launches/platforms. Native
`ProcessTable` worked in this run even though a shell `ps` command was denied by the run sandbox.

### launchd-like environments

No launch agent was registered. Manual checks used `env -i` or the equivalent explicit environment
in `ProcessStartInfo`/Python subprocess. With just `HOME` and `PATH=/usr/bin:/bin`:

```sh
env -i HOME="$HOME" PATH=/usr/bin:/bin /bin/sh -c 'dotnet --version'
# /bin/sh: dotnet: command not found (127)
env -i HOME="$HOME" PATH=/usr/bin:/bin "$HOME/.dotnet/dotnet" --version
# 10.0.404 (0)
```

The absolute dotnet path also successfully ran a real local-feed `tool update` from `test.1` to
`test.2` in that same minimal environment, exit 0. With `HOME` plus the current captured `PATH`
(the PATH `RunnerServiceCommand.PlanFor` would put in the plist), relative `dotnet --version`
returned `10.0.404` and a real `tool update --allow-downgrade` returned to `test.1`, exit 0.
These update commands used the same temp tools and local config shown above. **No `DOTNET_ROOT`
was supplied in any of these minimal environments.** This Mac needs either an absolute dotnet path
or the captured PATH; this install did not need `DOTNET_ROOT` to run dotnet itself.

At this base commit, `RunnerServiceCommand.PlanFor` captures `PATH`, `MBUS_HOME`, the service marker
and macOS key-store setting. It does **not** capture `MBUS_UPDATE_NUGET_SOURCE` or `DOTNET_CLI_HOME`.
B3 must explicitly add those if they need to survive service registration. The test does not
read or change the live plist.

### Can a run's MCP outlive its run?

Code reading, not a live teardown experiment: `AgentRun` calls `EndToolsAndSandboxAsync` in its
finalization, before settlement. It disposes the run-tools endpoint/credential and calls
`RunIsolation.Ended(workspace)`. On macOS that calls `PosixRunProcesses.EndMarked` with the run's
unique inherited `MBUS_RUN_MARKER`; it scans same-user processes, includes descendants, and sends
SIGKILL, with up to five passes. This catches reparented/new-session MCP children that retain the
marker. Windows terminates the run's job object instead.

Thus an ordinary agent-started `mbus mcp` should not survive successful run teardown, and its relay
credential is withdrawn regardless. Cleanup is best effort (process inspection failures are
swallowed); deliberately stripped markers or failed inspection can leave a process alive. The
post-drain holder check remains useful. No real runner was stopped to demonstrate this.

### Validation and remaining scope

- `dotnet build MessageBus.Client.slnx`: passed (existing NuGet/analyzer warnings).
- Normal `dotnet test --project tests/MessageBus.Cli.Tests`: passed, 358 succeeded / 46 skipped,
  including this harness skipped.
- `CI=1 MBUS_REAL_DOTNET_TESTS=1` with the harness filter: skipped (1 skipped, exit 0).
- Opt-in harness: passed on macOS; verifies exact versions, missing-version preservation, update
  while MCP runs, post-update requests and recovery from the separately saved package.
- No product code changed. There is no generated NuGet applier at this base commit to execute yet;
  Windows shell shapes are exercised below, ahead of B3's implementation.

## Windows — 2026-11-17, a Windows virtual machine

Windows build **26300.9974**, x64; `dotnet --version`: **10.0.402**; runtime
`Microsoft.NETCore.App/10.0.13`. These observations are specific to this SDK and Windows run.
All installs used disposable tool paths, including `tools & ' (probe)`. No Scheduled Task was
registered or changed, no runner install/stop/uninstall command was run, and the global tools
directory was untouched. The owner's runner remained running throughout.

### Update with a running MCP

After installing `0.1.3-test.1`, the harness started the tools-folder `mbus.exe mcp`, initialized
it and listed its tools. Updating to `0.1.3-test.2` while that process remained alive exited **1**.
Exact diagnostic, with the disposable prefix replaced by `<probe>`:

```text
Tool 'messagebus.ai.cli' failed to update due to the following:
Failed to uninstall tool package 'messagebus.ai.cli': Access to the path '<probe>\tools & ' (probe)\.store\messagebus.ai.cli\0.1.3-test.1' is denied.
```

The old store directory still existed and `mbus.exe --version` returned **0.1.3-test.1**, exit 0.
The same MCP PID answered `tools/list` (21 tools) and its first `fetch_attachment` call, which
returned the expected `unavailable` result from the closed loopback port. MCP stderr was empty.
After ending only this test-owned MCP, the same update succeeded, exit 0, and `--version`
returned **0.1.3-test.2**. A missing-version update also exited 1 and preserved the old version.

### Shim image alone: a nonzero exit can follow a successful replacement

The harness separately starts the installed shim with native `CreateProcessW`, using
`CREATE_SUSPENDED | CREATE_NO_WINDOW`. Its initial thread is never resumed: this holds only
the native executable image, before managed assemblies load, with no child process. The
test always terminates this suspended process in `finally`.

While that image was held, update printed:

```text
Tool 'messagebus.ai.cli' was successfully updated from version '0.1.3-test.1' to version '0.1.3-test.2'.
Unhandled exception: Access to the path '<tmp>\csjwwsms.cav\a1jery2n.5ln' is denied.
Exit: 1
```

The random path above is dotnet's scratch file outside the install. Despite exit **1**, the
old `.store` version was gone and a fresh invocation returned **0.1.3-test.2**, exit 0.
`ProcessTable` showed the suspended image had been moved to that scratch path: its executable
path and name changed, while PID, start time and original command line stayed the same.
This was observed twice, with different random scratch paths.

**Consequence:** a failed dotnet exit alone does not establish that the old version survived,
or that no replacement occurred. Check the actual executable/version and retain offline repair.
The suspended-image case isolates the native shim lock; it is not an ordinary initialized MCP.

### ProcessTable image and argv

The initialized MCP's record, from the existing native `ProcessTable` implementation:

```text
Pid: 66884
Name: mbus
ParentPid: 39264 (the harness)
ExecutablePath: <probe>\tools & ' (probe)\mbus.exe
CommandLine: "<probe>\tools & ' (probe)\mbus.exe" mcp
Owner: null
```

The full table contained **one** executable under this tool install: that shim. It did not
start a `.store` apphost or `dotnet` child. Its managed code is loaded from `.store` in that
same process. Matching the tools-folder image catches this launch; this experiment found no
need for loaded-module scanning. This does not eliminate support for direct `.store` or
`dotnet <assembly>` launches in the planned holder finder.

### Offline recovery

As on macOS, isolated `NUGET_PACKAGES` contained **no `.nupkg`** after installing the tool.
The package copies were in the tool's `.store`, at the same relative paths recorded above.
Before replacement, the harness saved the versioned old `.nupkg` in `<probe>\recovery`.

With `test.2` installed, the test asserts the old store directory is absent and renames the
original feed directory so it is unavailable. An update using only the saved recovery package,
a `<clear/>` recovery config, `--version 0.1.3-test.1 --allow-downgrade`, and the isolated package
cache succeeded, exit **0**. `mbus.exe --version` returned **0.1.3-test.1**. This verifies local
recovery without the original source or nuget.org configured; it is not a packet capture.

### Non-interactive cmd session

The harness executes the equivalent of this command through `ProcessStartInfo.Arguments`
(the outer quoting is for `cmd /s /c`, not PowerShell argument escaping):

```bat
cmd.exe /d /s /c "dotnet tool update MessageBus.Ai.Cli --tool-path "<probe>\tools & ' (probe)" --version 0.1.3-test.1 --allow-downgrade --configfile "<probe>\NuGet.Config" --verbosity normal < NUL"
```

It exited **0**, with no prompts, and returned the requested version. The same environment
sets `DOTNET_NOLOGO=1` and `DOTNET_CLI_TELEMETRY_OPTOUT=1`; all process waits are bounded.
Windows SDK 10.0.402 also rejects `--interactive false` with exit **1** and
`Unrecognized command or argument 'false'.` Its help lists `--interactive` as an opt-in switch,
default False. Omit it. No Scheduled Task was needed to exercise this no-console-input shape.

### Renaming batch files, including special characters in the path

A naive running batch with `ren "%~f0" "naive-renamed.cmd"` on one line followed by another
line failed with **`The batch file cannot be found.`**, exit **1**, before reaching that line.
Renaming a batch and continuing to read it by the old filename is unsafe.

Both following shapes executed real local-feed updates successfully, exit **0**, from
`<probe>\tools & ' (probe)`, under `cmd /d /s /c` with `< NUL`:

1. **Self-rename with same-line transfer**, with the renamed invocation taking an apply branch:

   ```bat
   @echo off
   setlocal DisableDelayedExpansion
   if "%~1"=="apply" goto apply
   ren "%~f0" "applying.cmd" && "%~dp0applying.cmd" apply
   :apply
   rem The real dotnet update command runs here, then writes a marker and exits /b.
   ```

2. **Preferred stable wrapper**, renaming the pending applier before it is running:

   ```bat
   @echo off
   setlocal DisableDelayedExpansion
   if not exist "%~dp0pending.cmd" exit /b 0
   ren "%~dp0pending.cmd" "applying.cmd" || exit /b 1
   call "%~dp0applying.cmd" apply
   exit /b %errorlevel%
   ```

The harness checks the pending name is gone, the applier writes its marker, the expected
version runs afterwards, and neither output stream contains `The batch file cannot be found`.
The wrapper stays present while its child applier runs; it does not rename or delete itself.
These are executed shell probes, not B3's future generated production scripts. They do not
change the plan's refusal of paths containing `%`, `!`, `"` or line breaks.

### Repeat and validation

The Windows sandbox blocks `dotnet test` named pipes, so tests ran directly from their built
DLL using the same Microsoft Testing Platform/xunit runner. The regular CLI run skips this
harness, and a filtered run with both `CI=1` and `MBUS_REAL_DOTNET_TESTS=1` also skips it.

```powershell
dotnet build MessageBus.Client.slnx -m:1
# Regular CLI suite, without MBUS_REAL_DOTNET_TESTS:
dotnet tests/MessageBus.Cli.Tests/bin/Debug/net10.0/MessageBus.Cli.Tests.dll
$env:MBUS_REAL_DOTNET_TESTS = '1'
dotnet tests/MessageBus.Cli.Tests/bin/Debug/net10.0/MessageBus.Cli.Tests.dll `
  --filter-class '*RealDotnetUpdateTests' --output Detailed --show-stdout All
```

- Solution build passed (existing NuGet/analyzer warnings); all harness packs also use `-m:1`.
- Full regular CLI suite passed: **342 succeeded / 62 skipped / 0 failed**, including the manual
  harness skipped. Some other integration tests skip inside a runner sandbox by design.
- CI guard: **1 skipped / 0 failed** even with the opt-in variable enabled.
- Extended Windows harness passed: **1 succeeded / 0 failed**; covers both holder forms,
  post-failure MCP calls, cmd with EOF, both rename shapes, and offline recovery.
- No product code changed. All test-owned MCP/suspended processes were ended. Build outputs,
  probe folders, SDK scratch folders and logs stay within this run for runner-owned cleanup,
  as requested; no recursive cleanup was performed by the worker.

### Generated wrapper and applier — 2026-11-17 follow-up

On the same Windows SDK **10.0.402** / runtime **10.0.13**, the harness now executes
the production-generated `wrapper.cmd`, which renames `pending.cmd` before running
`applying.cmd` through the independent applier copy. Both the tools path and the
settings path contain spaces, `&`, an apostrophe and parentheses. No Scheduled Task
or global tool install was changed.

The first run exposed a harness lookup error: a failed Windows update can leave
another `mbus.dll` under `.store/.stage`. Searching the entire tools folder with
`Single()` then fails before exercising the applier. The harness now selects the
entry assembly only from `.store/messagebus.ai.cli/0.1.3-test.1`, matching the
installed version that would supply its own entry assembly in production.
No product change was needed.

The corrected real-dotnet harness passed (**1 succeeded / 0 failed**):

- An initialized MCP holder makes `InstallAsync` refuse the handoff, keep waiting,
  write no pending script, and leave `test.1` installed.
- The generated wrapper updates `test.1` to `test.2` and records `Updated`.
- Three wrapper starts without a connection confirmation restore `test.1` and
  record `RolledBack`.
- A package changed to require runtime `99.0.0` installs but fails its version
  check; the generated applier recovers `test.1` from its saved package and records
  `RolledBack`.

Solution build passed with `-m:1` (existing warnings). The full normal CLI suite
passed with **395 succeeded / 62 skipped / 0 failed**, including the manual probe
skipped. Contracts passed **355/355** and crypto **58/58**.
`dotnet test` itself cannot run here because the sandbox blocks its named pipes;
these checks executed the built Microsoft Testing Platform test DLLs directly.

The successful probe and SDK scratch files were placed under this delivery's
`tmp` folder by setting `TEMP` and `TMP` before launching the test DLL. Test-owned
MCP and suspended-shim processes are terminated and awaited in `finally`. Build outputs, logs and probe folders
are left for the runner's requested cleanup policy.
