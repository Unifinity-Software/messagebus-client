# The client–service protocol

What `mbus` (the CLI, its local MCP server and its runner) and the messagebus.ai service exchange. The hosted service
at app.messagebus.ai talks to every released `mbus`, so everything on this page is a compatibility surface. The
normative definitions are the types in `src/MessageBus.Contracts`; this page says where to find them and the rules
that keep old and new versions working together.

## Compatibility rules

ACI inventories optionally include `deployment: { target: "azure-container-instances", azureResourceId: "..." }`.
The original `target` stays `docker`, so existing services can still enroll and run the machine. Services that
understand `deployment` preserve it and use its detailed target in Fleet. `MBUS_DEPLOYMENT_TARGET=aci` enables
these details; `MBUS_AZURE_RESOURCE_ID` supplies the ARM resource hint. Neither field authorizes Azure actions:
a workspace administrator must verify an Azure-side tag challenge and explicitly enable the stored binding.
An ordinary scan on another deployment clears the detailed metadata; an older inventory with no target leaves
existing deployment information intact. Azure action and managed drain behavior starts only after the service
has a verified binding and has read the runner's `supportsManagedDrain` capability.

Azure-only ACI bootstrap checkpoints a newly generated private key in a private Azure blob before enrollment.
The local `config.json` may contain optional `pendingRunnerEnrollment` (the runner API-key fingerprint) until
enrollment succeeds. In that state the CLI reuses the saved key, so retrying a lost enrollment response sends
the same public key and the service returns the existing machine. The bootstrap stores the completed identity
before starting the runner and restores it on subsequent starts. This adds no service route or required wire field.
Blob access uses the container's managed identity and an exclusive lease; the blob's Azure resource binding is
a bootstrap ownership check and never authorizes mbus lifecycle management.

- **JSON** is camelCase, written and read through the source-generated context in
  `src/MessageBus.Contracts/Json/MessageBusJson.cs`, which ignores fields it doesn't know. A new field is optional
  for both sides; an absent field means the old behaviour.
- **Nothing released is renamed or removed** (fields, enum values, routes, headers, close codes, tool names) without
  a deprecation period in which both the old and the new form work.
- **New behaviour is opt-in by capability.** The client uses it only when the service says it supports it, and the
  service sends it only to a client whose version (`X-Mbus-Client-Version`) or reported capabilities say it can
  handle it.
- **Minimum version.** The service reports the oldest `mbus` it accepts in the response header
  `X-Mbus-Min-Client-Version`. A runner older than that is closed with code 4426 and updates itself (see
  [Updates](#updates)).
- **Errors** carry a short machine-readable code in `X-Mbus-Error` (MCP errors: `McpErrorCodes`).

## Identity and request signing

Defined in `src/MessageBus.Contracts/Security/MachineAuth.cs` and `src/MessageBus.Contracts/Http/Http.cs`.

| `Authorization` scheme | Who uses it | For |
|---|---|---|
| `MbusCli <access token>` | `mbus login`, after the person approves it in the dashboard (`/api/cli/auth/*`) | Enrolling a machine and listing the workspaces it can join, nothing else |
| `MbusEnroll <token>` | A one-time enrollment token from an admin | The enroll request only |
| `MbusMachine <machineId>` | Every request from an enrolled machine: CLI, MCP server, runner | Everything a machine does |
| `MbusKey <key>` | An API key | Automation calling the REST API or `/mcp` |
| `MbusDelivery <token>` | A cloud agent sent one task | Heartbeat, progress and settle of that task only |

`mbus login` creates the machine's device key (ECDSA P-256) on the machine and registers its public half when it
enrolls (`POST /api/machines/enroll`). The private key lives in the device's key store. Customer-managed ACI
deployments can provision that identity through their secret store and restore it into owner-only local storage
on startup, as described in `docker/runner/README.md`. A machine request is signed with it:

- headers `X-Mbus-Timestamp` (Unix seconds), `X-Mbus-Nonce` (random, single use) and `X-Mbus-Signature`;
- the signature is ES256 in IEEE P1363 form, base64url without padding, over
  `MachineRequestSigning.CanonicalString`: the method in upper case, the path and query as sent, the timestamp, the
  nonce and the lowercase hex SHA-256 of the body, joined by `\n`;
- the service refuses a timestamp more than 5 minutes from its clock and a reused nonce.

Other request headers: `X-Mbus-Workspace` (when the path doesn't name the workspace), `X-Mbus-Client-Version` and
`X-Mbus-Client` (`cli`, `mcp` or `runner`), `X-Mbus-Agent` (the agent behind an MCP call), `X-Mbus-Request-Id`, and
W3C `traceparent` / `tracestate`.

### Frozen isolated machine domain (bounded HTTP integration; full runtime verification pending)

`MachineRequestSigningV2` is a separate additive builder. The existing `MachineRequestSigning.CanonicalString`
and v1 wire remain unchanged. The normal `ServerConnection.CreateAuthenticatedClient` HTTP pipeline now uses
the binding APIs below and signs v2 for complete isolated discovery. Other credential paths still require the
integration listed below. This bounded source checkpoint makes no regional availability or release-readiness claim.

Isolated discovery retains `product: "messagebus.ai"`, optional `capabilities: string[]` and all existing tokens.
The descriptive shape is `stamp:{stampId,geography,azureRegion,url,aliases}`. The security extension is exactly
`signIn.authority:{version:1,stampId,issuer,audience}` with sibling
`signIn.machineSignature:{version:2,required:true}`. Issuer and audience equal the normalized canonical stamp
origin and agree with `stamp.url` and `stampId`. Unknown unrelated additive properties are ignored.

Both security descriptors absent, with no expected or surviving isolated pin, means legacy v1 at the exact
selected endpoint, even when descriptive stamp metadata is present. Presence of either descriptor, including
explicit null, requires the complete valid consistent isolated bundle. Duplicate or ambiguously cased security
properties, malformed fields, partial bundles and unsupported versions refuse. Use `ServerDiscoveryParser`,
which tracks JSON property presence before generated deserialization; nullable DTO values alone cannot decide
this. Discovery failures and redirects are never evidence of legacy support.

The v2 header is exactly one `X-Mbus-Signature-Version: 2`. Missing, lower, unknown, duplicate and comma-joined
values cannot select v2. ES256 signs UTF-8 without BOM, IEEE P1363 fixed 64-byte `r||s`, encoded as canonical
unpadded base64url. The signed input is exactly these ten fields joined by one LF, with no final LF:

1. Literal `mbus-machine-v2`.
2. Normalized canonical credential authority origin.
3. Exact stamp ID (`ca`, `eu` or `us`).
4. Exact enrolled machine ID.
5. Actual exact `X-Mbus-Workspace` value, or an empty field only when the header is absent.
6. Invariant uppercase HTTP method.
7. Actual escaped origin-form raw path and query, including the selected base prefix and original query order.
8. Canonical nonnegative decimal Unix seconds, without leading zeros except `0`.
9. Exactly 22 canonical unpadded base64url characters representing 16 random nonce bytes.
10. Lowercase 64-character hex SHA-256 of the exact transmitted body bytes.

Reject control/whitespace/delimiter injection in identifiers and headers, empty present workspace headers,
invalid method tokens, absolute/network-path targets, fragments and malformed percent escapes. Do not decode,
sort, slash-collapse or re-encode the signed raw target, normalize the body, or compress content after signing.
The service must use its actual raw target and immutable local canonical authority/stamp, never Host or an
unsigned requester-provided authority. Proxies must preserve the exact signed target. Retain the existing
five-minute skew and local replay checks; validate provenance, placement and authorization before any nonce,
Seen, audit or endpoint mutation. V2 cannot be reinterpreted as v1 by stripping its version header.

`ProtocolOrigin` normalizes scheme, IDNA host and effective port; removes default ports/root slash; preserves
nondefault ports; rejects userinfo, paths, queries, fragments, whitespace, backslashes, trailing DNS dots,
escaped hosts and scoped IPv6. Isolated policy requires HTTPS except exact loopback HTTP under explicit local
fixture policy. `ServerEndpoint` separately retains escaped API base scope with a trailing separator. Equal
canonical authority does not make different selected origins or base paths interchangeable for saved credentials.
Its destination-scope check refuses encoded path separators, encoded traversal and nested escapes rather than
granting a scope that server-side path decoding could change. Query escapes remain untouched.

`ServerBindingPolicy.Evaluate` is a pure decision over one current anonymous observation, selected endpoint,
an explicit complete `ServerBindingFloor` and optional trusted local expectation. It never authorizes a send.
Unknown/corrupt binding state refuses. Any origin-keyed ledger entry or surviving credential/checkpoint ever-pin
establishes the isolated floor. Missing/lower/malformed/conflicting discovery cannot erase it. Integration must
read all state under the existing single profile security lock, reconcile and durably persist the ledger and
credential snapshots before first credential transmission, and preserve the floor through every writer, logout,
refresh, expiry and failure. The persistence APIs implement storage; the shared gate below integrates normal
machine/API-key HTTP sends. It does not yet cover every credential category.

`ConfigStore.WithProfileSecurityLockAsync` supplies an explicit `ProfileSecurityTransaction` using the existing
`cli-session.lock` and process semaphore. `LoadBindingFloor()` rereads the independent `server-bindings.json`
ledger and both saved files under that lock. `PrepareBinding(selected, discovery, expected)` evaluates the pure
policy, durably publishes the origin pin first, attaches matching exact-endpoint snapshots to reused config and
session credentials, then returns an immutable `ServerBindingSnapshot` for new credentials. A returned snapshot
is storage evidence, not permission to send; callers still need current discovery for each actual send. Existing
session-lock callbacks receive that explicit context and use its `Load`, `Save`, `LoadSession`, `SaveSession`
and `DeleteSession` operations, including through awaited helpers. Concurrent operations through the same
context are rejected before reading or writing state. Ordinary store methods acquire the profile lock. The
published `WithSessionLockAsync<T>(Func<Task<T>>, CancellationToken)` signature remains as an obsolete
compatibility overload: within its callback, ordinary `Load`, `Save`, `LoadSession`, `SaveSession` and
`DeleteSession` on that exact store route to the held context's guarded operations, including after awaits.
Inherited children share its operation guard; overlapping or completed-context operations refuse before
state access. This routing never grants unguarded ownership. Outside that compatibility scope, ordinary
operations always acquire the lock. New callbacks pass the explicit context to nested helpers, including
send preparation and consumption. Acquiring another transaction inside a compatibility callback refuses
with migration guidance. Context use after release refuses, and the HTTP send gate rejects a context
belonging to another store.

The ledger root is `{schemaVersion:1,bindings:{"<normalized transport origin>":<snapshot>}}`. Each snapshot has
exactly `version:1`, `selectedEndpoint`, `stampId`, `authorityVersion:1`, `issuer`, `audience`,
`minimumMachineSignatureVersion:2` and `machineSignatureRequired:true`. The ledger key establishes the floor for
every base path on that origin; its snapshot endpoint does not grant reuse of credentials at another base path.
Optional `serverBinding` on `CliConfig` (including its pending runner enrollment checkpoint) and `CliSessionFile`
uses the same snapshot schema. Absent fields in valid never-pinned old profiles remain optional. A missing
ledger with a surviving snapshot, inconsistent endpoint/tuple, duplicate security member, unsupported schema,
unreadable file or malformed session refuses; malformed sessions no longer silently become absent sign-ins.

Every config/session writer rereads and merges the floor. Rotation, clearing pending enrollment, unrelated
settings and local sign-out retain it; session deletion never removes the ledger. Files use owner-only temporary
creation, disk flush and same-directory atomic replacement, with directory metadata flush on Unix and
write-through replacement on Windows. New dedicated profile directories are created with private permissions.
Isolated preparation validates existing directory ownership and privacy and refuses unsuitable directories
without changing their mode or ACL. Windows permits the current user, SYSTEM and local administrators;
Unix requires current-user ownership and no group/other permission bits; macOS also refuses ACL grants to
other principals without modifying the ACL. Directory symlink/reparse aliases
are refused, including aliases in parent components. Existing valid private profiles need no permission repair.
`server-bindings.pending` is durably published before a multi-file transition. Any interruption retaining that
marker refuses further state use until profile restoration; normal commands never delete it to reset a pin.
Failure before preparation returns must not permit credentials. Local owner deletion/rollback of all state or an
older binary ignoring the fence remains outside this protection. Native filesystem/crash verification is still
required; compiling the fault-boundary fixtures is not runtime durability evidence.

The trusted local `MBUS_SERVER_BINDING` JSON object is parsed by `ServerBindingExpectation.Parse`:

```json
{"version":1,"transportOrigin":"https://eu.stamps.example.test","stampId":"eu","issuer":"https://eu.stamps.example.test","audience":"https://eu.stamps.example.test","authorityVersion":1,"minimumMachineSignatureVersion":2,"machineSignatureRequired":true}
```

It strengthens a floor and cannot replace a conflicting pin. For alias transport it supplies an explicit exact
local transport-to-canonical mapping; current discovery must also list that selected alias. Discovery aliases
alone never authorize a new mapping or credential migration. The canonical signing authority stays separate
from the selected alias transport. Changing selected origin/base requires fresh explicit credential binding.

First contact with no provisioned or surviving pin cannot distinguish an old server from a response with its
entire isolated advertisement stripped. Isolated automation must provision the exact expected binding and a
durable writable `MBUS_HOME`, validated before credential probes. This is trusted local deployment input, not a
new server-signed manifest. No geography, availability, residency or initial stripping detection can be inferred
from descriptive metadata or this core. All credential paths still require fresh anonymous validation before
each actual send, no stale-context fallback, no credential redirects and no host/v1 retry. Full native client
tests, real HTTP/WS/proxy and persistence fixtures, combined independent review and client-first publication must
precede server package pin/enforcement. Public update-feed release-key verification remains unchanged.

`ProfileSecurityTransaction.ValidateProvisioning(selected, expectedJson, durableHome)` validates the trusted
expected tuple against the selected origin and current floor, requires an explicit absolute home matching this
store, and verifies owner-only durable write access before future caller probes. The caller passes the returned
expectation to `PrepareBinding`; neither API reads process environment or discovers a server. A writable path
cannot prove that a container volume survives replacement: provisioning must still enforce the persistent mount.

### Per-send HTTP gate and remaining integration

`ServerSendGate(host, store, server)` owns a separate anonymous `HttpClient` without default headers. The optional
additive `CliHost.CreateDiscoveryHttpHandler` supplies its transport; when omitted, a fresh raw handler from
`CreateHttpHandler` is used. These are trusted transport factories, never an existing authenticated client.
Built-in HTTP handlers disable redirects, cookies, preauthentication, server credentials and default proxy
credentials. Custom injected handlers must uphold the same policy and must not inject credentials or follow
redirects. The real host has separate explicit discovery and API factories. The overload taking `ConfigStore`,
server URI, anonymous handler factory and environment reader is the seam for hosts without `CliHost`.

`DemandDestination(finalUri)` checks the exact selected origin/base scope before metadata or authentication is
attached. `PrepareSendAsync(finalUri, cancellationToken, optionalLockHeldTransaction)` checks the destination,
validates supplied `MBUS_SERVER_BINDING` and explicit matching writable `MBUS_HOME`, fetches current anonymous
`GET <base>/api/server`, strictly parses the successful response and calls `PrepareBinding` under the existing
profile security lock. Every actual send repeats discovery; no TTL, stale fallback or legacy choice on failure
exists. Discovery failure, redirect, downgrade and persistence failure produce terminal `ServerBindingException`.

The returned `PreparedServerSend` carries the selected endpoint, strictly parsed current `Discovery` (for
approval/destination checks), and optional durably prepared snapshot. It is
single-use: `ConsumeAsync(finalUri, sendCallback, cancellationToken, optionalLockHeldTransaction)` reacquires the
profile lock and rereads the ledger and every surviving snapshot immediately before consumption. It evaluates
the prepared discovery against that current floor and requires an isolated snapshot to retain its durable pin.
Thus a pin published by another process also blocks a queued legacy send, without any shared generation counter.
Successful isolated preparation invalidates outstanding legacy contexts in this process; failed preparation
invalidates all outstanding contexts for that profile/origin. These counters only refuse; they never grant sends.

The consumption callback receives a scoped `ServerSendAuthorization`. The additive
`MachineRequestSigner.CreateHeaders(authorization, actualRequest, bodyBytes)` signs only once inside that callback,
checking its lifetime, current generation and final destination. It uses the canonical issuer/stamp plus the
actual single workspace header and final escaped URI. Legacy results delegate to the unchanged three-argument
v1 signer. This replaces the earlier checkpoint's standalone `Consume` and prepared-context signer overload:
headers must not escape the lock scope for a later send. A snapshot alone never grants a send.

Buffer content before consumption. The normal handler buffers machine and API-key request bodies outside the
lock, then attaches credentials/signatures and awaits the actual transport send inside the callback. The process
semaphore and exclusive profile file lock remain held until the transport returns response headers or fails.
Consequently a competing durable pin either commits before the final floor read (legacy refuses), or waits
until that send finishes. Response-body reading occurs after the scope. This serializes sends per profile and
can delay other profile operations by the request timeout; callbacks must not acquire another profile lock or
call ordinary store methods. The dedicated anonymous discovery transport never enters this callback.

Callers already holding a session/profile transaction pass that explicit same-store context to preparation and
consumption. Omitting or substituting the preparation transaction refuses instead of trying to reacquire it.
No ambient ownership is inferred. The transaction reserves its operation guard across the callback, rejects
overlapping reads/writes/pins, and outer completion waits for any already-running send before releasing the lock.
An authorization expires when its callback ends. Request version headers remain selected solely by discovery.
Bytes already sent cannot be recalled; remote configuration can change independently of the local lock, and
server enforcement remains necessary. This does not authorize cached discovery or later reuse of signed headers.

`ServerConnection.CreateAuthenticatedClient` rejects reuse of a stored machine/workspace at another selected
endpoint/base before loading the machine key or reading its workspace for headers. `CreateSigner` enforces
the same scope before key loading. Equal API-key fingerprints only select the old machine signer at the exact
saved endpoint. The normal HTTP transport disables redirects and rejects all 3xx responses without replay.

Integrated callers are the normal client returned to CLI machine/inventory/key operations, doctor authenticated
probes, local MCP and script API operations, runner REST/tool services, and attachment-link API requests that
use that client. Factory use supplies this gate; separate transports do not inherit it automatically.

`CliSession` now uses the same gate for device start/poll, access-token sends (including workspaces and
enrollment as the signed-in person), each 401 retry, refresh and logout. Its public constructor,
`AccessTokenAsync`, `SendAsync` and `LogoutAsync` signatures remain unchanged. Even a still-fresh saved token
requires current anonymous validation before return; the actual request independently validates again.
Final destinations are resolved and checked against the exact selected endpoint/base before any refresh or
credential attachment. Bodies are buffered before consumption; access headers are attached only inside the
lock-held send and cleared on exit. All sign-in 3xx responses refuse without replay. Refresh, logout and
approved-session persistence pass their explicit same-store transaction to nested gate operations rather than
reacquiring the profile lock. Refresh rotation preserves the prepared snapshot. Refused or unavailable logout
reports only local forgetting, and session deletion retains the independent ever-isolated ledger.

Browser approval is checked before device start, including `--no-browser`. Require an absolute HTTP(S) URI
without userinfo, fragment, backslash, whitespace or control characters, and reject an existing `code` query
parameter. Existing unrelated query parameters are retained when adding the escaped user code. Current discovery
must still report the same approval address at start. In isolated mode its origin must be the canonical authority
or the exact selected alias already accepted by local provisioning/persisted binding and server configuration;
another advertised alias grants no permission. Never-pinned legacy servers retain explicit split-port and tunnel
approval addresses after the basic URI checks. The start's exact binding is retained for every device poll.
Approved tokens may be saved only by the collecting sign-in with that same prepared endpoint/authority, after
fresh validation and a current ledger check inside the session transaction; changed or stripped discovery cannot
relabel already-issued tokens. These are source integrations, not runtime verification or release readiness.

Remaining required work is explicit: anonymous caller-key and enrollment-token probes, environment-runner factories/checkpoint
reuse, WebSocket connect/reconnect and terminal refusal classification, attachment transfer/query credentials
and all separate MCP/script/dead-letter transfer clients, public signed update-feed transport and diagnostics.
`CreateAnonymousClient` now restricts its raw HTTP transport, but is not itself a per-send credential gate.
The old three-argument signer remains available for compatibility and is not evidence of gated WS integration.
Native full suites, real isolated HTTPS/WS/proxy fixtures, remaining transport integration, independent combined
review and client-first publication are required before server enforcement or any regional availability claim.

## REST routes the client calls

`ApiRoutes` in `src/MessageBus.Contracts/Http/Http.cs` lists them. The client uses: enrollment and the machine's own
record (`/api/machines/*`), its inventory of agents and tools (`PUT /api/machines/inventory`), public keys and key
grants (`/api/w/{workspaceId}/keys*`), attachment links (`/api/w/{workspaceId}/attachments/*`,
`/api/w/{workspaceId}/deliveries/{deliveryId}/attachment-links`) and the MCP endpoint `/mcp`.

## MCP tools

`src/MessageBus.Contracts/Mcp/McpTools.cs` (`McpToolNames`) names the tools, and `McpToolCatalog.cs` gives each one
its description and input schema. The catalog shared by the service's remote `/mcp` endpoint and the local
`mbus mcp` server includes:

`send`, `send_many`, `wait`, `watch`, `inbox`, `progress`, `settle`, `status`, `cancel`, `move`, `ensure_project`,
`find_project`, `complete_project`, `directory`, `describe_role`, `subscribe`, `fetch_attachment`, `retry`,
`request_approval`, `wait_approval`, `open_inbox`.

`fetch_attachment` and `retry` exist only on the local server (`McpToolNames.LocalOnly`): they need this machine's
keys and git. The local server (`src/MessageBus.Mcp`) is an encrypting proxy to `/mcp`: agents write and read
plaintext message bodies; the local server seals them into `sealedBody` before calling the service and opens them on
the way back, and hides the `sealed…` fields from the schema agents see. A client that calls `/mcp` directly sends
plaintext, which is protected in transit and at rest but not end to end.

Adding a tool, or a field to a tool's input or result, follows the compatibility rules above: an older local server
must keep working against a newer service, and the reverse.

## Role proposals (optional version 1)

`GET /api/server` identifies `product: "messagebus.ai"` and keeps `capabilities` as an optional
array of string tokens. Role proposals require both `"roleProposalsV1"` in that array and the optional
top-level `roleProposals: {version: 1, baselineVersion: "role-proposal-readonly-v1",
normalizerVersion: "role-definition-v1"}` descriptor. Existing `approvalAffectedV1` and `settleAttemptV1`
tokens retain their meaning. CLI login uses this same discovery shape and also accepts servers that omit
both optional discovery fields; sign-in settings remain under `signIn`.
Missing or wrong product, absent token or descriptor, malformed data, or
unknown protocol, baseline or normalizer versions mean
`unsupported`, independently of the product version. Existing tools keep working. Proposal tools are hidden
when unsupported; forced calls return a nonretryable `unsupported`. Endpoint absence triggers one capability
refresh. There is no role CRUD fallback.

`Roles/RoleProposals.cs` defines the client contracts. Local-only MCP tools are `propose_role`
(`ProposeRoleInput` → `RoleProposalSubmitted`), `get_role_proposal` (`GetRoleProposalInput` →
`RoleProposalDetail`) and `wait_role_proposal` (`WaitRoleProposalInput` → `RoleProposalDetail`). They adapt
REST under `/api/w/{workspaceId}/role-proposals`:

| Method/path | Request and result |
|---|---|
| POST root | `{operation: create|update, definition, rationale, idempotencyKey, agentId, coordinatorRecordId, inboxId}` → 201 acknowledgment; identical identity/key/content retries return the existing proposal |
| GET `/{proposalId}` | Query `coordinatorRecordId`, `inboxId`; `X-Mbus-Agent` identifies the original agent → own authoritative detail/result |
| GET `/{proposalId}/wait` | Same provenance plus `timeoutSeconds` (0–30) → authoritative detail/result at change or deadline |

The host fills provenance; it is absent from model-visible input schemas. A proposer cannot list workspace
proposals. Only a current workspace owner using the dashboard cookie can preview, approve or reject;
there are no agent/CLI decision tools. The server requires an approved enrolled machine in the workspace,
MessagesSend and an agent belonging to that machine for proposer operations. API keys, cloud delivery
tokens and a CLI person's sign-in do not authorize them. Owner access and decision authorization require a
fresh membership read. Admin status or role ownership alone does not grant owner review authority.

`RoleDefinitionV1` is a complete replacement: name, displayName, description, delivery, requiredCapabilities,
limits, alertRules, access, runEnvironment and instructions. No delete, BuiltIn, workspace, counters,
timestamps, attribution or signature fields are accepted. The definition rejects unknown top-level properties;
the server validates all nested properties, enum values and storage limits. Name is fixed at submission.
Omitted create run settings use the server's expanded safe baseline; the client leaves them absent rather
than substituting permissive runner defaults. `RoleProposalRunEnvironment`, `RoleProposalPermissionProfile`
and `RoleProposalModels` use nullable fields at every level: `{ "runEnvironment": { "timeoutSeconds": 7200 } }`
forwards only that timeout, with no permission profile, checkout, tools or model defaults. Explicit `false`
and empty lists remain explicit requests. Update omissions inherit the captured base. The server owns
normalization, warnings, canonical hashes and review revisions; generic `MessageBusJson` serialization is
not the canonical digest encoding. Body limit is 256 KiB UTF-8, canonical definition 128 KiB, instructions
1–30,000 UTF-16 units, rationale 2,000 characters. Existing stricter field/collection/storage bounds apply.
All exchanged types use the source-generated `MessageBusJsonContext`, preserving trim/AOT support.

`pending` and `applying` at a wait deadline are still pending work, not rejection or failure. Only `approved`
with the authoritative immutable receipt means the role was applied. Other terminal states are `rejected`,
`expired`, `conflict`, `failed`. Pending TTL is 24 hours of server time. Stale review never silently rebases.
The server commits role, published instructions, approved state and receipt atomically; transport failure
during applying is resolved by reading the outcome. Receipts survive subsequent role edits/deletion.
`SignedBy`/`approvedBy` are human attribution; signature/key fields remain null and do not claim cryptographic
signing. Original requested and applied edited definitions are distinct.

Proposal errors share `{code,message,retryable,field?,proposalId?,status?,currentRevision?}`. Codes include
400 `invalid-input`, 401 `unauthenticated`, 403 `forbidden`, 404 `not-found`, 409 `stale-review`,
`base-conflict`, `already-terminal`, `idempotency-conflict`, 413 `too-large`, 429 `limit-reached`, 503
`unavailable`; the local capability error is `unsupported`. Added `McpError` fields are optional.

Runner authority is an independent, non-request value: Interactive, ResumedCoordinator, Worker, Unknown.
It is checked before nullable/wildcard tool allowlists, capability discovery and forwarding. Worker-created
inboxes, orchestrator profile names and InboxTarget deliveries grant no proposal access. Any delivery/relay
marker without a working pipe fails closed, including missing/corrupt delivery files. A CLI inside a run uses
that pipe and never falls back to direct machine authentication.

Protected local session records persist immutable origin, random coordinator record id, workspace, machine,
agent, agent kind and resumable session identity. Legacy records have Unknown origin. Resumed authority
requires the runner's existing owner/session checks, exact identity/inbox matching and the original log;
mutable RunDeliveryId, request arguments and coordination claims do not establish origin. Explicit recovery
can retain the same expired record; it cannot silently bind another session/inbox. Own GET/wait remains
available after inbox expiry and results never reroute to another inbox. Before sending a proposal, the
local host durably marks its original record for retention independently of expiry and host lifetime;
opening unrelated inboxes cannot prune that provenance. These are runner/local-host
guarantees. The coordinator fields are not a cryptographic credential: a holder of the underlying machine
credential can use raw HTTP for that machine's proposals. This adds no stronger raw-HTTP session boundary.

CLI equivalents are `mbus roles propose --input proposal.json --agent codex --json`,
`mbus roles get-proposal <id> --inbox <original-inbox> --json` and
`mbus roles wait-proposal <id> --inbox <original-inbox> --timeout 30 --json`. Submission input has the
MCP input shape; `--input -` reads stdin. Interactive CLI responses, including ambiguous submission errors
after opening an inbox, additionally include local `coordinatorInboxId` for subsequent commands. Before
POST, the CLI durably binds the retry key to the original record, scoped to server, workspace, machine and
requested agent selector. Repeating the original command reuses that record even if the response was lost;
changed content under the same key remains subject to the server's idempotency conflict checks. Concurrent
local submissions with the same binding are serialized by an exclusive file lease; a busy lease fails with
a retryable error. `--inbox` can reuse an original saved interactive record;
inside a run the pipe supplies its exact binding and rejects agent/inbox overrides. No command applies a role.
Customer help-center documentation should cover this review flow before release.

`AgentRole.generationId` and `legacyInstructionCutoff` are optional snapshot metadata. Capture generation
with the role and use `(role, generation, version)` for instruction caches; never resolve a new generation
by name for an old snapshot. Missing generation denotes frozen legacy storage, not the current generation.
The cutoff bounds the committed legacy prefix. Recreated roles use a new generation without legacy fallback.
## Optional approval impact and settlement attempt

`GET /api/server` may include `capabilities: ["approvalAffectedV1", "settleAttemptV1"]`.
Trust these only when `product` is `messagebus.ai`. Each upgraded producer checks the actual
configured target through its authenticated HTTP client, without a positive cache (request
`Cache-Control: no-cache, no-store`). Missing, unknown or failed discovery means unsupported.
Servers advertise each capability only when its complete implementation is deployed and enabled.

`request_approval.affected` and `ApprovalResult.affected` are optional plain text. It is an
author-supplied impact description, never verified targets or authority to act. Normalize CRLF
and CR to LF, trim leading/trailing whitespace, and map empty to null. Validate before sending:
at most 1000 Unicode scalar values after normalization (supplementary characters count once),
reject invalid surrogates and controls other than LF/TAB; never truncate. Controls are rejected
before trimming too. The local proxy scans affected for secrets alongside title/detail.
Both local and remote generated schemas expose affected with `maxLength: 1000`.

For every non-null affected request, the local producer first freshly verifies
`approvalAffectedV1`; unsupported discovery refuses before POST. It never drops affected and
retries. The server must also refuse affected-bearing requests when the feature is disabled,
including a rollout race after discovery. Request and wait responses echo the normalized
affected and `requestDigest`. The local proxy rejects a missing/different echo or missing digest
as a protocol failure, never authorization. For requests made in the same local session it also
checks later wait responses against the original affected and digest. Affected participates in
the server's v1 digest exactly as normalized and displayed; omitted/null affected uses the legacy
digest path. Stored old digests must not be rewritten. New client/old server supports omitted
affected and explicitly refuses affected; old client/new server remains unchanged. A new
client/new server round-trips affected only with the capability and exact response echo.

`SettleInput.attempt` and `RunnerSettle.attempt` are optional integers copied from
`TaskDelivery.attempt`. With `settleAttemptV1`, the server must reject a stale mismatched attempt
or return that attempt's immutable prior receipt, never apply it to a newer lease. Direct remote
MCP callers may supply the delivered attempt; the local agent's schema hides it. Runner-owned
local tools inject only their trusted in-memory delivery attempt, overriding agent arguments
and kept-answer files, and never trust the agent-writable delivery file for it. `AgentRun` also
copies its own delivery attempt, never the agent's settle file. Both network producers freshly
gate the field on `settleAttemptV1` and omit it against older/unknown servers, preserving the
existing settlement behavior. Old untagged submissions cannot prove identity across a reused
delivery: consumers must qualify that history as identity-incomplete rather than inventing an
attempt or claiming exactly-once observations. No wire lease history is added.

## The runner connection

`RunnerStatus.supportsManagedDrain` enables reversible idle-stop drains. When true, the service
can send `DrainRequest.managedDrainId` with an operation ID. The runner keeps active tasks running
and refuses new work until it receives `resume: true` with the same ID. Another operation cannot
replace or resume that drain. Manual drains and update/shutdown restrictions remain in force.
`RunnerStatus.managedDrainId` identifies the current managed drain. These fields are optional.
The service must read `supportsManagedDrain` before sending either operation, because an older
runner interprets every drain request as a manual drain. A managed request ignores `deadline`.


Defined in `src/MessageBus.Contracts/Runner/RunnerProtocol.cs`; the transport is `src/MessageBus.Runner.Transport`.

1. The runner opens an outbound WebSocket to `/api/runner/connect` with subprotocol `mbus.v1`, signed as its machine,
   with `X-Mbus-Session` (its process's session id) and `X-Mbus-Slots` (free slots).
2. HTTP/2 runs over that socket with the **service as the HTTP client**: the service calls the runner's routes
   (`RunnerRoutes`): `/deliveries` (offer a task; the runner answers within seconds), `/deliveries/cancel`, `/drain`,
   `/rescan`, `/update`, `/status`, `/slots`, `/policy`, `/connect` and `/tool-setup`.
3. The runner calls the service back over ordinary HTTPS (`ServiceRoutes`, all under `/api/runner/`): `heartbeat`
   (keeps its tasks' leases), `progress`, `settle`, `inventory`, `policy`, `run-approval`, `update-progress`,
   `agent-capacity`, `repo-credential`, `telemetry-credential` and the `tool-setup/*` routes.
4. Queues are the durable record; the socket is the fast path. A runner that reconnects is offered whatever is still
   waiting for it.

Timing and close codes come from the public `Groundhog.Protocol` package: the service pings every
`RunnerProtocol.PingInterval`, drops a connection silent for `SilenceTimeout`, and the runner reconnects after 1, 2,
4 … 30 seconds with jitter. Close codes (`RunnerCloseCodes`):

| Code | Meaning | Runner does |
|---|---|---|
| 1000 | Normal close | Stops |
| 1001 | The service is restarting | Reconnects with backoff |
| `CredentialRevoked`, `MachineRemoved`, `SessionReplaced` | Revoked, removed from the workspace, or a newer session took over | Stops; never reconnects |
| `TooManyConnections` | Too many connections for this machine | Reconnects with backoff |
| 4426 | This `mbus` is older than the service accepts | Updates, then reconnects |
| 4403 | The machine waits for an admin's approval | Reconnects later with a long backoff |

## End-to-end encryption

Message bodies and attachments are encrypted on the sending machine and opened on the receiving one; the service
stores public keys and sealed key grants and can't read either. [security-model.md](security-model.md) describes the
scheme in full: AES-256-GCM content, content keys wrapped with ECDH-ES+A256KW to the target role's or the
workspace's P-256 key (RSA-OAEP-256 for RSA recipients), attachments as a chunked stream (`MBS1` header, 64 KiB
chunks), and role keys handed to machines as signed grants. The wire form is `EncryptionInfo` next to the
ciphertext. Changing an algorithm, a layout or a signed canonical string is a protocol change: old ciphertext must
still open for as long as it is kept.

## Attachments

`src/MessageBus.Attachments.Client` packages a repository range as a git bundle (or a patch, or takes a file), seals it
to the recipient's key, asks the service for an upload link, uploads the ciphertext and puts an `AttachmentRef`
(id, name, format, size, SHA-256, content type, branch and base commit for bundles, and its `EncryptionInfo`) in the
message. The receiver asks for a download
link, downloads, checks the hash, opens it and, for a bundle, verifies it before fetching it into the repository.

For local `send` and per message in `send_many`, attachment order is existing `attachments` followed by the
results of packaging `attach`. Only the first Git bundle in that order is eligible for automatic incremental
packaging, send history bookkeeping, and runner checkout history recovery. Files and patches do not take that
position. Later bundles default to whole history. An explicit nonempty companion `base` is honored and the
send notice lists any prerequisite commits advertised by the bundle. An empty `base` requests whole history. If
`fetch_attachment` reports missing prerequisite commits, fetch into a repository that has them or request a
resend with `base: ""`.

The local attachment spec accepts optional `expectedHead` for Git bundles in `send`, `send_many`, and `settle`.
It is a full 40- or 64-digit hex commit SHA checked against the branch advertised in the finished bundle before
its upload. A mismatch reports the attachment field and both commits. A send or batch with a mismatch is never
published. A runner settle with any pinned bundle fails if packaging any attachment fails and discards an earlier
kept reply. Staged uploads from earlier attachments can expire. The local server consumes `attach` and sends only
the packaged attachment references to the hosted MCP endpoint.

Local `retry` can repackage a single bundle or patch from the supplied repository. To resend a task with
multiple code attachments, use a fresh `send` with an explicit repository and branch per bundle or patch and
set `expectedHead` on each bundle to pin the intended tip.

## Updates

Defined in `src/MessageBus.Contracts/Releases`. `mbus` installs updates two ways:

- **As a .NET tool** (`MessageBus.Ai.Cli` from nuget.org, the usual install): `dotnet tool update`, run by `mbus
  update` or by the runner's service through a private applier copy.
- **As a standalone file**, from a signed feed the service serves anonymously at `/api/releases/{channel}/{file}`
  (channels `stable` and `preview`; a mirror or a folder can serve the same layout). Each channel holds
  `manifest.json` and its detached signature `manifest.json.sig` (ECDSA P-256, base64 or base64url, DER or P1363),
  `manifest-<version>.json` for pinned versions, and the files the manifest names with their size and SHA-256. Only
  a manifest signed with the release key built into that `mbus` is installed.

The service asks a runner to update with `POST /update` on the runner connection (`UpdateRequest`: version or
`latest`, whether it is pinned, the channel, a mirror, and how the runner is installed). The runner takes no new
work, lets running tasks finish, reports each stage on `/api/runner/update-progress`, installs, and exits with code
75 so its service manager starts the new version. It reports how the update went with its next inventory
(`RunnerUpdateReport`). Versions compare as SemVer and ignore `+build` metadata (`ReleaseVersion`).

## Making a change to the protocol

1. Change the client here first, with this page updated. The new client must still work with the service that is
   live now.
2. Release the client.
3. The service adopts the release and starts using the new behaviour.
4. Users update `mbus`.

## Required task verification

The local `directory` accepts optional `verificationRepositoryId` in `github:<numeric-id>` form.
The local MCP server forwards the ordinary role filters to the service's `directory` and uses the
same authenticated HTTP client for
`GET /api/w/{workspaceId}/environments?repositoryId=github:<numeric-id>`. This endpoint requires
`workspace.messages.send`. Its response is exposed as optional `DirectoryResult.verification`:

- `registryVersion` (currently 1) and `requiredTaskRequestsSupported` (boolean).
- `recipes[]` with `id`, `revision`, optional `nodeMajor` and `checks[]`. The initial recipe is
  `node-npm-ci` at revision 1 with Node 24 and checks `test`, `typecheck` and `build`.
- `environments[]` with `id`, `version`, `recipeId`, `recipeRevision` and
  `approvedSharedMachineCount`. The count records approval, independent of availability.

Ordinary directory calls make no discovery request. A 404 from the discovery route leaves the role
directory intact and omits `verification`. Missing discovery, an unknown registry version or
`requiredTaskRequestsSupported: false` blocks required dispatch. Discovery describes approvals for the
specified repository. The verified send independently authorizes the pinned execution target and the
requested checks, including when no eligible runner is currently available.

A role task can require a workspace-approved environment through the optional
`SendInput.verification` object `{environment, requiredChecks}`. Both properties are required when
the object is supplied. Phase-one check keys are `test`, `typecheck`, `build` and `browser`.
Browser verification is blocked until a compatible implementation is enrolled. The hosted service
must validate role permission, workspace approval, repository provenance and check selection before
queuing any work. Labels, encrypted bodies and bundle metadata cannot establish repository provenance.

The local `send` tool and `mbus send --input` accept the same object. `mbus send` also accepts
`--environment <id>` with repeated `--required-check <key>`. The local server sends the sealed
request to the service-only `send_verified` tool on `/mcp`, using the same input and result types as
`send`. A batch containing any such request uses `send_many_verified` with the ordinary batch
input and result types. The service must validate verification for the whole batch before
enqueueing any member. A service that does not implement the new tools rejects the call. The local
server never retries through the ordinary send tools. The ordinary service send tools must reject
explicit verification requirements and must never downgrade an inherited requirement. Dedupe must
not return a task with weaker requirements.

Before packaging a required send or any member of a mixed batch, the local client reads the
authenticated `/environments` registry (without a repository filter) and requires
`registryVersion: 1` and `requiredTaskRequestsSupported: true`. It then checks `tools/list` for
the particular verified handler. A listed tool alone never authorizes dispatch. A missing,
disabled, malformed or unsupported registry, or a missing handler, refuses the whole request
without creating ordinary children. Ordinary sends do not make these negotiation requests.
`McpToolCatalog.Tools(local: false)` omits verified tools and optional verification request schemas
by default. A service with their handlers installed explicitly uses
`Tools(bool local, IReadOnlySet<string>? only, McpCatalogFeatures features)` with
`McpCatalogFeatures.VerifiedTasks`. The original two-parameter signature and all constructor
signatures remain available. The flags in `MessageBus.Contracts.Mcp` are `None = 0`,
`VerifiedTasks = 1`, `Retarget = 2`, and `RoleProposals = 4`; the latter two reserve integration
values and do not enable additional tools here. Name filtering cannot enable an unsupported
feature. Local `send` and `send_many` retain the supported verification request schema and route
negotiated requests to the service-only tools, which remain hidden locally. Catalog visibility
never replaces the live registry/handler preflight or service authorization.

The service persists the normalized request as `MessageEnvelope.verification`, including on the
root message used by local retry. Retry sends it again with `sourceThreadId`. The service must
check the source thread's persisted requirements even when an older client omits this object, and
retain the requirements and pinned definition on retry and replay. The service rechecks approvals
and runner eligibility at claim and checks that the immutable input still matches its authorization.

`TaskDelivery.verification` is optional and carries the service-pinned
`{environment, recipe, version, digest, nodeVersion, npmVersion, repository, remote, revision,
baseRevision, requiredChecks}`. All members of a supplied snapshot are required. The first recipe
is `node-npm-ci`. `repository` is the trusted repository identity, `remote` the authorized HTTPS
remote, and `revision` and `baseRevision` full immutable commit IDs. The service sends required
deliveries exclusively to positively enrolled, compatible runners. A runner that cannot provide
the required execution boundary declines before checkout with `verification-unavailable`. The
service records the blocked reason independently of ordinary task completion.

Eligible runners report `verification:node-npm-ci-v1` in `MachineInventory.capabilities`.
Runtime enrollment and capability advertising stay disabled until the shared protected-execution
lifecycle supplies current session/generation/recipe/policy/proof-bound readiness, including
storage bounds, authenticated receipts and cleanup proof. The internal readiness adapter defaults
to unsupported; owner environment settings and executable/bubblewrap preflight cannot establish
this readiness. Inventory and delivery admission re-read the adapter, so missing or invalidated
readiness withholds the capability and refuses required deliveries before checkout. The
service also needs to positively enroll the runner in the workspace's approved pool and match the
delivery's pinned environment and repository. The capability alone does not attest to deployer
egress control or authorize a delivery. [Dedicated Node verifier setup](node-verifier.md) describes
the required runner settings and the supported recipe.

The verifier fetches both pinned full SHA-1 commit IDs from the approved HTTPS remote and requires
the base to be an ancestor of the input revision. If anonymous fetch fails, it may use the
existing repository credential endpoint for the held delivery and exact approved remote with
`canPush: false`. The service must honor the read-only request for verified editing deliveries
as well as reviews. The runner uses this credential only for the pinned Git fetch. It accepts a
single repository without a session resume. An optional input bundle does not establish provenance
or supply objects to the verifier. A review (`task.review` or a dot-delimited subtype) checks the
pinned input. An editing run starts on `mbus-verified` with dependencies restored using the fixed
recipe. It checks the committed descendant on `mbus-verified` in a fresh checkout after the agent
exits. The runner serves the agent's settle reply but owns code
return for verified tasks. Its reply attaches the exact checked commit and the runner settlement
includes the corresponding `git.commit` ref. The runner cannot report a coding pass before that
reply has been accepted. Neither the send contract nor the shape of the verification snapshot changes.

Only authenticated runner settlement at `POST /api/runner/settle` may report
`RunnerSettle.verification`, accompanied by `verificationAttempt` and `verificationDigest`.
The service checks these against the current lease, pinned definition and required immutable
revision. The agent's MCP `settle` has no such field. The verification
result shape is `{state, environment, reason?, revision?, checks?}`, with `checks` entries
`{name, state, reason?}`. State values are `pending`, `blocked`, `failed` and `passed`. A
pass requires all requested checks to have run successfully at the required revision. The Node
runner uses fixed reason codes when present: `revision-unverified`, `dependency-policy-changed`,
`toolchain-missing`, `restore-failed`, `check-failed`, `check-unavailable`, `timed-out`, and `cancelled`.
The dependency policy code indicates that a supported manifest, lockfile, or Git attributes file
differs from the approved base. Process output does not appear in these fields.

The service exposes this result as optional `verification` on full thread summaries and compact
status tasks. Omission represents legacy work. Optional `currentFrictionKinds` is authoritative
when present, including an empty array. Existing `frictionKinds` continues to represent history.
