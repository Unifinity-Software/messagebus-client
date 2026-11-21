# Node verification recovery and integration map

This candidate selectively applies the client feature delta
`e2a30b20cf379dcb1edca25c2bf85c2941cb9044..7ccd78923820f44cde9c4100fff4b822e89e551c`
to `cfe58abf1e384a89b2816fd23349909d12b8cb3c`. It does not merge the old branch or
import the reference server (`9c4dbefcf48531cffe98efd811ddc3a071f3add8`). Version,
release, ACI bootstrap, managed drain, transport and later bundle work remain at the current base.

## Per-path provenance

Paths below are relative to the repository. “Recovered” means the final feature delta was
applied over the current base, preserving later changes. New recipe files match the reviewed
snapshot. Late fixes in that snapshot include lease/digest settlement binding, nested/private
checkout, archive integrity, private attempt cache, input revision policy validation, and
dependency restore before the agent starts.

| Path | Provenance and adaptation |
| --- | --- |
| `docs/node-verifier.md` | Reviewed snapshot, unchanged; dedicated Linux setup and dependency recipe. |
| `docs/protocol.md` | Recovered additive contracts; adds registry negotiation and catalog opt-in rules; keeps current ACI/bundle protocol. |
| `skills/mbus-orchestrate/SKILL.md` | Recovered verification planning; conflict combined with current non-code role routing; LF preserved for embedding. |
| `skills/mbus-orchestrate/reference/tool-calls.md` | Recovered verified request guidance over current attachment/provenance guidance. |
| `skills/mbus-orchestrate/reference/workflow-steps.md` | Recovered approved frontend verification workflow. |
| `src/MessageBus.Cli/Runner/AgentRun.cs` | Recovered preparation/check/return lifecycle and in-memory agent settle capture. |
| `src/MessageBus.Cli/Runner/NodeVerification.cs` | Reviewed final Node recipe, unchanged. |
| `src/MessageBus.Cli/Runner/NodeVerificationPolicy.cs` | Reviewed final manifest/lockfile/archive policy, unchanged. |
| `src/MessageBus.Cli/Runner/RunCodeReturn.cs` | Recovered exact checked-commit return; preserves current bundle packaging API. |
| `src/MessageBus.Cli/Runner/RunTools.cs` | Recovered trusted settle callback on existing request. |
| `src/MessageBus.Cli/Runner/RunToolsServer.cs` | Recovered callback wiring. |
| `src/MessageBus.Cli/Runner/RunWorkspace.cs` | Recovered runner-owned `VerifiedReturnRevision`. |
| `src/MessageBus.Cli/Runner/RunnerCommand.cs` | Recovered explicit enrollment/preflight and existing extension wiring. |
| `src/MessageBus.Cli/Runner/RunnerEngine.cs` | Recovered early incapable-delivery refusal and enrolled inventory capability; current drain remains intact. |
| `src/MessageBus.Cli/Runner/Sandbox/RunIsolation.cs` | Recovered nesting gate and withholding ordinary Git credentials from verified agents. |
| `src/MessageBus.Cli/Runner/VerificationProcess.cs` | Reviewed fixed Linux process boundary, unchanged; integration seam described below. |
| `src/MessageBus.Cli/Scripts/ScriptCommands.cs` | Recovered `send --environment` and repeated `--required-check`. |
| `src/MessageBus.Contracts/Http/Http.cs` | Recovered optional environment discovery route constant. |
| `src/MessageBus.Contracts/Json/MessageBusJson.cs` | Recovered source-generated registry JSON registration. |
| `src/MessageBus.Contracts/Machines/Machines.cs` | Recovered enrolled Node verification capability name. |
| `src/MessageBus.Contracts/Mcp/McpToolCatalog.cs` | Recovered verified specs/local schema; preserves current attachment descriptions; original `Tools(bool, IReadOnlySet<string>?)` excludes verified tools remotely; additive three-argument opt-in for installed handlers. |
| `src/MessageBus.Contracts/Mcp/McpTools.cs` | Recovered optional requests/discovery/status plus service-only names. |
| `src/MessageBus.Contracts/Mcp/VerificationDirectory.cs` | Reviewed registry DTOs, unchanged. |
| `src/MessageBus.Contracts/Messaging/MessageEnvelope.cs` | Recovered optional persisted verification request. |
| `src/MessageBus.Contracts/Messaging/Threads.cs` | Recovered optional verification/current friction state. |
| `src/MessageBus.Contracts/Messaging/Verification.cs` | Reviewed request/snapshot/result/check contracts, unchanged. |
| `src/MessageBus.Contracts/Runner/RunnerProtocol.cs` | Recovered optional delivery snapshot, decline reason, runner verification report/attempt/digest; preserves managed drain. |
| `src/MessageBus.Mcp/LocalServer.cs` | Recovered runner-owned callback option. |
| `src/MessageBus.Mcp/LocalTools.cs` | Recovered routing/discovery/retry/trusted settle; combines retry attachment guard and current settle semaphore/pinned-bundle refusal with verification; adds live registry and installed-handler negotiation before packaging any child. |
| `tests/MessageBus.Cli.Tests/Runner/NodeVerificationTests.cs` | Reviewed policy/process/restore/checkout fixtures, unchanged. |
| `tests/MessageBus.Cli.Tests/Runner/VerificationDeliveryTests.cs` | Reviewed incapable-delivery refusal cases, unchanged. |
| `tests/MessageBus.Cli.Tests/VerificationSendTests.cs` | Recovered CLI/discovery/batch coverage; adapted fake service and adds negotiation matrix, ordinary legacy route and retry source/input preservation. |
| `tests/MessageBus.Contracts.Tests/ContractSamples.cs` | Recovered optional contract samples, including leased attempt/digest. |
| `tests/MessageBus.Contracts.Tests/JsonContractTests.cs` | Recovered optional serialization/agent-claim rejection coverage. |
| `tests/MessageBus.Contracts.Tests/VerificationCatalogTests.cs` | New regression for default remote omission, explicit opt-in/filtering and retained local request schema. |
| `docs/node-verification-recovery.md` | This integration handoff. |

## Existing generic integration seams for #496

The Node recipe uses the following existing lifecycle points. The single #496 integrator should
adapt these points to its versioned plan/readiness/supervisor, rather than introduce a parallel
execution lifecycle. Types are internal unless their existing declarations are public.

| File and signature | Recovered responsibility and integration direction |
| --- | --- |
| `AgentRun.cs`: `RunServices(..., NodeVerification? Verification = null)`; `AgentRun.RunAsync()`; `VerifyAndReturnAsync(NodeVerification, RunWorkspace, RunWorkspace)` | Prepares/restores before starting the agent, captures trusted settle, stops agent sandbox/tools before fresh checks, returns checked commit and binds settlement. Replace the concrete recipe dependency with the #496 lifecycle while retaining Node policy calls. |
| `RunnerEngine.cs`: `RunnerExtensions.Verification`; `Deliver(TaskDelivery)`; inventory generation | Declines an explicit unsupported request/snapshot before creating a workspace; advertises capability only after enrollment. #496 owns keyed readiness and invalidation. |
| `RunnerCommand.cs`: `RunAsync(...)` enrollment block | Calls `NodeVerification.EnrollAsync(Func<string,string?>, bool linuxAgentIsolation, bool canReturnCommits, TimeProvider, TextWriter, IRepoCredentialSource, string privateRoot, CancellationToken)`. Move generic readiness orchestration into #496; keep recipe/owner settings here as inputs. |
| `VerificationProcess.cs`: `IVerificationProcess.RunAsync(string? checkout, string executable, string[] args, CancellationToken, bool network = false, bool maskGit = false, string? input = null, string? output = null, TimeSpan? timeout = null, string? npmCache = null)` | Injection seam already used by Node tests. All executable/arguments/mounts are selected by the recipe, never task callers. The recovered implementation supplies Linux bubblewrap, clean environment, deadlines and bounded output. #496 can supply its supervisor through this seam; do not expose this parameterized interface as a public caller executor. |
| `VerificationProcess.cs`: `ProbeAsync(CancellationToken)`; `CommandResult(int ExitCode, string Output, bool TimedOut = false)` | Recovered preflight/result; #496 owns plan-bound readiness and bounded exit/log-digest evidence. Current result has no log digest. |
| `RunIsolation.cs`: `SupportsVerificationAgent`; `PrepareAsync(RunWorkspace, TaskDelivery, PermissionProfile, CancellationToken)`; `Wrap(ProcessLaunch, RunWorkspace, PermissionProfile)` | Linux nesting precondition and existing agent confinement; no ordinary credential exposure for verified delivery. #496 owns native Windows/macOS protection and shared isolation policy. |
| `RunTools.cs`: `RunToolsRequest.RecordVerifiedTaskSettle : Action<SettleInput>?`; `RunToolsServer.cs` wiring; `LocalServerOptions.RecordVerifiedTaskSettle` and `LocalTools.SettleCoreAsync` | Runner memory owns verified result, not an agent-writable file. Retain existing locked settle path and bundle-pin rejection. Reuse callback when moving lifecycle ownership. |
| `RunWorkspace.cs`: `VerifiedReturnRevision : string?` | Runner-owned checked commit pin; separate fresh workspace and per-attempt npm cache. #496 can associate plan/attempt ownership with the same workspace. |
| `RunCodeReturn.cs`: `ReturnAsync(HttpClient, HttpClient, string workspace, RunWorkspace, TaskDelivery, string branch, string? baseCommit, CancellationToken, Func<MessageTarget?, CancellationToken, Task<PublicKeyInfo>> recipient) : Task<bool>` | Checks branch and finished bundle head against the runner-owned revision; ordinary return behavior retained. Bind #496 evidence to this same revision rather than add another return path. |
| `NodeVerification.cs`: `PrepareAsync(string checkout, TaskDelivery, RunWorkspace fresh, CancellationToken) : Task<string?>`; `VerifyAsync(TaskDelivery, RunWorkspace agent, RunWorkspace fresh, CancellationToken) : Task<VerificationResult>` | Recipe entry points for pinned fetch, dependency policy, exact toolchain, restore and named checks. `NodeVerificationPolicy.ProjectPrefix(string checkout, IReadOnlyList<string> checks)` selects the single approved project. Keep these Node responsibilities when adapting the generic supervisor. |
| `Verification.cs`: `VerificationSnapshot`; `RunnerProtocol.cs`: `TaskDelivery.Verification`, `RunnerSettle.Verification`, `.VerificationAttempt`, `.VerificationDigest` | Existing service-authorized environment/recipe/version/definition digest, repository/remote/input/base commits and toolchain versions; runner report tied to delivery/attempt/digest. Extend additively with negotiated support when #496 introduces its stronger plan identity. |
| `LocalTools.cs`: `RetryAsync(RetryInput, CancellationToken)` | Replays original request, exact repository refs and `sourceThreadId`. The complete immutable execution snapshot remains server-owned; the client must not reconstruct it from a new environment lookup. The server must preserve and validate the entire source pin on retry/replay. |

## Deliberate omissions and remaining integration

This recovery does not implement #496's recipe-independent versioned plan, readiness key and
invalidation, native Windows/macOS boundary, or additional profile/tree/image/toolchain/policy
digests and run/nonce binding. The recovered snapshot and process result are the reviewed #571
shape, not a claim that this stronger boundary has been completed. #496 should own those
additive contracts and consume the Node recipe through the existing seams above.

#571 owns the approved Node/Linux toolchain and dependency recipe. Restore is registry-only with
deployer-enforced egress and per-attempt cache; checks run without network. This is distinct from
#389 offline reproduction and its three-way verdict, which is not implemented here. #317 remains
the privileged PR assignment/evidence consumer. No new generic executor, enrollment, release,
publication, deployment, or hosted-server implementation is included.

Required sends additionally require registry version 1 plus `requiredTaskRequestsSupported` and
the particular installed handler. The final hosted server must explicitly opt its catalog into
verified tools only after installing those handlers, and retain atomic admission for mixed
batches. Client-side negotiation never substitutes for server authorization or whole-pin retry
validation. Customer-visible CLI and orchestration changes need the help center updated when
the combined feature ships.
