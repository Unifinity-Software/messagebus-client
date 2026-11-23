# Azure Container Instances

Back to the [runner image guide](../README.md).


The Linux ACI runner requires a protected launch under UID 10001. It enrolls or
restores its identity before announcing readiness. A startup check uses dummy
files and a local test endpoint to verify the same task launcher used for real
work. If the container denies the required namespace operations, the runner
stays connected and reports its harnesses as unavailable. It won't run tasks
with basic isolation. Confirm that the target deployment passes the check
before assigning roles. When the kernel denies a fresh procfs mount, the official
amd64 image uses its checksum-pinned gVisor userspace kernel for Codex deliveries (release-20261116.0,
systrap platform). This provides dynamic, private `/proc` without mounting the
enclosing container's procfs into a task. Codex checkout, setup, agent and tools
use this protected launcher. Other agents keep the existing protected native launcher.
The runtime needs no KVM, privileged container
or additional host capabilities. Its Apache 2.0 license ships in the image.

- **Files and processes.** A task gets its own home, worktree, temporary
  directories and PID namespace. The runner's settings, machine key and other
  worktrees aren't mounted. The image's toolchain is read-only. Codex read-only roles
  get a read-only worktree mount, including its Git metadata. Only the runner's
  initial checkout has a separate, trusted write phase.
- **Network.** A task has a separate network namespace. Its proxy allows
  approved public IPv4 connections on HTTPS or SSH ports. The proxy denies
  private, loopback, link-local and Azure platform addresses, and the runner's
  identity storage host. Open-network roles still use this allow-list. Upstream
  proxies and host-local models aren't supported in this mode.
- **Harnesses.** Claude Code and Codex start with a fresh home. Use credentials
  supplied by the deployment, no saved sign-ins or tools from the runner's
  home. A harness that fails its protected launch check is unavailable. The
  userspace launcher enforces Codex's file profile externally; readiness checks
  a real Codex command against an offline Responses fixture for both writable
  and read-only profiles. The check has no provider credentials or model bill.
  Custom images without the bundled runtime retain the protected empty-proc
  profile and cannot offer harnesses that need dynamic procfs. Resumed harness
  sessions aren't supported in this mode.
  Existing ACI deployments need the new released runner image: updating only
  the `mbus` binary inside an older image does not install the runtime and helper.
  Keep the existing runner name and identity storage when replacing its image.
- **Cleanup.** Private runtime journals remain outside guest mounts. Before a
  run releases capacity, the runner ends its exact owned sandbox IDs and verifies
  their recorded Gofer and Sentry process identities have exited. Uncertain
  cleanup quarantines the runner and preserves the journal and worktree. Startup
  recovers only this runner's owned journals before accepting new tasks.

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

The template defaults to the official `1.5.2` image. Choose a verified
digest pin to deploy the exact release image.

## Deploy in Azure

Choose a distinct container group name for each runner. Deploy the template through Azure Cloud
Shell or your deployment pipeline. The default is one
mbus task slot, two CPU cores, four GiB of memory and no public ingress. Agents run as UID 10001.

| Parameter | Value |
|---|---|
| `runnerName` | Your ACI container group name. |
| `runnerImage` | Defaults to the official `1.5.2` tag. Set a verified digest to pin the exact image, or supply a custom image. |
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
Save the following as `runner.parameters.json`. Replace the Key Vault resource ID,
secret names, server URL and names with your deployment's values. For an unused
provider, use `{"value": ""}` for its parameter. Use the same resource group and
runner name when upgrading so the template reuses its identity storage.

```json
{
  "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
  "contentVersion": "1.0.0.0",
  "parameters": {
    "runnerName": {"value": "mbus-aci-runner"},
    "machineName": {"value": "mbus-aci-runner"},
    "runnerImage": {"value": "ghcr.io/unifinity-software/messagebus-runner:1.5.2"},
    "containerRegistryName": {"value": ""},
    "serverUrl": {"value": "https://app.messagebus.ai"},
    "runnerApiKey": {
      "reference": {
        "keyVault": {"id": "<key-vault-resource-id>"},
        "secretName": "mbus-runner-enrollment"
      }
    },
    "anthropicApiKey": {
      "reference": {
        "keyVault": {"id": "<key-vault-resource-id>"},
        "secretName": "anthropic-api-key"
      }
    },
    "openaiApiKey": {
      "reference": {
        "keyVault": {"id": "<key-vault-resource-id>"},
        "secretName": "openai-api-key"
      }
    }
  }
}
```

The template also accepts `location` (defaults to the resource group region),
`cpu` (defaults to 2) and `memoryInGB` (defaults to 4).
The public image uses an empty `containerRegistryName` and requires no ACR.

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

## First start and later starts

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

## Recover or retire

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
