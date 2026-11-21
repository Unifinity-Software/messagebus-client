# Pinned local OpenCode runtime

The supported implementation targets npm **opencode-ai 1.19.5**, upstream commit
`810c0eba1a7348036f8bdf61e6632d0c36c6876c`. It does not use the OpenCode 2 shared server.
Source inspection is backed by native coordinator verification on Windows at client commit
`2ac7c1a13cdb6a21f42151fe9bcf2bfa869b61bb`: the full client build and suite, public-contract and diff
checks passed (1,229 tests: 1,197 passed, 32 platform/environment skips, zero failures). The SRI-verified
Windows x64 1.19.5 package passed the production launch/isolation proof with fake providers: nine local
inferences and zero cloud requests or cloud inference after the positive-control baseline. These are
coordinator results; the implementation worker did not execute the package or tests. Actual LM Studio
inference, Mac two-model trials and the Windows/macOS three-family demo remain pending.

## Why the config confines model resolution

All links below name the exact commit, rather than mutable documentation or a guessed environment flag.

| Path | Pinned source and behavior |
| --- | --- |
| Repository config and file agents | [config loader](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/config/config.ts) and [config paths](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/config/paths.ts): `OPENCODE_DISABLE_PROJECT_CONFIG=true` skips project JSON/JSONC and repository `.opencode` discovery. Remaining config directories are the isolated global, home and explicit config directories. |
| Global/home scopes | [global paths](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/core/src/global.ts): XDG config/data/cache/state roots and `OPENCODE_TEST_HOME` control these paths. The implementation sets them, HOME and USERPROFILE to fresh owned paths. This use of the internal home override is deliberately version-pinned. |
| Accounts and remote config | [auth](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/auth/index.ts) accepts `OPENCODE_AUTH_CONTENT={}`. [database path](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/core/src/database/database.ts) accepts `OPENCODE_DB`; each launch gets a fresh database, so no active account/organization config is inherited. The original auth files and account DB are never written. |
| Model catalog | [models-dev](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/core/src/models-dev.ts) uses `OPENCODE_MODELS_PATH` before its embedded snapshot. An owned `{}` file prevents catalog collisions/inherited model options; fetch is disabled. |
| Providers and fallback | [provider resolver](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/provider/provider.ts) filters providers with `enabled_providers`, models with provider `whitelist`, and resolves language models from that filtered map. The only provider is `mbus-local`; its only model is `selected`, with an explicit checked API ID, bundled `@ai-sdk/openai-compatible` runtime, and loopback baseURL. A reference to any other provider/model fails resolution. Default/recent/small-model selection cannot select an absent cloud provider. |
| Auxiliary/subagent inference | [agent construction](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/agent/agent.ts) supports model overrides for build, plan, general, explore, title, summary and compaction. All seven and top-level `model`/`small_model` are pinned to `mbus-local/selected`. [compaction](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/session/compaction.ts) resolves its agent's model through the same provider service. |
| Plugins | [runtime flags](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/effect/runtime-flags.ts) and [plugin loader](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/plugin/index.ts): `OPENCODE_PURE=true` excludes config plugins and `OPENCODE_DISABLE_DEFAULT_PLUGINS=true` excludes built-ins. No user plugins/SDK hooks are copied. |
| Schema | [config](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/core/src/v1/config/config.ts) and [provider](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/core/src/v1/config/provider.ts) define these singular provider/options/id fields, agent model fields, allowlists and model limits/costs. |
| Managed/system scopes | [managed configuration](https://github.com/anomalyco/opencode/blob/810c0eba1a7348036f8bdf61e6632d0c36c6876c/packages/opencode/src/config/managed.ts) loads after inline content. The implementation refuses any managed directory or macOS managed plist, and inspection errors, before readiness/checkout and again at launch. It does not redirect managed settings to an empty location. |

Only the endpoint, selected API alias and that local endpoint's optional API key enter the fresh config. Inherited
small models, deprecated modes, custom agents, fallback providers, plugins, model options and variants are discarded.
The bounded baseline declares 8192 context tokens and 2048 output tokens; the operator must load a model with at
least that context. Existing configured cloud selections use the original snapshot and environment as before.
Successful LM Studio readiness is necessary as well as confinement. Readiness failures never mark usage local.
The parser reports known-zero inference cost only after token usage appears; positive reported costs are preserved.

This assumes the configured loopback server is operator-controlled LM Studio, not an HTTP redirector or remote
gateway. The operator must verify that server's actual inference destination. Configuration confinement covers
OpenCode's model resolver; authorized shell/MCP/web tools can network or invoke other programs. Coordination and
reporting also use the network. This is not a claim that the whole agent is offline or that electricity/hardware/tools
have zero cost. It is not an OS egress policy against an agent deliberately changing its own execution environment.

## Artifact provenance

Registry records: [launcher](https://registry.npmjs.org/opencode-ai/1.19.5) and
[Windows x64 binary](https://registry.npmjs.org/opencode-windows-x64/1.19.5).

The worker downloaded the launcher tarball without installation/execution and verified its registry SHA-512:

```text
opencode-ai-1.19.5.tgz
SHA256: 1f97e49381f8a42a629482eff8be98aa96d62e2e94410a8ec98e738f9c787680
SRI: sha512-tEqMvQahfhdDj8vceYpXtoIrvjPXm5XRfetWnPN5YISueU7adNx9JcUA3zZ+2GKuDVgDpGdQe1ZLtCZDjqodvA==

opencode-windows-x64-1.19.5.tgz (SRI verified and executed by the native Windows coordinator)
SRI: sha512-xCVbrA8A/qQTAdBIKyhK5tx3/mcq18yF82Mi8phry0MJb2631nnnpxF9m6FFfWAVHyer4qFGrO7hbWXesHQ1Gg==
```

## Native coordinator acceptance

Use the [native proof packet](../eng/opencode-proof/README.md) and its verified, task-owned setup
scripts. It includes exact Windows x64 and Darwin arm64 1.19.5 registry SRI pins, tar path checks,
archive/binary hashes and fail-preserving evidence. Tests never download packages automatically.

The opt-in `OpenCodeRuntimeProofTests` fixture executes the real package with separate local and
fake-cloud HTTP endpoints. It requires hostile global/home/project/auth/account/plugin positive
controls, all seven expected agents, explicit summary/subtask/compaction calls, observed API model
IDs, and no cloud requests on primary failure or unknown model. Its confined commands reuse the
production `AgentProcessLaunch` and `RunIsolation` result. A compiled fixture is not runtime proof;
the coordinator must execute it outside a task with permission and verify it did not skip. The native
Windows coordinator did so at the commit above: hostile global/home/project/account/auth/plugin
controls included two fake-cloud inferences before the baseline; confined primary, auxiliary,
subtask and compaction inference and failure/unknown-model negative controls passed with no
subsequent cloud traffic. See the packet for the recorded result and pending Mac execution.

`OpenCodeLocalHostTests` remains a separate opt-in loaded-LM-Studio smoke. It now requires the
verified binary digest, all seven expected agents and saves command output even on failure.
It is not the fake-cloud counter proof, the two-model suitability trial, or the production enrolled
runner handoff. Capture actual LM Studio inference logs independently before claiming local inference.

The packet keeps the authorized Mac execution context, actual Qwen/Gemma inventory IDs, four
180-second coding trials and Windows/macOS three-family demo explicitly pending. No worker may
clear InsideRun, execute test DLLs, change role permissions, install globally, alter personal
configuration/services or enroll/rekey a machine to bypass that gate.
