# messagebus.ai client

The code that runs on your machines for [messagebus.ai](https://messagebus.ai), which passes tasks between coding
agents on different machines: the `mbus` command, its runner, the local MCP server agents talk to, and the libraries
they share with the service. The hosted service is at [app.messagebus.ai](https://app.messagebus.ai); its source is
not in this repository.

[Deploy a runner with Docker, Compose, or Azure Container Instances](docker/runner/README.md).

## Install mbus

`mbus` is published on nuget.org as the .NET tool `MessageBus.Ai.Cli`. It needs .NET 10.

```sh
dotnet tool install --global MessageBus.Ai.Cli
mbus login --server https://app.messagebus.ai
mbus connect
mbus runner install
```

`mbus login` asks you to approve this machine in the dashboard. `mbus connect` sets up the coding agents it finds and
shows each file it changes first. `mbus runner install` keeps the machine available for tasks. Update later with
`dotnet tool update --global MessageBus.Ai.Cli`, or with **Update now** on the dashboard's Runners page.

For .NET-tool installs, `mbus connect` sets up a separate MCP launcher so mbus can update while your coding-agent
sessions stay open. Existing sessions keep their version; new connections use the updated one. After upgrading
from an older client, run `mbus connect` once and reconnect the agent's mbus MCP server to adopt this setup.
Sessions still using the older setup can block that first update; reconnect or close their MCP servers first.

### Unattended Mac runners

The installed launch agent keeps a supervisor running while it restarts a failed runner after five seconds.
It applies pending updates before each launch. This avoids depending on launchd to respawn the runner when
macOS defers automatic starts in a GUI session's on-demand-only mode. A clean exit, `mbus runner stop`, or
`mbus runner uninstall` stops it. If the supervisor itself is terminated, launchd's restart can still be deferred.

After upgrading an existing Mac installation, run `mbus runner install` once to replace its older launch agent.
The new service definition takes effect then; updating the executable alone leaves the previous definition in place.
For an already installed but stopped service, `launchctl kickstart "gui/$(id -u)/ai.messagebus.runner"` starts it
explicitly. Check `~/.mbus/logs/runner.log` and `runner-errors.log` for connection and supervisor failures.

Agent authentication is separate: to avoid depending on Claude Code's login Keychain, set
`CLAUDE_CODE_OAUTH_TOKEN` from `claude setup-token` when running `mbus runner install`. This stores it for the
runner's agents; the supervisor neither creates nor refreshes agent credentials.

### Windows runners that start at boot

For personal PCs, `mbus runner install` sets up a Scheduled Task that starts when you sign in.
To use a dedicated standard user with the Scheduled Task, sign in as that user once, run `mbus login`
and `mbus runner env --save`, and run `mbus runner install` under that account. Give it only the
repository access it needs. It must remain signed in. Windows fast user switching keeps that session available.

`mbus runner install --service --server https://app.messagebus.ai` installs a delayed-start Windows service
that can run before anyone signs in. From an elevated terminal, use a published mbus .NET tool, a machine-wide
.NET SDK in Program Files, and Git and the agent CLIs on the machine PATH. Set `MBUS_API_KEY` or
`MBUS_API_KEY_FILE` to a runner-scoped key for the selected server. The service creates its own identity and
discards the bootstrap key after enrollment. `--print` previews the install without changing the machine.

The default identity is `NT SERVICE\mbus-runner`. On a domain-joined host, `--account DOMAIN\name$`
selects a dedicated gMSA. Provision it on the host, authorize the host to retrieve its password, and grant it
**Log on as a service**. **Do not add the runner account to Backup Operators.** Its backup and restore
privileges bypass file ACLs. Virtual accounts can authenticate to Windows network resources as the computer
account. Keep the computer account and any gMSA out of groups that can read developer data.

Set tokens with `mbus runner env --save --service` from an elevated terminal. The supported variables are
`CLAUDE_CODE_OAUTH_TOKEN` or `ANTHROPIC_API_KEY` for Claude Code, `OPENAI_API_KEY` or `CODEX_API_KEY`
for Codex, and `COPILOT_GITHUB_TOKEN` for GitHub Copilot CLI. Each also accepts a `_FILE` variable
containing the path to a token file. Use `mbus doctor --service` to check enrollment and configured token
names, and `mbus runner env --clear --service` to remove stored agent tokens. Use a workspace integration
that issues per-run repository tokens.

The protected host in Program Files supervises a private worker installation in ProgramData. The worker drains
before updates, and the supervisor can restore the previous installer package offline. The Runners page
reports Basic isolation with a separate service account. Windows ACLs govern access to developer files.
Processes in a run share the service's account, so they can read that account's state and use its DPAPI
secrets. The network proxy applies to programs that use it.

`mbus runner uninstall --service` drains the worker and removes the service and its local state. The associated
machine record can be removed separately from the Runners page.

Help center: [messagebus.ai/docs](https://messagebus.ai/docs).

## OpenCode with LM Studio

The runner reads a partial configured model inventory and checks LM Studio's loaded-model state without inference.
Local execution supports **OpenCode `opencode-ai@1.19.5` with `@ai-sdk/openai-compatible` on an unmanaged host**.
The runner creates fresh run-owned home/config/data/cache/state folders, skips repository OpenCode configuration,
and constructs a single-provider, single-model runtime. Primary, small, title, summary, compaction and subagent
inference use the selected local model. Inherited plugins, credentials, model overrides and fallback providers are
excluded. Managed/system configuration (including macOS MDM) is refused because it loads after run content.
OpenCode 2 and unverified versions still refuse local execution. Explicit cloud task selections preserve their settings.

Configure a loaded, tool-capable LM Studio model in the runner account's OpenCode settings:

```jsonc
{
  "model": "local/coder",
  "provider": {
    "local": {
      "npm": "@ai-sdk/openai-compatible",
      "options": { "baseURL": "http://127.0.0.1:1234/v1" },
      "models": { "coder": { "id": "your-loaded-instance-id" } }
    }
  }
}
```

Use the API ID served by `/v1/models`, not the display name. Model keys can contain additional slashes.
The bounded baseline uses an 8192-token context and 2048-token output budget; load a model with at least that context.
The reader also accepts OpenCode 2's `providers`/`package`/`settings`/`modelID` shape for inventory inspection.

The scan merges global JSON then JSONC, `OPENCODE_CONFIG`, `OPENCODE_CONFIG_DIR` JSON then JSONC, and
`OPENCODE_CONFIG_CONTENT` in that order. `{env:NAME}` resolves in the runner session; missing variables, file
substitutions and malformed or oversized settings require a configuration fix. Inventory is partial, capped at 100
service-compatible IDs of at most 100 characters. Repository, managed/system, remote and file-based agent settings
are not inventory sources. They are not copied into the confined local runtime. Personal settings stay unchanged.

The implementation is based on [exact pinned upstream source and package integrity](docs/opencode-local-runtime.md).
Native Windows verification passed the full client build and suite, public-contract checks, and the real pinned
package's production launch/isolation proof against fake local and cloud providers. The proof observed nine local
inferences and no cloud requests or cloud inference after its hostile-settings positive-control baseline.
Actual LM Studio inference, Mac two-model trials and the Windows/macOS three-family handoff remain pending;
the [native proof packet](eng/opencode-proof/README.md) records the results and remaining acceptance steps.

`mbus doctor` and scans make only read-only model-list GETs under **one five-second budget for all candidates**, at
most two requests per candidate, 1 MiB per response, without redirects. Native `/api/v1/models` must confirm a loaded
language model, and `/v1/models` must include its API ID. An unloaded default does not hide a healthy second local
candidate. If the shared deadline leaves candidates unverified, availability is retained with a warning and each
delivery must pass preflight. If all candidates fail, an authless local-only agent is unavailable until a successful
rescan; saved cloud authentication remains available. A loaded-candidate warning also explains the local execution
restriction. With LM Studio authentication enabled, configure `settings.apiKey` (OpenCode 1: `options.apiKey`).
The runner never starts a server, loads/downloads a model or assumes JIT loading.

After setup, use `mbus connect opencode` and `mbus connect --rescan` to inspect configuration and readiness.
`mbus doctor --smoke --agent opencode` runs inference for the supported local baseline; unsupported local selections
are handed back before checkout/setup. Only confined local runs with reported token use get known-zero inference
cost. Missing usage stays unknown and positive reported costs remain positive. Inference for this path stays on the
configured operator-controlled loopback LM Studio server. Coordination, reports and allowed tools can use the network;
this is not an offline-agent or zero-total-network-cost guarantee.

Full issue acceptance still needs an operator-run Windows/macOS handoff and bounded tester trials of two already
downloaded coding candidates. Use one harmless repository SHA and tester prompt, fixed context and timeout, and two
attempts per candidate. Record exact models/quantizations, memory fit, tool/MCP success, test commands and exit codes,
progress/settle, elapsed time, tokens/costs, failures, and the chosen default with rationale. Use disposable run-scoped
configuration and restore any operator-authorized service/model changes afterwards. Automated fake-agent tests do
not establish that live acceptance evidence.

## Run a job

A job carries out a work item. Start it with a published workflow for hands-off execution that
continues after your coding session closes, or ask your agent to **orchestrate it here** for a
session that plans, sends tasks to roles and checks their replies. The `mbus-orchestrate` skill
looks for a workflow that fits the repository and work item and offers both ways. When workflows
are unavailable or none fits, it orchestrates here as before.

With the service's `workflows` capability enabled:

```sh
mbus workflow list
mbus workflow show Feature
mbus job start example/todo-app#42 --workflow Feature
mbus job list
mbus job show <id>
mbus job open <id>
```

The start command reports a dashboard link. A workflow controls its steps, approvals and waits;
the repository's [`.messagebus/workflow.md`](docs/workflow-file.md) guides an orchestrating agent
and is not automatically imported into a published workflow. `mbus project …` remains an alias
for one release.

## What is in this repository

| Path | Package | What it is |
|---|---|---|
| `src/MessageBus.Contracts` | `MessageBus.Ai.Contracts` | The messages, names and MCP tool list the client and the service share |
| `src/MessageBus.Crypto` | `MessageBus.Ai.Crypto` | End-to-end encryption of message bodies and attachments |
| `src/MessageBus.Runner.Transport` | `MessageBus.Ai.Runner.Transport` | The runner's connection to the service |
| `src/MessageBus.Attachments.Client` | `MessageBus.Ai.Attachments.Client` | Packaging, uploading and fetching code attachments |
| `src/MessageBus.Mcp` | `MessageBus.Ai.Mcp` | The local MCP server behind `mbus mcp` |
| `src/MessageBus.Cli` | `MessageBus.Ai.Cli.Core` | Everything `mbus` does, as a library |
| `src/MessageBus.Cli.Tool` | `MessageBus.Ai.Cli` | The `mbus` command |
| `src/MessageBus.Testing` | `MessageBus.Ai.Testing` | A fake coding agent, for tests only |
| `tests/` | | Tests for the CLI and runner (against a fake server), the contracts and the encryption |
| `skills/mbus-orchestrate` | | The orchestrate skill built into `mbus` and installed by `mbus connect` |
| `docker/runner` | | A container image for a runner |
| `infra/runner.bicep` | | Run your own runner in Azure |
| `docs/` | | [Protocol](docs/protocol.md), [security model](docs/security-model.md), [workflow file](docs/workflow-file.md) |

All packages share one version. The `mbus` command is the supported product.

**The libraries have no compatibility promise.** The version number describes the `mbus` tool: any public type in the
`MessageBus.Ai.*` libraries may change or disappear in any release. They are published so the `mbus` tool and the
service can share code and tests; depend on them only if you can follow those changes.

## Build and test

Requires the .NET 10 SDK (see `global.json`) and git.

```sh
dotnet build MessageBus.Client.slnx
dotnet test --solution MessageBus.Client.slnx
```

Run your build of `mbus` with `dotnet run --project src/MessageBus.Cli.Tool -- <command>`, or pack it as a tool:

```sh
dotnet pack src/MessageBus.Cli.Tool -c Release -o out/nuget
dotnet tool install --tool-path out/tools --add-source out/nuget MessageBus.Ai.Cli
out/tools/mbus --version
```

A build from source reports its version as `<version>+dev.<commit>`.

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md). Report vulnerabilities privately as [SECURITY.md](SECURITY.md) describes, not in
a public issue.

## Licence

[MIT](LICENSE). Copyright Unifinity Software.
