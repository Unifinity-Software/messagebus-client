# The messagebus.ai runner image: mbus plus the coding agents it can start (Claude Code, Codex,
# OpenCode, GitHub Copilot CLI). It needs no inbound port: the runner connects out to the server.
# Secrets (the machine's API key, the agents' keys) come from the environment or from files the
# container platform mounts from the customer's secret store; see docker/runner/README.md.
#
# Build from the repository root:
#   podman build -f docker/runner/Dockerfile -t messagebus-runner .
#   docker build -f docker/runner/Dockerfile -t messagebus-runner .

ARG NODE_VERSION=24
ARG MBUS_RELEASE_VERSION
ARG MBUS_SOURCE_REVISION

# ── mbus, self-contained for the image's architecture ─────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
ARG MBUS_RELEASE_VERSION
ARG MBUS_SOURCE_REVISION
RUN if [ -n "$MBUS_RELEASE_VERSION" ]; then \
      [ "$(printf '%s\n' "$MBUS_RELEASE_VERSION" | grep -Ex '[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?')" = "$MBUS_RELEASE_VERSION" ] \
        && [ -n "$MBUS_SOURCE_REVISION" ] \
        || { echo "Supply a release version and a full Git SHA for a release image." >&2; exit 1; }; \
    fi \
 && if [ -n "$MBUS_SOURCE_REVISION" ]; then \
      [ "$(printf '%s\n' "$MBUS_SOURCE_REVISION" | grep -Ex '[0-9a-f]{40}')" = "$MBUS_SOURCE_REVISION" ] \
        || { echo "MBUS_SOURCE_REVISION must be a full Git SHA." >&2; exit 1; }; \
    fi
WORKDIR /src
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY src/ src/
# The orchestrate skill under skills/ is embedded in mbus. The glob lets the build work before
# that folder exists; global.json is listed only so the COPY always has a source, and is removed.
COPY global.json skill[s] skills/
# Build output copied from a developer checkout (Windows paths in obj/) would confuse restore.
RUN rm -f skills/global.json \
 && find src -type d \( -name bin -o -name obj -o -name node_modules \) -prune -exec rm -rf {} + \
 && case "${TARGETARCH:-amd64}" in arm64) rid=linux-arm64 ;; *) rid=linux-x64 ;; esac \
 && set -- \
 && if [ -n "$MBUS_SOURCE_REVISION" ]; then set -- "$@" "-p:SourceRevisionId=$MBUS_SOURCE_REVISION"; fi \
 && if [ -n "$MBUS_RELEASE_VERSION" ]; then set -- "$@" "-p:Version=$MBUS_RELEASE_VERSION" -p:MbusReleaseBuild=true; fi \
 && dotnet publish src/MessageBus.Cli.Tool/MessageBus.Cli.Tool.csproj -c Release -r "$rid" --self-contained \
      -p:PublishSingleFile=true -p:DebugType=none -p:MbusImageManaged=true "$@" -o /out

# ── runtime: Node (the agent CLIs are npm packages) plus mbus ─────────────────
FROM node:${NODE_VERSION}-trixie-slim AS protected-runtime
ARG TARGETARCH

# git and ssh for the code the agents work on, ICU for .NET, tini to pass signals to mbus.
RUN apt-get update \
 && apt-get upgrade -y \
 && apt-get install -y --no-install-recommends ca-certificates git openssh-client libicu76 tini bubblewrap netcat-openbsd \
 && rm -rf /var/lib/apt/lists/*

# ACI hosts may deny private kernel procfs. The amd64 userspace kernel keeps
# dynamic procfs private without exposing the enclosing container's processes.
# The installer verifies the exact official release checksum before extraction.
COPY docker/runner/install-gvisor.mjs /tmp/mbus-install-gvisor.mjs
RUN node /tmp/mbus-install-gvisor.mjs "${TARGETARCH:-amd64}" && rm /tmp/mbus-install-gvisor.mjs
COPY docker/runner/gvisor-LICENSE /usr/local/lib/mbus/gvisor-LICENSE
COPY docker/runner/aci-sandbox.mjs /usr/local/lib/mbus/aci-sandbox.mjs
COPY docker/runner/aci-codex-probe.mjs /usr/local/lib/mbus/aci-codex-probe.mjs

FROM protected-runtime

# Keep the release harness versions exact. Custom builds can override them.
ARG CLAUDE_CODE_VERSION=2.1.330
ARG CODEX_VERSION=0.171.0
ARG OPENCODE_VERSION=1.19.6
ARG COPILOT_VERSION=1.0.114
ARG NPM_VERSION=12.2.3

# Allow the two install scripts that fetch each CLI's native binary.
RUN npm install -g --no-fund --no-audit --ignore-scripts "npm@${NPM_VERSION}" \
 && npm install -g --no-fund --no-audit --allow-scripts=@anthropic-ai/claude-code,opencode-ai \
      "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" \
      "@openai/codex@${CODEX_VERSION}" \
      "opencode-ai@${OPENCODE_VERSION}" \
      "@github/copilot@${COPILOT_VERSION}" \
 && npm cache clean --force

COPY --from=build /out/mbus /usr/local/bin/mbus
COPY docker/runner/entrypoint.sh /usr/local/bin/mbus-entrypoint
COPY docker/runner/aci-bootstrap.mjs /usr/local/lib/mbus/aci-bootstrap.mjs
# Strip CRs in case the script was checked out with Windows line endings.
RUN sed -i 's/\r$//' /usr/local/bin/mbus-entrypoint && chmod 755 /usr/local/bin/mbus-entrypoint /usr/local/bin/mbus

# A non-root user owns the work: agent sign-in state and mbus settings live in its home, checked-out
# code under ~/work. Mount a volume at /home/runner to keep them across restarts.
RUN useradd --create-home --uid 10001 --shell /bin/bash runner \
 && mkdir -p /home/runner/work && chown -R runner:runner /home/runner
USER runner
WORKDIR /home/runner/work

# The image is updated by rebuilding it, so the agents don't update themselves inside it.
ENV DISABLE_AUTOUPDATER=1 \
    MBUS_SECRETS_DIR=/run/secrets

ARG MBUS_RELEASE_VERSION
ARG MBUS_SOURCE_REVISION
LABEL org.opencontainers.image.source="https://github.com/Unifinity-Software/messagebus-client" \
      org.opencontainers.image.description="messagebus.ai runner for Docker and Azure Container Instances" \
      org.opencontainers.image.documentation="https://github.com/Unifinity-Software/messagebus-client/blob/main/docker/runner/README.md" \
      org.opencontainers.image.version="$MBUS_RELEASE_VERSION" \
      org.opencontainers.image.revision="$MBUS_SOURCE_REVISION"

ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/mbus-entrypoint"]
CMD ["runner"]
