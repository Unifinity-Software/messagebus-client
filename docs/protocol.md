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

### Frozen isolated machine domain (v2 core; runtime integration pending)

`MachineRequestSigningV2` is a separate additive builder. The existing `MachineRequestSigning.CanonicalString`
and v1 wire remain unchanged. This core does not activate isolated signing, persist a binding ledger, or gate
credential transports. It makes no regional availability or release-readiness claim.

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
refresh, expiry and failure. These persistence/locking and transport gates remain subsequent implementation work.

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

## REST routes the client calls

`ApiRoutes` in `src/MessageBus.Contracts/Http/Http.cs` lists them. The client uses: enrollment and the machine's own
record (`/api/machines/*`), its inventory of agents and tools (`PUT /api/machines/inventory`), public keys and key
grants (`/api/w/{workspaceId}/keys*`), attachment links (`/api/w/{workspaceId}/attachments/*`,
`/api/w/{workspaceId}/deliveries/{deliveryId}/attachment-links`) and the MCP endpoint `/mcp`.

## MCP tools

`src/MessageBus.Contracts/Mcp/McpTools.cs` (`McpToolNames`) names the tools, and `McpToolCatalog.cs` gives each one
its description and input schema. The service's remote `/mcp` endpoint and the local `mbus mcp` server show the same
catalog with the same input and result types:

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
