# Run a runner in Azure Container Instances

Back to the [runner image guide](../README.md).

One template, `infra/runner-aci.bicep`, runs the public runner image in Azure. On its first start the runner signs
itself in to your workspace and keeps its identity in a private Azure storage account, so restarts and new images
keep the same machine on **Fleet**. Nothing runs on your computer.

## What you need

- An Azure resource group you can deploy to.
- A Key Vault that allows template deployment, with these secrets. You need `Microsoft.KeyVault/vaults/deploy/action`
  on the vault ([how Key Vault references work](https://learn.microsoft.com/en-us/azure/azure-resource-manager/templates/key-vault-parameter)).

| Secret | What it is | Where to get it |
| --- | --- | --- |
| Sign-in key | Lets the runner join your workspace, once | In the dashboard, **Security**, **New API key**, then **Sign in runners** |
| Anthropic API key **or** Claude subscription token | Claude Code. The token bills your Claude subscription instead of an API account. | An API key from the [Claude Console](https://console.anthropic.com/settings/keys), or run `claude setup-token` on your computer and copy the token it prints |
| OpenAI API key | Codex | [OpenAI platform](https://platform.openai.com/api-keys) |

For an agent you don't use, leave its key out (Codex: pass an empty value).

## 1. Save the parameters

Save this as `runner.parameters.json`, with your vault's resource ID and secret names. Give each runner its own
`runnerName`.

```json
{
  "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#",
  "contentVersion": "1.0.0.0",
  "parameters": {
    "runnerName": {"value": "mbus-aci-runner"},
    "runnerApiKey": {"reference": {"keyVault": {"id": "<key-vault-resource-id>"}, "secretName": "mbus-runner-sign-in"}},
    "anthropicApiKey": {"reference": {"keyVault": {"id": "<key-vault-resource-id>"}, "secretName": "anthropic-api-key"}},
    "openaiApiKey": {"reference": {"keyVault": {"id": "<key-vault-resource-id>"}, "secretName": "openai-api-key"}}
  }
}
```

To use a Claude subscription instead, replace the `anthropicApiKey` line with `claudeCodeOauthToken`, pointing at the
secret that holds your `claude setup-token` token.

Never put key values in the file or on a command line.

Optional parameters:

| Parameter | Default |
| --- | --- |
| `runnerImage` | `ghcr.io/unifinity-software/messagebus-runner:1.5.2`. To pin the exact image, use the **Digest pin** from the release notes. |
| `machineName` | The `runnerName`. This is the name shown on Fleet. |
| `cpu`, `memoryInGB` | 2 cores, 4 GiB |
| `location` | The resource group's region |
| `serverUrl` | `https://app.messagebus.ai` |
| `containerRegistryName` | Empty. Set it only for your own image in a private Azure Container Registry in the same resource group. |

## 2. Deploy

In Azure Cloud Shell, or your pipeline:

```sh
az deployment group create --resource-group <group> \
  --template-file infra/runner-aci.bicep --parameters @runner.parameters.json
```

The template creates:
- the container group, with no public address
- a managed identity
- a private storage account for the runner's identity, with no anonymous or shared-key access

The identity can read and write only that storage, and pull from your registry if you set one.

## 3. Approve it

1. On **Fleet**, approve the new machine if your workspace asks for approval, then assign its roles.
2. Keep **Tasks at once** at 1 to start with.
3. Redeploy with `runnerApiKey` empty, and revoke the sign-in key once no other runner needs it. The runner signs in
   with its own identity from now on. Keep the Claude and OpenAI keys.

## Check it works

1. Send a small Claude Code task and a small Codex task to this runner. For Codex, a read-only request such as
   "Reply with `codex-ready`" with a five-minute time limit is enough.
2. Stop and start the container group in Azure, and send both again.
3. On **Fleet**, the machine and its roles should be the same as before.

## Update

Redeploy with the new image tag or digest, using the same `runnerName` and resource group. Keep the storage account:
it holds the runner's identity. Updating `mbus` inside an old image isn't enough, because the image also carries the
runtime that keeps tasks apart.

## Let messagebus.ai start and stop it

This is optional. In messagebus.ai:

1. Turn on Azure runners in **Integrations**.
2. In the machine's settings on **Fleet**, add the pairing tag it shows to the container group, and verify it.
3. Turn on **Wake for eligible queued work** and **Stop when idle**.

The runner reporting its Azure address never grants control by itself.

## If something is wrong

| What you see | What to do |
| --- | --- |
| Claude Code or Codex shows as unavailable | The container didn't allow the isolation the runner needs. The runner won't run tasks without it. Check you deployed the public image or one built from it. |
| OpenAI says the key is missing or wrong | Check the secret in Key Vault, then restart the container group. |
| OpenAI refuses Codex because of the organization's API Credits for ChatGPT subscriptions | Ask the organization owner for an API project that allows Codex, then retry. |
| The runner never starts work | It can't read its identity storage, or another copy holds it. Restore access to the storage account, or stop the other container group, then restart. A first sign-in that was interrupted needs the original sign-in key. |

An identity belongs to one container group and one server. It can't be moved to another.

## Remove it

1. Let running tasks finish.
2. On **Fleet**, remove the machine.
3. In Azure, delete the container group, its managed identity and its storage account. If you used a resource group
   just for this runner, delete that instead.
4. Delete the model keys from Key Vault if nothing else uses them.

A stopped container group costs nothing for compute. Storage and model use are billed separately
([stop and start in Azure](https://learn.microsoft.com/en-us/azure/container-instances/container-instances-stop-start)).

## How tasks are kept apart

The runner checks this at startup, and won't offer an agent whose check fails.

- **Files.** Each task gets its own home, working copy and temporary folders. The runner's settings, machine key and
  other tasks' copies aren't visible. Tools in the image are read-only. Read-only roles get a read-only copy.
- **Processes.** Each task runs in its own process space. On Azure, Codex runs inside a user-space kernel bundled in the image
  ([gVisor](https://gvisor.dev), Apache 2.0). It needs no privileged container.
- **Network.** Each task goes through a proxy that allows only public HTTPS and SSH addresses the role permits. It blocks private, local and
  Azure platform addresses, and the runner's identity storage. An upstream proxy or a model on the host isn't
  supported.
- **Sign-ins.** Agents start with a fresh home and only the keys you deployed. Resuming an agent's earlier session
  isn't supported.
- **Clean-up.** The runner confirms a task's processes have ended before taking the next one. If it can't confirm
  that, it stops taking work and keeps the evidence.
