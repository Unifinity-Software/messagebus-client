# Runner image

A container image that runs `mbus runner`: it connects out to a messagebus.ai server and starts a
coding agent for each task it is given. It contains `mbus` and four agent CLIs, Claude Code
(`claude`), Codex (`codex`), OpenCode (`opencode`) and the GitHub Copilot CLI (`copilot`), plus git.
It opens no port.

## Build

Build your image from released source or from a development checkout. The project
does not supply a ready-made runner image. To build from a release, start at
the repository root and use the release version in both the checkout and image tag.

```sh
version='<version>'
git checkout "mbus-v$version"
```

For a development build, use your development checkout and set `version` to
a development image tag.

```sh
podman build --ignorefile docker/runner/Dockerfile.dockerignore -f docker/runner/Dockerfile -t "messagebus-runner:$version" .
docker build -f docker/runner/Dockerfile -t "messagebus-runner:$version" .
```

The agent CLIs are installed at their newest version unless you pin them:
`--build-arg CLAUDE_CODE_VERSION=… --build-arg CODEX_VERSION=… --build-arg OPENCODE_VERSION=… --build-arg COPILOT_VERSION=…`.
Rebuild the image to update them; they don't update themselves inside it.

## Configure

Everything is an environment variable. Secrets can also come from your secret store as files (below).

| Variable | What it's for |
|---|---|
| `MBUS_SERVER` | The server URL, e.g. `https://app.messagebus.ai` (the address **Add runner** shows). |
| `MBUS_API_KEY` | An API key that signs in runners (below). Secret. |
| `MBUS_MACHINE_NAME` | How the dashboard names this machine. |
| `MBUS_LOCATION` | The machine's Location label in the dashboard. |
| `ANTHROPIC_API_KEY` or `CLAUDE_CODE_OAUTH_TOKEN` | Claude Code (the token comes from `claude setup-token`). Secret. |
| `OPENAI_API_KEY` | Codex. Secret. |
| The key for OpenCode's provider | OpenCode, for example `ANTHROPIC_API_KEY` or `OPENAI_API_KEY`. Secret. |
| `GH_TOKEN` | GitHub Copilot CLI: a fine-grained token with the "Copilot Requests" permission. Secret. |

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

## Azure Container Instances

`infra/runner-aci.bicep` provisions a Linux ACI runner that enrolls itself in Azure. You do not
run an enrollment container on your computer or copy a machine private key into a deployment.
Use a runner image that includes `docker/runner/aci-bootstrap.mjs` and the pending-enrollment
checkpoint support. The image supplied for deployment must include this change; an older image
built from `mbus-v1.1.0` does not contain this bootstrap. Official GHCR distribution is tracked
separately in issue #17.

### Deploy in Azure

Choose a distinct container group name for each runner. Deploy the template through Azure Cloud
Shell or your deployment pipeline, using an image already in your registry. The default is one
mbus task slot, two CPU cores, four GiB of memory and no public ingress. Agents run as UID 10001.

| Parameter | Value |
|---|---|
| `runnerName` | Your ACI container group name. |
| `runnerImage` | The supplied image tag or, preferably, its pinned digest. |
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
**Tasks at once** is one for the initial deployment. Run a Claude Code task and a Codex task,
then stop and start the ACI group and repeat them. The machine ID and role assignments must stay
the same.

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
