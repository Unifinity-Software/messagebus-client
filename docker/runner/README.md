# Runner image

The runner image runs `mbus runner` in a container. It connects out to messagebus.ai and starts a coding
agent (Claude Code, Codex, OpenCode or GitHub Copilot CLI) for each task it takes. It opens no ports.

```
ghcr.io/unifinity-software/messagebus-runner:1.5.2
```

The image is public, so no registry sign-in is needed. It is built for Linux amd64. Each
[release](https://github.com/Unifinity-Software/messagebus-client/releases) lists its image version and digest.

## 1. Get the keys

You need a key to add the runner to your workspace, and a key for each coding agent it should run.

| File name | What it is | Where to get it |
|---|---|---|
| `mbus_api_key` | Adds this runner to your workspace. | In the dashboard, **Security**, **New API key**, then **Sign in runners**. |
| `anthropic_api_key` | Claude Code, billed to an API account. | An API key from the [Claude Console](https://console.anthropic.com/settings/keys). |
| `claude_code_oauth_token` | Claude Code on your Claude subscription, instead of the API key. | Run `claude setup-token` on your computer and copy the token it prints. |
| `openai_api_key` | Codex. | An API key from the [OpenAI Platform](https://platform.openai.com/api-keys). |

Leave out the agents you don't use. An agent without a key takes no tasks. For OpenCode, GitHub Copilot CLI
and every other setting, see [All settings](docs/custom-image.md#configure-a-custom-image).

## 2. Start the runner

Make a folder with a `secrets` folder inside it. Put each key in its own file in `secrets`, named as in the
table, with only the key in it. Keep the folder out of source control.

On Linux, let the container's user (ID 10001) read the files. Docker Desktop on Windows and macOS doesn't
need this.

```sh
sudo chown -R 10001 secrets && chmod 700 secrets && chmod 600 secrets/*
```

Save this as `compose.yaml` in the folder, then run `docker compose up -d`.

```yaml
services:
  runner:
    image: ghcr.io/unifinity-software/messagebus-runner:1.5.2
    platform: linux/amd64
    restart: unless-stopped
    environment:
      MBUS_SERVER: https://app.messagebus.ai
      MBUS_MACHINE_NAME: docker-runner   # its name on Fleet; one name per runner
    volumes:
      - ./secrets:/run/secrets:ro
      - runner-home:/home/runner         # keeps the runner's identity: don't delete it
volumes:
  runner-home:
```

Without Compose:

```sh
docker run -d --name messagebus-runner --platform linux/amd64 --restart unless-stopped \
  -e MBUS_SERVER=https://app.messagebus.ai -e MBUS_MACHINE_NAME=docker-runner \
  -v "$PWD/secrets:/run/secrets:ro" -v messagebus-runner-home:/home/runner \
  ghcr.io/unifinity-software/messagebus-runner:1.5.2
```

Podman works the same way with `podman compose` or `podman run`.

## 3. Approve it

1. Open **Fleet** in the dashboard. The runner appears as a shared machine named `docker-runner`.
2. Approve it, give it roles, and set **Tasks at once**.
3. Delete `secrets/mbus_api_key`. From now on the runner signs in with its own machine key, kept in the
   `runner-home` volume. Keep the agent keys.

`docker compose logs` should show `Runner connection: connected`, and a `Ready to run:` line that lists each
agent you gave a key.

## Update

Change the version in `compose.yaml`, then run `docker compose pull` and `docker compose up -d`. Keep the
`runner-home` volume. A runner in a container doesn't update itself; a new version always means a new image.

## If something is wrong

- **The runner isn't on Fleet:** check `docker compose logs`, the `MBUS_SERVER` address and the
  `mbus_api_key` file.
- **An agent is missing from `Ready to run:`:** its key file is missing or misnamed.
- **An agent takes tasks but they fail:** the key works, but its account can't use the model. Check the
  account's billing and model access.
- **The `runner-home` volume was deleted:** the runner needs `mbus_api_key` again and joins as a new machine.

## Other setups

### Azure Container Instances

Deploys the public image with a template that keeps the runner's identity in Azure storage:
[Azure Container Instances](docs/azure-container-instances.md).

### Azure Container Apps

[Azure Container Apps](docs/azure-container-apps.md).

### Your own image

Add tools or pin agent versions by building from a release tag: [Custom images and private
registries](docs/custom-image.md).
