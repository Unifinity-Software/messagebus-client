# Native OpenCode proof packet (#332)

This packet is authored and compiled, **not executed** by the task worker. The fake-provider
proof is separate from LM Studio model quality/acceptance and the three-family Windows/Mac demo.
Run only in a native coordinator context with test execution authorized. Current Mac worker
tester/backend roles have `canRunTests=false`; that is an execution gate, not a reason to clear
`MBUS_RUN_TOOLS`, rename a test, invoke its DLL, change permissions, or suppress `InsideRun`.

Baseline: server `3621746ce2c2cdd794443ed6174b1fa901e0d14e` stays unchanged; client parent
`cbd04253ba567b1044920a50112bf9317a0ae745`. Run on this packet's committed client head.
No global install, personal home/config/auth writes, machine enrollment, rekey, role changes,
service changes, model download/load/unload, release, push, PR, approval or merge is part of this packet.

## Verified artifact setup (no package execution)

`prepare.py` retrieves metadata and the exact first-party npm platform tarball, requires the
pinned registry SRI, verifies SHA-512, rejects unexpected tar paths/types and package identity,
then writes the binary. It does not run npm, package scripts or OpenCode. The native test hashes
the binary again before execution. Keep metadata, archive, manifest (archive and binary SHA-256)
with the evidence. Pin provenance was retrieved read-only on 2026-11-21:

| Package | Exact registry metadata | SHA-512 SRI |
| --- | --- | --- |
| opencode-windows-x64 1.19.5 | https://registry.npmjs.org/opencode-windows-x64/1.19.5 | `sha512-xCVbrA8A/qQTAdBIKyhK5tx3/mcq18yF82Mi8phry0MJb2631nnnpxF9m6FFfWAVHyer4qFGrO7hbWXesHQ1Gg==` |
| opencode-darwin-arm64 1.19.5 | https://registry.npmjs.org/opencode-darwin-arm64/1.19.5 | `sha512-0MbSbKkmwVHtgP31GJyyECe6QVy4T7W4Wp7s2TsmXHTPPyf01ne3dWSX97X9dkNK28Lc6kt+zMPQUP/9GgDLZg==` |

Tarball URLs are `<registry package URL>/-/<package>-1.19.5.tgz`. The only accepted members are
`package/package.json` and `package/bin/opencode.exe` (Windows) or `package/bin/opencode` (Mac).
Registry metadata does not expose `gitHead`; artifact integrity does not independently establish
a reproducible source/binary binding. Version/config/session runtime assertions must pass too.

Windows x64, from the client checkout, with a **new**, explicitly chosen owned scratch path:

```powershell
./eng/opencode-proof/windows.ps1 -Scratch (Join-Path $PWD ('.proof-' + [guid]::NewGuid().ToString('N')))
```

This verifies setup, scopes opt-in variables to the full standard client build/test command,
records logs, runs public-contract and whitespace checks, then restores process variables.
Inspect the suite result: `OpenCodeRuntimeProofTests` must execute once and pass, not skip.
The loaded-model host fact is expected to skip when no real LM Studio model is supplied.
Regular tests have no download or native proof unless a coordinator explicitly supplies the binary.
If executing individual commands, use the same manifest values and standard suite invocation:

```powershell
$m = Get-Content '<owned-scratch>/manifest.json' -Raw | ConvertFrom-Json
$env:MBUS_TEST_OPENCODE_EXE = $m.executable
$env:MBUS_TEST_OPENCODE_SHA256 = $m.binarySha256
$env:MBUS_TEST_OPENCODE_EVIDENCE = $m.evidence
$env:MBUS_TEST_PROOF_PYTHON = $m.python
dotnet build MessageBus.Client.slnx -m:1 --disable-build-servers -p:UseSharedCompilation=false
dotnet test --solution MessageBus.Client.slnx --no-build
python eng/check-public-contracts.py
git diff --check
```

First verify/build the unchanged companion server with `dotnet build MessageBus.slnx -m:1
--disable-build-servers -p:UseSharedCompilation=false` in its own checkout. Do not substitute CI
for local results. Native test failures require inspection/correction, not a claim of success.

## What the executable fake-provider fixture requires

`OpenCodeRuntimeProofTests` creates separate loopback TCP listeners for local and fake-cloud
traffic; neither reaches real cloud. The local listener serves LM Studio `/api/v1/models` and
OpenAI `/v1/models` plus deterministic streamed/nonstreamed `/v1/chat/completions`. Request bodies
and actual API model IDs are retained independently of the parser's zero cost calculation.

Owned hostile scopes include global JSON, home `.opencode` config/file agent, project JSONC/file
agent, auth file with a wellknown config source, SQLite active account/org pointing to fake cloud,
cloud primary/small/compaction model settings, and an executable file-plugin sentinel. The fixture
lets the real package initialize the original owned SQLite schema, then uses Python sqlite3 to
seed synthetic values (never a personal DB). Positive control requires config/file agents to
parse, both auth/account remote requests to reach the counter, the plugin sentinel to execute,
and a real OpenCode inference to hit fake cloud. Only then is the counter baseline taken.

The owned npm environment points at a third loopback listener, separate from both inference
counters, with retries disabled and a five-second fetch timeout. Unknown paths return HTTP 404.
This is needed because the pinned config loader starts an `@opencode-ai/plugin` dependency
install for each config directory, and instance bootstrap initializes plugins before `debug
config` prints. When a file plugin is present, plugin initialization joins those installs.
The schema initializer must also configure an external plugin: without one, the join is
skipped, and the CLI's explicit process exit can leave detached installs holding locks. Native
diagnostics found dependency locks still owned by the exited initializer while the next
positive control was waiting. Upstream only considers those locks stale after 60 seconds,
longer than the fixture's unchanged 45-second command deadline.

Initialization now supplies an import-free file plugin through a separate launch environment.
Its `INITIALIZE_DEPENDENCIES_JOINED` stdout marker is required before setup proceeds; upstream
joins every config dependency fiber before invoking the plugin. The plugin is outside discovery
directories and is absent from subsequent launches. The explicit owned config directory uses
native path separators to match the XDG global path, avoiding two string identities for the
same directory on Windows. Hostile global/home/repository/auth/account/plugin controls still
load afterward.

A public-registry install was therefore an unbounded external dependency in the positive
control. The upstream loader logs a failed background install and continues; the fixture's
import-free file plugin must still execute and fake-cloud inference must still occur. No
installed SDK is fabricated and no config/plugin path is disabled. Registry requests/errors
are saved separately, and a positive assertion requires the real package lookup to reach it.

Source evidence at the pinned commit: [config install fibers](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/config/config.ts#L405),
[all-fiber join](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/config/config.ts#L584),
[conditional plugin dependency join](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/plugin/index.ts#L168),
[file plugin support](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/plugin/shared.ts#L158),
[instance bootstrap](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/project/bootstrap.ts#L29),
[explicit CLI exit](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/index.ts#L130),
[lock staleness](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/core/src/util/effect-flock.ts#L39),
[config directory identities](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/config/paths.ts#L20),
and [npm environment configuration](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/core/src/npm-config.ts).
The wellknown and account response envelopes both correctly contain `config`; they are not
changed to work around the timeout. This source-backed timeout explanation still needs the
native coordinator's runtime confirmation; compilation alone does not establish a fix.

The confined launch comes from **AgentRun.AgentProcessLaunch + actual RunIsolation.Wrap**, including
environment scrubbing and the platform's sandbox wrapper. It retains the final wrapped executable,
environment and wrapper prefix when invoking `debug config`, `models`, `run` and `serve`. It records
the actual isolation report, including platform limitations; Windows basic isolation is not an OS
file/egress guarantee. This test does not simulate a complete enrolled runner delivery or the
restricted-network proxy. Those handoff/network checks remain separate native acceptance.

Assertions require all seven named built-in agents (not just returned entries), local model and
small_model, allowlisted provider, checked baseURL, exact model inventory, absence of hostile
markers, actual primary LOCAL_OK/token usage, known-zero parser inference cost, and observed local
API IDs. Session calls deliberately execute a subtask and compaction. A nonretryable induced local
400 and unknown explicit provider/model must fail without increasing **any** fake-cloud request
or inference counter. There is no invented `fallback` config key: the pinned schema does not define
one; default/recent/small selection and failure/unknown-model resolution are the fallback surfaces.

Pinned upstream source (`810c0eba1a7348036f8bdf61e6632d0c36c6876c`):

- [Session API payloads/routes](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/server/routes/instance/httpapi/groups/session.ts): POST `/session`, `/session/:id/message`, `/session/:id/summarize`; GET children/messages.
- [Session handlers](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/server/routes/instance/httpapi/handlers/session.ts): summarize creates compaction and runs the prompt loop.
- [PromptInput/handleSubtask](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/session/prompt.ts) and [SubtaskPartInput](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/schema/src/v1/session.ts): subtask parts with agent/prompt/description execute the task tool; children must exist and local inference must increase.
- [Compaction](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/session/compaction.ts): resolves compaction agent; saved messages must contain compaction and the local inference count must increase.
- [Account schema](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/core/src/account/sql.ts), [account config](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/account/account.ts), [config loader](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/config/config.ts), [plugin loader](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/plugin/index.ts).

Every command stores argv/stdout/stderr/exit/timeout before assertions. Session request/response,
effective config, final sanitized launch, server output, counters, HTTP logs and FAIL.json are
kept on failure too. An outer evidence guard also records endpoint/workspace/isolation setup
exceptions in FAIL.json and retains setup stages in isolation.log. Native initialization DEBUG
logs go to stderr; each output stream retains its first 1 MiB of characters (with a truncation
marker) while continuing to drain the pipe. Each command has a 45-second tree-kill deadline, server readiness 20 seconds,
session request timeout 35 seconds. A schema/API incompatibility or inability to trigger a path is
an explicit acceptance failure/gap, never inferred success. Fake-provider PASS does not establish
that LM Studio performs real inference locally or that a coding model is suitable for the role.

## Mac arm64 native packet (instructions only)

Acquire a native unmanaged Mac context outside an mbus task, with test/model execution allowed.
The current role gate must be resolved by an authorized coordinator; do not change it from a worker.
From the client checkout, create a fresh owned scratch with `mktemp -d`'s **parent** plus a new child
(prepare.py requires the child not exist), then verify the exact platform package:

```sh
scratch="$(mktemp -d)/opencode-proof"
python3 eng/opencode-proof/prepare.py --platform darwin-arm64 --scratch "$scratch"
export MBUS_TEST_OPENCODE_EXE="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["executable"])' "$scratch/manifest.json")"
export MBUS_TEST_OPENCODE_SHA256="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["binarySha256"])' "$scratch/manifest.json")"
export MBUS_TEST_PROOF_PYTHON="$(command -v python3)"
export MBUS_TEST_OPENCODE_EVIDENCE="$scratch/evidence"
dotnet build MessageBus.Client.slnx -m:1 --disable-build-servers -p:UseSharedCompilation=false
dotnet test --solution MessageBus.Client.slnx --no-build
python3 eng/check-public-contracts.py
git diff --check
```

Stop on a nonzero command; retain evidence on failure. Confirm the opt-in fixture executed, not
skipped. For an additional real LM Studio smoke, read inventory only (no loading):

```sh
curl --fail --max-time 5 http://127.0.0.1:1234/api/v1/models > "$scratch/native-models.json"
curl --fail --max-time 5 http://127.0.0.1:1234/v1/models > "$scratch/api-models.json"
```

There are **no verified current Qwen/Gemma identifiers in this worker's Windows context**. Record
exact existing file/model key, quantization, runtime version, loaded instance ID/API alias and
context capacity from the Mac inventory/operator evidence. Match `type=llm`, a nonempty
`loaded_instances`, and the exact `/v1/models` alias. Require context >=8192. If Qwen or Gemma is
not already downloaded and loaded, stop that phase and report the missing owned execution context;
do not substitute a name, use cloud, download, or mutate a personal instance.

Set MBUS_TEST_LMSTUDIO_MODEL to the checked instance ID and MBUS_TEST_LMSTUDIO_URL to its `/v1`
endpoint; rerun the full standard suite for that candidate. Capture LM Studio inference/request
logs as independent destination evidence. The loaded-host fact is a smoke, not the four trials below.

For each already available Qwen and Gemma candidate, run two fresh task-owned production runner
attempts in the authorized native context. Use identical empty harmless repositories and this task:

> Create a function add(a,b) in calc.py and a unittest file testing positive, negative and zero
> arguments. Run python3 -m unittest. Do not access the network or modify other files. Report
> progress and settle through the provided mbus tools.

Require the existing selected role to allow commands/tests before dispatch. Preserve the standard
production local config (context 8192, output 2048); impose a **180-second process-tree timeout per
attempt** from the native coordinator, with no retries hidden inside a row. Do not run four tasks
against a personal model loading arrangement without an already authorized task-owned context.

| Candidate | Trial | exact model/quant/runtime/API ID | seconds (<=180) | in/out tokens | cost/local | edit/test correctness | settle | LM request IDs | cloud counter |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Existing Qwen | 1 | pending Mac inventory | pending | pending | pending | pending | pending | pending | pending |
| Existing Qwen | 2 | same exact candidate | pending | pending | pending | pending | pending | pending | pending |
| Existing Gemma | 1 | pending Mac inventory | pending | pending | pending | pending | pending | pending | pending |
| Existing Gemma | 2 | same exact candidate | pending | pending | pending | pending | pending | pending | pending |

Inspect real diffs and run the test command independently. Record failure/recovery on a task-owned
local endpoint and selected override; do not stop a personal LM Studio service. Separately run the
three-family demo with Windows Claude author, independent GPT reviewer, and Mac local tester, using
existing permitted roles/enrollment. Capture project report task IDs, OS/machine/model, settlement,
time/tokens and local $0. The fake-provider fixture and four trials do not fulfill this handoff.

## Cleanup and reporting

On native Windows preserve evidence first, stop any owned process, then use
`./eng/opencode-proof/cleanup.ps1 -Scratch '<exact-owned-scratch>'`. The script validates the absolute
target and ownership marker before native PowerShell deletion. On the Mac preserve evidence and
give the exact scratch path to the native coordinator's cleanup policy; do not delete any parent
returned by mktemp until its ownership and contents have been inspected. Restore/unset only the
task's exported variables; do not edit global/personal config.

Worker build/cache/scratch folders are left to the runner's cleanup setting. The worker records
their exact paths and stops build processes, without recursively deleting them. Return compile
outcomes separately from native suite/proof/model/demo outcomes. Anything pending stays pending.
