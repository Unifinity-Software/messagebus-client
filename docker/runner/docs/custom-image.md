# Custom images and private registries

Back to the [runner image guide](../README.md).


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

## Configure a custom image

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

Configure credentials or supported sign-in state for the agents you intend to run.

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
- **Docker and Podman:** a folder of secret files mounted at `/run/secrets`, as in [compose.yaml](../compose.yaml), or
  `podman secret create` and `--secret`.

## Run a custom image

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
