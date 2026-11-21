# Runner image

A container image that runs `mbus runner`: it connects out to a messagebus.ai server and starts a
coding agent for each task it is given. It contains `mbus` and four agent CLIs, Claude Code
(`claude`), Codex (`codex`), OpenCode (`opencode`) and the GitHub Copilot CLI (`copilot`), plus git.
It opens no port.

## Build

Copy the public Linux amd64 image reference from the
[client release notes](https://github.com/Unifinity-Software/messagebus-client/releases).
The digest identifies the exact image. To build your own from a release, start
at the repository root and use the release version in both the checkout and image tag.

```sh
version='<version>'
git checkout "mbus-v$version"
revision=$(git rev-parse HEAD)
docker build -f docker/runner/Dockerfile -t "messagebus-runner:$version" \
  --build-arg MBUS_RELEASE_VERSION="$version" --build-arg MBUS_SOURCE_REVISION="$revision" .
```

For a development build, use your development checkout and set `version` to
a development image tag. Omit `MBUS_RELEASE_VERSION` to keep the `+dev` version.
You can pass `MBUS_SOURCE_REVISION` on its own for a development image.

```sh
podman build --ignorefile docker/runner/Dockerfile.dockerignore -f docker/runner/Dockerfile -t "messagebus-runner:$version" .
docker build -f docker/runner/Dockerfile -t "messagebus-runner:$version" .
```

The Dockerfile pins the agent CLI versions. Override a pin with its build argument:
`--build-arg CLAUDE_CODE_VERSION=… --build-arg CODEX_VERSION=… --build-arg OPENCODE_VERSION=… --build-arg COPILOT_VERSION=…`.
A release image records its CLI version and full source commit in OCI labels. Use
`mbus --version` for the CLI version and `mbus doctor` for the full source revision
and update policy. The agent CLIs have their own versions.

To update mbus or the agent CLIs, rebuild or obtain a replacement image and
redeploy the runner with it. Image-managed mbus refuses `mbus update`,
`mbus update --check`, and remote in-place updates.

## Configure

Everything is an environment variable. Secrets can also come from your secret store as files (below).

| Variable | What it's for |
|---|---|
| `MBUS_SERVER` | The server URL, e.g. `https://app.messagebus.ai` (the address **Add runner** shows). |
| `MBUS_API_KEY` | An API key that signs in runners (below). Secret. |
| `MBUS_MACHINE_NAME` | How the dashboard names this machine. |
| `MBUS_LOCATION` | The machine's Location label in the dashboard. |
| `ANTHROPIC_API_KEY` or `CLAUDE_CODE_OAUTH_TOKEN` | Claude Code (the token comes from `claude setup-token`). Secret. |
| `CODEX_API_KEY` | Codex API key. Secret. Takes precedence over `OPENAI_API_KEY`. |
| `OPENAI_API_KEY` | Fallback Codex API key. Secret. The runner passes it to Codex as `CODEX_API_KEY`. |
| The key for OpenCode's provider | OpenCode, for example `ANTHROPIC_API_KEY` or `OPENAI_API_KEY`. Secret. |
| `COPILOT_GITHUB_TOKEN` | GitHub Copilot CLI: a fine-grained token with the "Copilot Requests" permission. Secret. |

Only the agents that have their key can run tasks; leave out the ones you don't use.

### The API key

In the dashboard, open **Security**, choose **New API key** and pick **Sign in runners**. That key
can do nothing but add runners, and one key can add as many as you start. On its first start a
container makes its own machine key, signs in with the API key and shows up on **Runners and
agents** as a shared runner named `MBUS_MACHINE_NAME` (containers running at the same time with
the same name get `-2`, `-3` added). From then on it signs in as that machine and doesn't use the
API key again. Keep `/home/runner` on a volume; a container without it (or that lost it) makes a new
machine key and takes over the runner of its name once that one has been offline for 5 minutes.

Revoking the key stops new runners signing in with it; runners it already added keep working until
you remove them (the revoke dialog can do both at once). Every runner the key adds is in the audit
log. A one-time token (`MBUS_ENROLL_TOKEN`) still works instead of the API key.

### Secrets from your secret store

The entry point fills in each variable that isn't already set from, in order:

1. `NAME_FILE=/path/to/file`: the file holds the value of `NAME`.
2. One file per secret in `/run/secrets` (change it with `MBUS_SECRETS_DIR`). The file name is the
   variable name; lower case and dashes work too, so `anthropic-api-key` becomes `ANTHROPIC_API_KEY`.

That covers the usual ways a secret store reaches a container:

- **Azure Container Apps:** Key Vault references become plain environment variables;
  `infra/runner.bicep` sets this up.
- **Kubernetes:** mount a Secret, or Key Vault through the Secrets Store CSI driver, at `/run/secrets`.
- **Docker and Podman:** `secrets:` in `compose.yaml` (an example is next to this file), or
  `podman secret create` and `--secret`.

## Run

Set `image: messagebus-runner:<version>` in your copy of the Compose example.
For Azure Container Apps use `<registry>.azurecr.io/messagebus-runner:<version>`.
For Kubernetes use `<registry>/messagebus-runner:<version>`.
Push the image you built to the registry before deploying to Azure or Kubernetes.

```sh
podman run -d --name messagebus-runner \
  -e MBUS_SERVER=https://app.messagebus.ai -e MBUS_LOCATION="Build server" \
  --secret mbus_api_key --secret anthropic_api_key \
  -v messagebus-runner-home:/home/runner "messagebus-runner:$version"
```

Agent sign-in state, mbus settings (with the runner's machine key) and checked-out code live in
`/home/runner`; keep it on a volume so a restart doesn't lose them. Any other command runs as given, which helps when checking an image:
`podman run --rm "messagebus-runner:$version" claude --version`.

A container can't reach a server that listens on localhost only. To point a runner container at a
server on the same computer, make that server reachable on a network address (or through a tunnel)
and use that address as `MBUS_SERVER`.

## Azure Container Apps

Use [the ACA template](../../infra/runner.bicep) for one outbound-only runner replica in a Consumption
environment. Keep the existing Key Vault and any private ACR in the deployment resource group. Use
a vault configured for Azure RBAC. The template assigns its user-assigned Azure identity Key Vault
Secrets User and, for a private ACR, AcrPull. Use the server URL shown by **Add runner** and make
sure the app can reach it, the attachment store, and the endpoints used by the chosen agent and
repository.

1. Create a **Sign in runners** API key. Put it in Key Vault as `mbus-api-key`. Add a secret for the
   agent you intend to run. Keep secret values in the vault.
2. Build the runner from a release supported by your server. Pin the agent CLI build arguments
   listed above, smoke-check the selected agent with `--version`, and publish the image to your
   registry. Use the resulting digest as `runnerImage` for staging.
3. Supply `runnerImage`, `serverUrl`, `keyVaultName`, and a unique `runnerName` in the deployment
   parameters. Set `containerRegistryName` for private ACR. The `secrets` array maps environment
   names to vault secret names. For example, a Claude Code deployment could use
   `[{"env":"MBUS_API_KEY","secret":"mbus-api-key"},{"env":"ANTHROPIC_API_KEY","secret":"anthropic-api-key"}]`.
4. Compile the template with `az bicep build --file infra/runner.bicep --stdout`. For an approved
   staging deployment, use `az deployment group create --resource-group <group> --template-file infra/runner.bicep --parameters @runner.parameters.json`
   with a parameter file containing vault secret names only. Use the checklist below to verify it.

### Identity and isolation

The template leaves `/home/runner` on the container filesystem. A runner process that starts with
its previous settings and key reuses the same mbus machine identity. A restart or revision update
that replaces the container filesystem loses that key, agent state and working copies. The new
container enrolls with a fresh key using `MBUS_API_KEY`. Keep that key valid for replacements. If the
old machine was offline for less than five minutes when enrollment occurs, the new machine can
receive a suffixed name. After five minutes offline, a fresh enrollment can take over the old name.
Compare machine IDs to distinguish continuity from name reuse. Check approval and roles after
replacement. The Azure managed identity and the mbus machine identity serve different purposes.

Deployments that require identity continuity need separately configured durable storage writable
by UID 10001 that preserves the runner settings and machine key. Use one writer per identity and
verify storage permissions and restart behavior before relying on it. The supplied template uses
ephemeral storage.

The image runs as UID 10001. On Container Apps, check the runner's reported isolation
before adding work. If the host cannot start the standard Linux sandbox, basic isolation
gives each task its own worktree, temporary files, Git settings and filtered environment.
The container is then the OS boundary. Use a dedicated runner for workloads that can
share that boundary. Apply network controls outside the task process if you need to
enforce egress.

When configuring an upstream proxy for the runner, use `HTTP_PROXY`, `HTTPS_PROXY`, `ALL_PROXY`
and `NO_PROXY` as applicable. The run proxy supports an `http://` upstream, including for CONNECT
to HTTPS destinations. Unsupported upstream schemes can cause direct connections. Anonymous
server discovery and attachment transfers require a route that works without proxy credentials.
Verify server HTTPS and WebSocket connectivity through the configured route.

The runtime image has Node.js, Git, SSH and the agent CLIs. Add any repository-specific SDKs or
build tools to your own image and smoke-check them as the runner user. The .NET SDK used to build
the image is in a separate build stage.

For the GitHub App mode, enable the workspace integration on the task repository and grant the
task role push access if it needs to push. The runner supplies a short-lived, repository-scoped
credential through the run's Git credential helper. Leave `GH_TOKEN`, `GITHUB_TOKEN` and owner Git
credentials out of the deployment. The Copilot token authenticates the agent itself. For the
bundle mode, supply a Git bundle and have the task return a bundle with its commit.

### Opt-in staging checklist

Use a disposable workspace and two dummy repositories containing only a README. Run the checks
from another machine, targeting a role assigned to this staging runner alone. Record the image
digest, runner machine ID, task IDs and returned commit SHAs. Keep evidence free of credentials.

- Confirm the runner appears as shared with Location **Azure**. Approve it if required and confirm
  that the configured agent is signed in. Agents without credentials should take no tasks.
- Without the GitHub App, send a bundle with the first dummy repository and ask the agent to add a
  unique line to the README, commit, and return a bundle. Confirm that the runner leases and settles
  the task, the input bundle is downloaded and decrypted, and the sender can fetch and inspect the
  returned commit and README change. Check the trace and audit records for the run.
- With the GitHub App installed on the second dummy repository, send an equivalent repository task
  and ask the agent to push its commit to a disposable branch. Confirm the branch and commit on
  GitHub. In the task, report only whether `GH_TOKEN` and `GITHUB_TOKEN` are absent. Query the isolated
  Git helper for the fixture repository and for a different repository with prompting disabled.
  Check only credential presence in memory and report booleans. The fixture repository should get
  a credential and the other repository should get none.
- After the tasks settle, restart the ACA revision and send another dummy task. Record whether the
  machine ID survived. For the supplied template, expect a new ID when the replica filesystem is
  replaced. Record any suffix and offline interval and check whether a fresh enrollment after five
  minutes offline can reuse the name. With durable storage configured, require the same machine
  ID and roles after replacement.
- Review Container Apps startup and task console logs, plus the sender's reply and returned
  artifacts, for secret values. Use throwaway staging credentials and, if testing environment
  filtering, a dummy sentinel in a separate secret variable. Compare privately and record only
  pass or fail. Never dump the environment, enable Git credential tracing or print helper output.

## Azure Container Instances

The Linux ACI runner requires a protected launch under UID 10001. It enrolls or
restores its identity before announcing readiness. A startup check uses dummy
files and a local test endpoint to verify the same task launcher used for real
work. If the container denies the required namespace operations, the runner
stays connected and reports its harnesses as unavailable. It won't run tasks
with basic isolation. Confirm that the target deployment passes the check
before assigning roles. When the kernel denies a fresh procfs mount, the runner
repeats the checks with a private, empty, read-only `/proc` and the same mount,
PID and network namespaces. A standalone Node.js fixture in a disposable,
unprivileged ACI container verified this layout under UID 10001. It wrote its
own file and could not see dummy files or the loopback listener outside the
sandbox.

- **Files and processes.** A task gets its own home, worktree, temporary
  directories and PID namespace. The runner's settings, machine key and other
  worktrees aren't mounted. The image's toolchain is read-only.
- **Network.** A task has a separate network namespace. Its proxy allows
  approved public IPv4 connections on HTTPS or SSH ports. The proxy denies
  private, loopback, link-local and Azure platform addresses, and the runner's
  identity storage host. Open-network roles still use this allow-list. Upstream
  proxies and host-local models aren't supported in this mode.
- **Harnesses.** Claude Code and Codex start with a fresh home. Use credentials
  supplied by the deployment, no saved sign-ins or tools from the runner's
  home. A harness that fails its protected launch check is unavailable. Codex
  also has to start its nested command sandbox. In local offline checks with
  an empty `/proc`, Claude Code 2.1.330 and GitHub Copilot CLI 1.0.114 passed
  startup. Codex 0.171.0 failed its nested sandbox probe and OpenCode 1.19.6
  failed to start. Codex also failed to load its configuration before making
  any model request in a noninteractive test using the outer sandbox alone.
  The same test completed against a dummy Responses API through the relay
  with a fresh, namespace-local procfs. Codex stays unavailable on the
  empty-proc profile. Resumed harness sessions aren't supported in this mode.

`infra/runner-aci.bicep` provisions a Linux ACI runner that enrolls itself in Azure. You do not
run an enrollment container on your computer or copy a machine private key into a deployment.
Use a runner image that includes `docker/runner/aci-bootstrap.mjs` and the pending-enrollment
checkpoint support. Images built from `mbus-v1.1.0` predate this bootstrap.

For a release whose notes list a verified public Linux amd64 runner image, copy the
**Digest pin** from the notes into `runnerImage`. It has the form
`ghcr.io/unifinity-software/messagebus-runner@sha256:<digest>`. Leave
`containerRegistryName` empty. This path uses the published image directly. To
upgrade, select the digest from the newer release and redeploy with the same
runner name, resource group and identity storage. For a custom image, supply
its own tag or digest and set `containerRegistryName` for a private ACR.

The template defaults to the official `1.3.0` image. Use that default once the
release notes list a successful public image, or choose a verified digest pin.
You can supply a custom image before the first public release.

### Deploy in Azure

Choose a distinct container group name for each runner. Deploy the template through Azure Cloud
Shell or your deployment pipeline. The default is one
mbus task slot, two CPU cores, four GiB of memory and no public ingress. Agents run as UID 10001.

| Parameter | Value |
|---|---|
| `runnerName` | Your ACI container group name. |
| `runnerImage` | Defaults to the official `1.3.0` tag. Set a verified digest to pin the exact image, or supply a custom image. |
| `containerRegistryName` | Existing private ACR name in the resource group; empty for a public image. |
| `serverUrl` | Your reachable mbus server address. |
| `machineName` | How the runner appears in Fleet; defaults to the container group name. |
| `runnerApiKey` | A workspace API key with **Sign in runners**, supplied as a secure value. |
| `anthropicApiKey` | Claude Code API key, supplied as a secure value. |
| `openaiApiKey` | Codex API key, supplied as a secure value. |

Keep key values in your Azure secret/deployment configuration. Do not commit them to a parameter
file or place them on a command line. For a Key Vault reference, the vault must allow template
deployment and the deployer needs `Microsoft.KeyVault/vaults/deploy/action` on it.
[Required permissions and reference format](https://learn.microsoft.com/en-us/azure/azure-resource-manager/templates/key-vault-parameter).
For example, this parameter references the enrollment key without including its value:

```json
"runnerApiKey": {
  "reference": {
    "keyVault": { "id": "<key-vault-resource-id>" },
    "secretName": "mbus-runner-enrollment"
  }
}
```

In Azure Cloud Shell, with the template and a parameter file containing secret references:

```sh
az deployment group create --subscription <subscription> --resource-group <group> \
  --template-file infra/runner-aci.bicep --parameters @runner.parameters.json
```

The template creates a user-assigned managed identity and a private storage account with one
identity blob container. That identity gets Blob Data Contributor scoped to that container and,
when a private ACR is selected, AcrPull on that registry. The account disables anonymous blob
access and shared-key authentication. Identity data is encrypted at rest by Azure Storage.
The bootstrap retries initial permission propagation for a bounded period and fails closed if
it cannot access the identity store.

### First start and later starts

On first start the container obtains its Azure token through managed identity. It takes a blob
lease, generates a machine key, and saves a pending checkpoint before sending the mbus enrollment
request. It then uses the Sign in runners key to join your workspace and saves the completed
machine identity before starting the runner. An interrupted enrollment reuses the checkpointed
key, including when the service enrolled the machine but its response was lost.

The new shared machine appears on Fleet. Approve it if required, assign its roles and confirm
**Tasks at once** is one for the initial deployment. A credentials check (`mbus doctor`, runner
startup or a Fleet scan) establishes that Codex can be offered work while model access is still
unverified. When you can invoke mbus directly on the runner, `mbus doctor --smoke --agent codex`
runs a bounded model-backed edit, build, test and commit in a scratch checkout.

For ACI acceptance, run a small Claude Code task and a Codex task on this runner. For Codex, use a
single read-only request without a checkout, such as “Reply with `codex-ready`,” with a five-minute
role time limit. Verify the task succeeds, returns the requested reply and reports Codex output
tokens. Stop and start the ACI group and repeat both tasks. Verify the machine ID and role
assignments stayed the same. Keep key values out of task prompts, commands and logs.

If OpenAI reports missing authentication or rejects the key, check the configured secret and
restart the runner. If OpenAI refuses Codex because of the organization's monthly API Credits
benefit for ChatGPT subscriptions, ask the organization owner for a Codex-eligible sign-in or API
project, then retry the task. Model access is verified by a completed model request.

On later starts the container restores the same identity into owner-only local files. It does
not enroll again and does not need the enrollment API key. After the first successful enrollment,
remove `runnerApiKey` from your deployment's secure values or redeploy it as empty. Revoke that
key when you no longer need it to enroll other runners. Keep the agent API keys configured.
Working copies and per-task state are ephemeral; the identity blob is the durable state.

The template reports its ACI target and ARM resource ID as setup hints. To enable automatic
management, approve the shared runner, enable Azure runners in Integrations, verify the Azure
pairing tag in runner Settings, and explicitly enable wake and idle stop. Reported metadata
never authorizes Azure management. The template outputs the group ARM ID and identity storage
account name.

### Recover or retire

If storage access is revoked, the checkpoint is corrupt or another bootstrap owns the lease,
the runner does not begin work. Restore access or the correct identity checkpoint and restart.
A pending enrollment requires its original Sign in runners key. Preserve the storage account
and blob when updating or replacing the ACI group; deleting them loses the persisted identity.
An identity checkpoint cannot be used for a different ACI group or mbus server.

Drain active tasks before stopping or retiring the runner. A stopped ACI group retains its
deployment configuration; identity storage, registry storage and model usage are separate from
ACI compute charges.
[ACI lifecycle and billing](https://learn.microsoft.com/en-us/azure/container-instances/container-instances-stop-start).
To retire it, remove its mbus lifecycle binding and machine registration, delete the ACI group,
its managed identity and role assignments, and its dedicated identity storage. A dedicated
resource group can be deleted after the runner has drained. Retire deployment-held model keys
and revoke enrollment keys that are no longer needed.
