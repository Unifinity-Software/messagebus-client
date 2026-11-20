# mbus update

`mbus` updates itself in one of two ways, depending on how it was installed:

- **As a .NET tool** (`MessageBus.Ai.Cli` from nuget.org, the usual install) it updates with `dotnet tool update`.
- **As a standalone file** it replaces itself with a newer release, and only with one signed by the messagebus.ai
  release key.

The runner does the same when the server asks it to (or when the server says its version is too old), after its
running tasks have finished.

```sh
mbus update --check             # is there a newer mbus? does the server need one?
mbus update                     # install it
mbus update --allow-downgrade   # install the channel's release even if it is older
mbus update --channel preview   # follow another channel (default stable)
mbus update --version 0.3.0     # install exactly that version, older or not
```

Versions compare as SemVer and ignore `+build` details. A build made from source rather than by the release reports
`MbusSourceVersion` (`Directory.Build.props`) with `+dev.<commit>` (`0.1.0+dev.1a2b3c4`, or `0.1.0+dev` outside a git
repository), so it compares exactly like that version. A release build (`-p:Version=<release>`) reports exactly its
version.

## The .NET tool

```sh
dotnet tool install -g MessageBus.Ai.Cli
mbus --version
mbus login --server https://app.messagebus.ai
mbus connect
```

`dotnet pack src/MessageBus.Cli.Tool -c Release -p:Version=<version> -o out/nuget` makes the framework-dependent
`MessageBus.Ai.Cli` package. It uses the installed .NET runtime and rolls forward to newer major runtimes. To try a
local package without changing your global installation:

```sh
dotnet tool install --tool-path out/tools --add-source out/nuget MessageBus.Ai.Cli --version <version>
out/tools/mbus --version
```

On Windows the launcher is `out/tools/mbus.exe`.

**Manual update.** `mbus update` selects the newest listed package for the channel (or the exact `--version`), lists
any processes holding this install and refuses while any remain. It also refuses when a registered runner uses this
install: use **Update now** on the Runners page or `mbus runner stop` first. It asks
`Update now with dotnet tool update …? [y/N]`; `--yes` skips the question. Without a terminal and without `--yes`, it
prints the command and changes nothing. `--check` only reports the version. On macOS and Linux it replaces its process
with a private applier copy of mbus; on Windows the applier shares the console, waits for the calling process to exit,
then updates. Local manifest tools print the `dotnet` command to run instead. A NuGet install never replaces itself
from the standalone feed.

**Runner update.** A registered Windows or macOS runner updates this tool through its service. Run
`mbus runner install` once to install the service's update step; an older service definition reports "Run mbus runner
install once on this machine so it can update itself." Terminal runners, local manifest tools and Linux services
can't use this handoff.

The runner keeps taking work while other processes hold this install, reporting `waiting-for-sessions` and only the
number of holders (its local log lists their process ids and names). Once clear, it drains its tasks, checks again,
prepares the update and exits 75. It never kills sessions. Before each service start, `update/wrapper.sh` or
`wrapper.cmd` starts a private applier copy outside the tool store, which runs `dotnet tool update` with an isolated
package source and keeps a copy of the installed package so a failed update can be restored. The new version's
`--version` decides success; a failed check, or three starts without a runner connection, restores the previous
version.

Files under the settings folder's `update/`:

- `waiting.json`: the requested version and pin intent, kept across runner restarts.
- `attempt.json`: the attempt id, from/to versions, pin intent, start count, confirmation, retry and final state.
- `applier/`: a private copy of mbus and its dependencies, unaffected by the tool being replaced.
- `recovery/`: the installed `.nupkg`, copied before dotnet removes the previous version.
- `NuGet.Config`: `<clear/>` plus nuget.org, replaced by the machine's `MBUS_UPDATE_NUGET_SOURCE` when set.
- `Recovery.Config`: `<clear/>` plus only the saved local recovery folder.
- `result.json`: the result for the attempt, with a short redacted diagnostic; moved to `consumed-result.json` once
  reported.
- `last-failure.txt`: a diagnostic and repair command if automatic recovery fails; also logged at startup.

## Standalone files

### What it checks before replacing anything

1. `manifest.json` from the release channel, and `manifest.json.sig` next to it: an ECDSA P-256 (ES256) signature of
   the manifest's exact bytes. It must verify with the release public key built into this mbus. If it doesn't,
   nothing is downloaded. For a pinned version (`--version`, or the workspace's setting) it reads
   `manifest-<version>.json` and its `.sig` instead, and refuses one that names another version.
2. The manifest's version against this mbus: the same version does nothing; an older one is refused unless
   `--allow-downgrade` or it is the pinned version.
3. The file for this platform (`win-x64`, `osx-arm64`, `linux-x64`, ...): downloaded into a new folder of its own
   beside the installed mbus (`.mbus-versions/<version>/mbus`, below), then its size and SHA-256 must match the
   manifest. If they don't, the folder is deleted and nothing changes.
4. The start check. The download is made executable and started once where it will run from
   (`.mbus-versions/<version>/mbus --version`, `VersionStartCheck.cs`): it must exit 0 and print the manifest's
   version within 30 seconds. If it doesn't, the folder is deleted, nothing changes, and the message says what
   happened (couldn't start and why, its exit code and signal, still running after 30 seconds, or the version it
   printed instead), plus the first lines it printed. The runner records that as `rolled-back`.
5. The switch: `mbus` starts the new version from now on. Processes still running the old version (an agent's
   `mbus mcp`, a runner) carry on with it until they restart.

### One folder per version

A single-file .NET program opens its own file by path again each time it needs more of itself, so the file at a path
any mbus runs from must never change. Each version therefore gets its own folder:

```text
~/.local/bin/mbus                         a symlink to .mbus-versions/0.3.0/mbus (macOS, Linux)
~/.local/bin/.mbus-versions/0.3.0/mbus    the current version
~/.local/bin/.mbus-versions/0.2.0/mbus    the one before it
```

- **macOS and Linux.** `mbus` is a relative symlink, switched by renaming a new symlink over it (the path is never
  missing). The .NET host resolves the symlink when it starts, so a running mbus keeps reading its own version's file.
- **Windows** can't make symlinks without Developer Mode, so `mbus.exe` stays a real file: a hard link to the current
  version's file (a copy where hard links don't work). On a switch, `mbus.exe` is renamed into `.mbus-versions` and
  the new version's file is linked in its place. `mbus mcp` and `mbus runner` started from `mbus.exe` start the
  current version's own file with the same arguments and console, and wait for it (`VersionLauncher.cs`), in a job
  that ends with `mbus.exe`.
- **The first update** of an install that is still one plain file moves it into this layout.
- **Kept versions.** The current one and the one before. Each update deletes older ones that nothing runs, and
  leftovers of the old layout (`mbus.old`, `mbus.exe.old`, abandoned `.mbus.*.download` files).
- **Stable paths.** `mbus connect`, `mbus runner install` and the runner's own restart use `mbus` on PATH, never a
  version's folder (`VersionedInstall.StableFor`).

### Where releases come from

| Setting | What it does |
|---|---|
| (default) | The server's feed: `GET {server}/api/releases/{channel}/{file}`. |
| `MBUS_UPDATE_FEED` | A folder (or a mirror's http(s) URL) laid out like the server's: one sub-folder per channel. |
| `MBUS_UPDATE_CHANNEL` | The channel (default `stable`); `--channel` wins. |
| `MBUS_UPDATE_TARGET` | The `mbus` to install for, instead of the running one (development and tests): its versions go beside it. |
| `MBUS_RELEASE_PUBLIC_KEY` | The release public key (SubjectPublicKeyInfo, base64). Read **only** by builds without a built-in key. |

Everything in a feed is checked against the signature, so a copy on a file share or a mirror is just as safe. A
channel folder looks like this:

```text
feed/stable/manifest.json             the channel's latest
feed/stable/manifest.json.sig
feed/stable/manifest-0.2.0.json       each version's own, for pinning (same content when it is the latest)
feed/stable/manifest-0.2.0.json.sig
feed/stable/mbus-0.2.0-win-x64.exe
feed/stable/mbus-0.2.0-osx-arm64
```

```json
{
  "version": "0.2.0",
  "channel": "stable",
  "publishedAt": "2026-11-13T09:00:00Z",
  "files": [
    { "rid": "win-x64", "name": "mbus-0.2.0-win-x64.exe", "size": 71234567, "sha256": "<hex>" },
    { "rid": "osx-arm64", "name": "mbus-0.2.0-osx-arm64", "size": 69876543, "sha256": "<hex>" }
  ]
}
```

## The runner

When the server sends an update request, or closes the connection with 4426 ("update required": this mbus is older
than the server accepts), the runner takes no new work, lets running tasks finish and report their results, installs
the release as above, and exits with code **75** so its service manager starts the new version. It tells the server
each stage (`POST /api/runner/update-progress`: waiting, installing, restarting), which the Runners page shows. If the
update can't be installed the runner does not exit: it says why (also kept in `last-update.json`) and takes work
again. A runner that can't update itself (no release key, or started with `dotnet run`) logs `Updates: off` (or
`through dotnet`) and reports that with its inventory, so the dashboard offers no update for it. Only after a 4426
close does a failed update still end the runner (exit 1).

On Windows the Scheduled Task from `mbus runner install` starts the runner again a few seconds after any exit but a
clean stop (0), so exit 75 or a crash brings it back.

**Started in a terminal.** Nothing would restart that runner, so after installing it starts the new mbus itself
(`Runner/RunnerRestart.cs`): same arguments, same environment, same terminal. The first process stays behind only to
wait for it, pass Ctrl+C and `kill` on, and return its exit code. It counts as started by a service manager, and exits
75 instead, when it has `--log-file` (which `mbus runner install` always passes), when its parent is pid 1 (launchd,
init, a container's init) or `systemd`, `supervisord`, `runsv`, `s6-supervise` or `tini`, or when
`MBUS_RUNNER_SUPERVISED=1`; `MBUS_RUNNER_SUPERVISED=0` makes it restart itself anyway.

**From the dashboard.** **Update now** on a runner, or **Updates** on the Runners page, makes the server send each
connected runner an update request with the workspace's release settings: the channel (`stable` or `preview`), a
version to stay on (installed exactly, older or not), and an https mirror to download from instead of the server.
Runners also read these settings with their work policy each time they connect. `MBUS_UPDATE_FEED` on the machine
still wins over the workspace's mirror.

**How it went.** When an update ends the runner writes `last-update.json` in the mbus settings folder (`updated`,
`rolled-back` when the new mbus didn't start and the old one stays, or `not-installed` with the reason), and sends it
with its inventory every time it starts. The Runners page shows it per machine.

## Trying it locally

No release key is committed. To try an update end to end, make a throwaway key, build with its public half and lay
out a feed like the one above, signing the manifest yourself:

```sh
openssl ecparam -name prime256v1 -genkey -noout -out test-release-key.pem
openssl ec -in test-release-key.pem -pubout -outform DER | openssl base64 -A    # -> MBUS_RELEASE_PUBLIC_KEY
openssl dgst -sha256 -sign test-release-key.pem manifest.json | openssl base64 -A > manifest.json.sig
```

`mbus` accepts the signature as base64 or base64url, in DER form (what openssl writes) or IEEE P1363 form (what
`KeyPair.Sign` in MessageBus.Crypto writes). Keep the key out of the repository.

A release build puts the public key in at build time:

```sh
dotnet publish src/MessageBus.Cli.Tool -c Release -r osx-arm64 -p:PublishSingleFile=true -p:DebugType=none \
  -p:MbusReleasePublicKey=<base64 SubjectPublicKeyInfo> -o out/osx-arm64
```

A machine that trusts a key through `MBUS_RELEASE_PUBLIC_KEY` installs anything signed with it, from any feed or
mirror: keep a test key to yourself, and unset the variable when you are done.

**macOS.** The .NET SDK gives a Mac build an ad-hoc signature wherever it is built, which is enough for a program mbus
downloads itself. A release signed with a Developer ID also passes Gatekeeper when downloaded in a browser, and keeps
the machine key where only mbus can read it. If a build has no signature at all, macOS refuses to run it on arm64, its
start check fails (exit code 137, SIGKILL) and nothing is replaced: sign it before computing the manifest's hashes
(signing changes the file), `codesign --force --sign - mbus`. mbus never runs codesign itself.
