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

`infra/runner-aci.bicep` provisions one Linux runner with outbound connections, a private-registry
pull identity, Claude Code and Codex API keys, and a stable mbus machine identity. Use a dedicated
resource group and an image built from the release used by your server. Pin all four CLI versions
with the build arguments above and build for `linux/amd64`.

The template sets `MBUS_DEPLOYMENT_TARGET=aci` and `MBUS_AZURE_RESOURCE_ID` to the container group's
ARM ID. A CLI release supporting ACI metadata reports that setup hint to Fleet; older releases still run
but appear as Docker. Metadata never enables management: approve the shared runner, enable Azure runners
in Integrations, verify its Azure pairing tag in runner Settings, then explicitly enable wake and idle stop.

### Enroll the identity

Enroll once using the release image and a dedicated home volume. Create a **Sign in runners** API
key in your workspace's **Security** page. Save its value in a private file and run these commands
in a shell with Podman. Choose distinct container, volume and secret names for each runner.

```sh
podman secret create aci-enrollment ./runner-api-key.txt
podman run --name aci-enrollment \
  --secret aci-enrollment,target=MBUS_API_KEY \
  -e MBUS_SERVER=https://app.messagebus.ai \
  -e MBUS_MACHINE_NAME=aci-runner-1 -e MBUS_LOCATION="Azure Container Instances" \
  -v aci-runner-home:/home/runner messagebus-runner:<version> login
```

Confirm that login reports the intended workspace and that the dashboard shows the new shared
runner. Approve the machine if required, choose its roles, and set **Tasks at once** to one for
the initial deployment. The enrollment container only runs the login command.

Copy the identity into a private directory and store both files in your secret store. On Linux
or macOS, create that directory with `umask 077` first.

```sh
mkdir private-runner-state
podman cp aci-enrollment:/home/runner/.mbus/config.json private-runner-state/config.json
podman cp aci-enrollment:/home/runner/.mbus/machine-key.pem private-runner-state/machine-key.pem
az keyvault secret set --vault-name <vault> --name aci-runner-config \
  --file private-runner-state/config.json --output none
az keyvault secret set --vault-name <vault> --name aci-runner-key \
  --file private-runner-state/machine-key.pem --output none
```

Keep each container group's identity distinct. These files identify the enrolled machine, so its
role assignments survive restarts. The template restores the same files into local storage as
UID 10001 with directory mode `700` and file mode `600`. Working copies and per-run state are
ephemeral. Azure Files SMB mounts expose synthetic Unix modes, so the identity uses an ACI secret
volume. The release image's `USER runner` supplies the non-root UID.

### Deploy and verify

Push the image to an existing ACR in the resource group. The template creates a user-assigned
managed identity with `AcrPull` on that registry. Supply the following parameters through your
deployment system's secure parameters, or a JSON parameter file with Key Vault references.

| Parameter | Value |
|---|---|
| `runnerName` | Your container group name. |
| `runnerImage` | `<registry>.azurecr.io/messagebus-runner:<version>` or its digest. |
| `containerRegistryName` | Existing registry name in the resource group. |
| `machineConfig` | The enrolled runner's `config.json`, stored as a secure string. |
| `machinePrivateKey` | Its `machine-key.pem`, stored as a secure string. |
| `anthropicApiKey` | Claude Code API key, stored as a secure string. |
| `openaiApiKey` | Codex API key, stored as a secure string. |

For each secret parameter, use a reference in your parameter file. The vault must allow template
deployment and the deployer needs `Microsoft.KeyVault/vaults/deploy/action` on it.
[Microsoft documents the required permissions and reference format](https://learn.microsoft.com/en-us/azure/azure-resource-manager/templates/key-vault-parameter).

```json
"machinePrivateKey": {
  "reference": {
    "keyVault": { "id": "<key-vault-resource-id>" },
    "secretName": "aci-runner-key"
  }
}
```

```sh
az deployment group create --subscription <subscription> --resource-group <group> \
  --template-file infra/runner-aci.bicep --parameters @runner.parameters.json
az container show --subscription <subscription> --resource-group <group> \
  --name <runner> --query instanceView.state
```

Check the dashboard for that machine, run a task with Claude Code and another with Codex, and
confirm both return their results. Once both tasks have finished, stop and start the group with
`az container stop` and `az container start`. Check that its machine ID and role assignments
are the same and repeat the tasks. Review container logs for credential exposure before sharing
them. The template outputs the ARM resource ID for the workspace's ACI binding.

### Retire a runner

Drain the runner and wait for its active tasks to finish before stopping it. A stopped ACI group
retains its deployment configuration and secret volumes. Registry storage and other provisioned
services continue to incur their own charges.
[ACI lifecycle and billing](https://learn.microsoft.com/en-us/azure/container-instances/container-instances-stop-start).

To retire it, remove the machine from mbus, delete the container group and its pull identity and
role assignment, and remove its identity secrets from your secret store. If the resource group
was dedicated to this runner, deleting that group removes its Azure resources together. After
the identity has been saved and deployed, remove the enrollment container, its dedicated home
volume, the Podman enrollment secret and the temporary identity files. Revoke the enrollment API
key when it is no longer needed to enroll other runners.
