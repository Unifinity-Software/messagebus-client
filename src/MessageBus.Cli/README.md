# mbus

The messagebus.ai command-line tool. It signs a machine in to a server, finds the coding agents
installed on it and sets them up to use the bus.

```sh
mbus login --server https://app.messagebus.ai --location "Mac mini"
mbus connect            # shows each file it will change, then asks
mbus connect --rescan   # look for agents again and update the dashboard (changes nothing)
mbus doctor             # can this machine take tasks? git, agent sign-in, toolchains, and how to fix each
mbus machine set-location "Build room"
mbus update --check     # a newer signed release? (Update/README.md)
mbus logout
```

`mbus mcp` is the local MCP server the agents start themselves (their settings, written by
`mbus connect`, run it). `mbus runner` comes from issue the original issue (`Runner/`).

## Retarget a queued task

Preview a different recipient while keeping the existing thread and project:

```sh
mbus retarget thr_example --to role:reviewer --dry-run --json
```

Use the preview's `previousMessageId` and a unique operation id to apply it:

```sh
mbus retarget thr_example --to role:reviewer --expected-task-message-id msg_example --operation-id retarget-example --reason "Needs a review" --json
```

Keep the same operation id and task pin when retrying an uncertain response. `--to` also accepts
`agent:agt_example` and `capabilities:os:linux,tool:dotnet`. The service must support retargeting,
and refuses tasks once picked up. A preview never uploads content. If encryption needs to change,
run the command on a machine holding every original body/attachment key; plaintext references are
preserved. Errors and exit codes follow the other script commands. The output includes the service's
current status and any notice about the new routing or independent-task settings.

## What it stores

Everything is in `~/.mbus` (`%USERPROFILE%\.mbus` on Windows; `MBUS_HOME` moves it):
`config.json` (server, machine id, workspace, name, location), the machine's private signing key
(ECDSA P-256, made on this machine; only the public half is sent to the server), and `cli-session.json`, your mbus sign-in: a one-hour access token and a refresh token the server
issued when you approved `mbus login` in the dashboard (the original issue). mbus renews it before it runs out (the
refresh token changes every time; `cli-session.lock` keeps two mbus processes from renewing at once).
It is only used to enroll the machine; the machine's own requests are signed with its key.
`mbus logout` ends it on the server too, and **My account → Sessions** can end it from the dashboard.
On macOS and Linux the folder is `700` and the files `600`; on Windows they sit in the user's profile.

The private key goes to the system's key store (the original issue, `Security/MachineKeyVault.cs`): on Windows
`machine-key.dpapi`, encrypted with DPAPI for the current user; on macOS a generic password
(`ai.messagebus.mbus.machine-key`) in the login Keychain, written through `security -i` so it never appears
on a command line; on Linux `machine-key.pem` (`600`). A `machine-key.pem` from an older mbus moves into the
store the first time mbus reads it, and is deleted once the store gives the same key back; if the store refuses
(a locked keychain over SSH), the file keeps working and the move is tried again next time.
`MBUS_KEY_STORE=file` keeps the PEM file on every system.

**macOS with a Developer ID signature (the original issue, `Security/MacKeychain.cs`).** An item written through `security`
trusts that tool, so any program running as you (one a task's run starts, say) can ask it for the key without
macOS asking you. When mbus itself carries a Developer ID signature (a release from `release-mbus.yml`; the team
built in with `-p:MbusAppleTeamId`), it writes the item through Security.framework instead, and the item's access
list then trusts only mbus. Every other program, Apple's tool included, gets a macOS prompt first, and mbus never
lets a prompt appear itself (a runner has nobody watching its screen). The designated requirement of a Developer
ID signature stays the same from one release to the next, so updates keep access. An item an earlier mbus wrote
through the tool is read that way once and written again. Ad-hoc signed builds (anything built on your own
machine, or a release without the Apple secrets) keep using `security`: their signature changes with every build,
so the Keychain would ask again after each update.

**Never inside a run.** mbus refuses to load the machine key when `MBUS_RUN_TOOLS` is set (the run's own
credential, the original issue) or when macOS has it in a sandbox (`sandbox_check`; mbus itself never runs sandboxed, and a run
can't take its sandbox off the way it can clear a variable). A task's `mbus mcp` relays to the runner instead and
never needs the key.

## Environment variables

| Variable | What it does |
|---|---|
| `MBUS_SERVER` | Server URL; wins over the stored one. |
| `MBUS_API_KEY` | Use this API key instead of the machine key (containers; no sign-in needed). |
| `MBUS_MACHINE_NAME` | The machine's name in the dashboard (default: host name). |
| `MBUS_LOCATION` | The machine's Location label. |
| `MBUS_ENROLL_TOKEN` | A one-time enrollment token, instead of signing in (same as `--token`). |
| `MBUS_HOME` | Folder for settings and the machine key. |
| `MBUS_KEY_STORE` | `os` (default: DPAPI on Windows, the Keychain on macOS) or `file` (the `600` PEM file everywhere). |
| `MBUS_RUNNER_PASS_ENV` | More variables a run may inherit from `mbus runner`, comma-separated (for example `AWS_PROFILE,AWS_REGION`). |
| `MBUS_RUNNER_NETWORK` | `limit` (default: each run reaches only its allow-list through a proxy of its own, the original issue) or `open`. |
| `MBUS_RUNNER_ALLOW_HOSTS` | More hosts every run may reach, comma-separated (`*.example.com` for every name under a domain). |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Send mbus's own traces to this OpenTelemetry collector (the other standard `OTEL_*` variables apply). |

Runs started by `mbus runner` don't inherit its whole environment: only what programs need (paths, locale,
proxies, toolchains) and the agent's own provider settings. Nothing that signs in as the machine reaches a run
(`MBUS_API_KEY`, `MBUS_HOME`): the runner serves each run's mbus tools itself (the original issue, `Runner/RunTools.cs`). The
agent's `mbus mcp` sees `MBUS_RUN_TOOLS` and only relays MCP to the runner over a named pipe (Windows) or a Unix
socket in `/tmp`, after sending the run's random credential; the runner answers as the machine, limited to the run's
task (its tools, its thread and the threads it started, paths inside its run folder; `MessageBus.Mcp/RunToolScope.cs`),
and starts git for those tools inside the run's sandbox. The pipe and credential end with the run. `OTEL_*` variables are removed too, because exporter
headers can carry credentials. Each run still gets `TRACEPARENT`, so its work shows in the task's trace. To give
runs your collector settings, name them in `MBUS_RUNNER_PASS_ENV` (for example
`OTEL_EXPORTER_OTLP_ENDPOINT,OTEL_EXPORTER_OTLP_HEADERS`).

## Build and publish

From the repository root, one self-contained file per platform:

```sh
dotnet publish src/MessageBus.Cli.Tool -c Release -r win-x64   -p:PublishSingleFile=true -p:DebugType=none -o out/win-x64
dotnet publish src/MessageBus.Cli.Tool -c Release -r osx-arm64 -p:PublishSingleFile=true -p:DebugType=none -o out/osx-arm64
dotnet publish src/MessageBus.Cli.Tool -c Release -r linux-x64 -p:PublishSingleFile=true -p:DebugType=none -o out/linux-x64
```

**macOS (Apple silicon).** macOS only runs arm64 programs that are signed. Publishing on the Mac
itself signs `mbus` automatically. A file published on Windows and copied over must be signed there
once, and the download flag cleared:

```sh
chmod +x mbus
codesign --force --sign - mbus
xattr -d com.apple.quarantine mbus 2>/dev/null || true
sudo mv mbus /usr/local/bin/    # or anywhere on PATH
```

From a checkout on the Mac, `dotnet run --project src/MessageBus.Cli.Tool -- login ...` works too; then
`mbus connect` writes the build output's `mbus` into the agents' settings.
