# Dedicated Node verification runner

Runtime enrollment and capability advertising are disabled in this candidate. The shared
protected-execution lifecycle (#496) must first supply session/generation/recipe/policy/proof-bound
readiness, bounded storage, authenticated receipts and verified cleanup. The internal readiness
adapter has no production implementation and defaults to unsupported. The provisioning settings
below are recipe inputs for that integration; setting them and passing executable preflight do
not enable verification. No personal runner should be enrolled from this candidate.

Provision a disposable Linux runner as an unprivileged user for one approved repository. The
deployer enforces egress restrictions outside mbus. Allow the approved Git HTTPS origin and
`registry.npmjs.org` for checkout and restore, plus the application and model endpoints needed by
the runner and its agent. Restrict other destinations, including local services and metadata
endpoints. The runner's proxy is advisory on Linux. Final checks use a separate network namespace.

Install administrator-owned Git, Node, npm and non-setuid bubblewrap at `/usr/bin/git`, `/usr/bin/node`,
`/usr/bin/npm` and `/usr/bin/bwrap`. Git must support `ls-tree --format`. Provide the system trust
bundle at `/etc/ssl/certs/ca-certificates.crt`, direct DNS resolution from the runner, and working
unprivileged user, PID, IPC, UTS, mount and network namespaces. The system toolchain and libraries
mounted into the verifier must be administrator-controlled and free of credentials. Provision
resource limits for the dedicated runtime.

The sandbox preflight requires bubblewrap to disable further user namespaces inside the verifier.
OCI hosts that mount `/proc/sys` read-only can prevent that operation. Use a dedicated runtime
that supports the namespace controls and confirm the verifier preflight before enrolling it.

Set the following values in the runner process environment.

| Owner setting | Value |
| --- | --- |
| `MBUS_RUNNER_VERIFICATION` | `node-npm-ci-v1` |
| `MBUS_RUNNER_VERIFICATION_REMOTE` | The exact approved HTTPS Git URL from the service snapshot, with no credentials or redirects |
| `MBUS_RUNNER_ISOLATION` | `os` (the default) |
| `MBUS_KEY_STORE` | `file` (the default on Linux) |

Use the normal credential-scoped run tools and runner bundle return. Once the shared lifecycle
is integrated, advertising `verification:node-npm-ci-v1` also requires current shared readiness,
the Linux agent sandbox with nesting and the separate verifier preflight. Inventory and delivery
admission re-read readiness; expiry, revocation or cleanup failure must invalidate it. Shared
per-delivery acknowledgment and child-launch enforcement remain mandatory. After that integration
and acceptance proof, enroll the machine in the service's approved verification pool and assign its
Node and npm versions to the environment. A dedicated verifier accepts only required Node
verification tasks. The recorded OCI digest identifies the operator-approved image provenance.
The runner checks that namespace preflight succeeds and that the installed Node and npm
versions match the delivery's pins.

## Supported checkout and checks

For a private repository, configure the workspace integration to issue a read-only credential
for the approved repository on the verified delivery. The runner tries the pinned fetch
anonymously first and confines the credential to the retry.

The first recipe supports one Node project, at the repository root or at `src/client`, with
`package.json` and version 3 `package-lock.json`. The manifest, lockfile and Git attributes must
match the approved base. The runner reports `dependency-policy-changed` when those files
differ, including edits to an approved lockfile. Dependency updates require a separate
policy decision and an updated approved base. The recipe rejects symlinks, submodules, npm
workspaces and overrides, repository npm configuration, shrinkwrap files, checked-in
`node_modules`, and non-registry dependency specs. Lockfile dependency entries must use HTTPS tarballs from the
public npm registry with SHA-512 integrity. The recipe supports the named `test`, `typecheck`
and `build` npm scripts.

Restore uses `npm ci --include=dev --ignore-scripts --prefer-offline` against the public registry
with fresh configuration. The runner prepares the verified checkout and restores dependencies
before the coding or review agent starts. After it exits, each check gets its own full committed tree and restore,
followed by its named script in the project directory with lifecycle hooks disabled. Restores
in the same attempt reuse a disk cache and npm checks tarball integrity against the approved
lockfile. The runner mounts the cache only for restores and cleans it up with the runner-owned
verification workspace during normal run cleanup. The runner discards check output. Git fetches
are limited to three minutes and npm commands to five minutes each, within the role time limit.

Read-only `task.review` tasks and their subtypes verify the input SHA. For an editing task, commit
to the checked-out `mbus-verified` branch and settle with your answer. The runner verifies and returns that exact
committed descendant as a bundle. Working-tree edits are outside the verification result. The
runner reports setup or return problems as blocked and an unsuccessful required script as failed.
