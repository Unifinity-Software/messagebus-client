# Azure Container Apps

Back to the [runner image guide](../README.md).


Use [the ACA template](../../../infra/runner.bicep) for one outbound-only runner replica in a Consumption
environment. Keep the existing Key Vault and any private ACR in the deployment resource group. Use
a vault configured for Azure RBAC. The template assigns its user-assigned Azure identity Key Vault
Secrets User and, for a private ACR, AcrPull. Use the server URL shown by **Add runner** and make
sure the app can reach it, the attachment store, and the endpoints used by the chosen agent and
repository.

1. Create a **Sign in runners** API key. Put it in Key Vault as `mbus-api-key`. Add a secret for the
   agent you intend to run. Keep secret values in the vault.
2. Build the runner from a release supported by your server. Pin the agent CLI build arguments
   in [Custom images](custom-image.md#build), smoke-check the selected agent with `--version`, and publish the image to your
   registry. Use the resulting digest as `runnerImage` for staging.
3. Supply `runnerImage`, `serverUrl`, `keyVaultName`, and a unique `runnerName` in the deployment
   parameters. Set `containerRegistryName` for private ACR. The `secrets` array maps environment
   names to vault secret names. For example, a Claude Code deployment could use
   `[{"env":"MBUS_API_KEY","secret":"mbus-api-key"},{"env":"ANTHROPIC_API_KEY","secret":"anthropic-api-key"}]`.
4. Compile the template with `az bicep build --file infra/runner.bicep --stdout`. For an approved
   staging deployment, use `az deployment group create --resource-group <group> --template-file infra/runner.bicep --parameters @runner.parameters.json`
   with a parameter file containing vault secret names only. Use the checklist below to verify it.

## Identity and isolation

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

## Opt-in staging checklist

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
