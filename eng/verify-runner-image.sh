#!/usr/bin/env bash
# Inspect and run the linux/amd64 image from the staged OCI archive.
set -euo pipefail

archive=$1
version=$2
revision=$3
reference="oci-archive:$archive"
layout=$(mktemp -d "$RUNNER_TEMP/runner-layout.XXXXXXXX")
tar -xf "$archive" -C "$layout"

index=$(skopeo inspect --raw "$reference")
platform=$(jq -er '
  select(.mediaType == "application/vnd.oci.image.index.v1+json" and (.manifests | length) == 2)
  | [.manifests[] | select(.platform.os == "linux" and .platform.architecture == "amd64" and (.platform.variant // "") == "")]
  | select(length == 1) | .[0].digest' <<<"$index")
attestation=$(jq -er --arg platform "$platform" '
  [.manifests[] | select(.annotations["vnd.docker.reference.type"] == "attestation-manifest"
    and .annotations["vnd.docker.reference.digest"] == $platform)]
  | select(length == 1) | .[0].digest' <<<"$index")
blob() {
  [[ "$1" =~ ^sha256:[a-f0-9]{64}$ ]]
  local path="$layout/blobs/sha256/${1#sha256:}"
  [[ "$(sha256sum "$path" | cut -d ' ' -f1)" == "${1#sha256:}" ]]
  cat "$path"
}
provenance=$(blob "$attestation" | jq -er '
  .layers[] | .annotations["in-toto.io/predicate-type"] as $type
  | select($type == "https://slsa.dev/provenance/v0.2" or $type == "https://slsa.dev/provenance/v1")
  | .digest')
blob "$provenance" | jq -e --arg revision "$revision" --arg platform "${platform#sha256:}" '
  def materials:
    if .predicateType == "https://slsa.dev/provenance/v0.2" then .predicate.materials
    elif .predicateType == "https://slsa.dev/provenance/v1" then .predicate.buildDefinition.resolvedDependencies
    else [] end;
  any(.subject[]; .digest.sha256 == $platform)
  and any(materials[]; .digest.sha1 == $revision
    and (.uri | split("#")[0] | sub("^git[+]"; "") | ascii_downcase)
      == "https://github.com/unifinity-software/messagebus-client.git")' >/dev/null

skopeo --override-os linux --override-arch amd64 copy "$reference" docker-daemon:mbus-runner:candidate >&2
[[ "$(docker image inspect --format '{{.Os}}/{{.Architecture}} {{.Config.User}}' mbus-runner:candidate)" == 'linux/amd64 runner' ]]
docker image inspect --format '{{json .Config.Labels}}' mbus-runner:candidate | jq -e \
  --arg revision "$revision" --arg version "$version" '
    .["org.opencontainers.image.revision"] == $revision and .["org.opencontainers.image.version"] == $version
    and .["org.opencontainers.image.source"] == "https://github.com/Unifinity-Software/messagebus-client"' >/dev/null

check_version() {
  local executable=$1 expected=$2 reported
  reported=$(docker run --rm --network none --platform linux/amd64 --entrypoint "$executable" mbus-runner:candidate --version)
  # Harnesses may print a product name and end the version sentence with a period.
  if ! awk -v expected="$expected" '
    { for (i = 1; i <= NF; i++) {
        value = $i
        sub(/\.$/, "", value)
        if (value == expected) found = 1
      } }
    END { exit !found }' <<<"$reported"; then
    printf '%s reported %s, expected %s\n' "$executable" "$reported" "$expected" >&2
    return 1
  fi
  printf '%s %s\n' "$executable" "$expected"
}

pinned_version() {
  local value
  value=$(sed -n "s/^ARG $1=//p" docker/runner/Dockerfile)
  [[ "$value" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || return 1
  printf '%s\n' "$value"
}

check_version mbus "$version"
check_version claude "$(pinned_version CLAUDE_CODE_VERSION)"
check_version codex "$(pinned_version CODEX_VERSION)"
check_version opencode "$(pinned_version OPENCODE_VERSION)"
check_version copilot "$(pinned_version COPILOT_VERSION)"

if refusal=$(docker run --rm --network none --entrypoint mbus mbus-runner:candidate update --check 2>&1); then
  echo 'The image unexpectedly allowed an in-place update check.' >&2
  exit 1
fi
grep -Fq 'redeploy the runner' <<<"$refusal"
