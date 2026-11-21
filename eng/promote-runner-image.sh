#!/usr/bin/env bash
set -euo pipefail

archive=$1
expected=$2
version=$3
image=ghcr.io/unifinity-software/messagebus-runner
candidate="oci-archive:$archive"
destination="docker://$image:$version"
[[ "$expected" =~ ^sha256:[a-f0-9]{64}$ ]] || exit 1
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[a-z0-9.-]+)?$ ]] || exit 1

# Hash the original manifest bytes, including attestations. Do not reserialize JSON.
digest() {
  local hash
  hash=$(skopeo inspect --raw "$@" | sha256sum) || return
  printf 'sha256:%s\n' "${hash%% *}"
}

[[ "$(digest "$candidate")" == "$expected" ]] || {
  echo 'The staged OCI candidate does not match the verified digest.' >&2
  exit 1
}
auth=$(mktemp -d "$RUNNER_TEMP/runner-auth.XXXXXXXX") || exit 1
trap 'rm -f "$auth/write.json"; rmdir "$auth"' EXIT
printf '%s' "$GH_TOKEN" | skopeo login --authfile "$auth/write.json" \
  --username "$GITHUB_ACTOR" --password-stdin ghcr.io || exit 1
if existing=$(digest --authfile "$auth/write.json" "$destination" 2>&1); then
  if [[ "$existing" != "$expected" ]]; then
    printf '%s already points to %s, expected %s\n' "$destination" "$existing" "$expected" >&2
    exit 1
  fi
elif [[ "$existing" == *'manifest unknown'* || "$existing" == *'name unknown'* ]]; then
  skopeo copy --all --preserve-digests --dest-authfile "$auth/write.json" "$candidate" "$destination" || exit 1
else
  printf 'Unable to determine whether the release tag exists: %s\n' "$existing" >&2
  exit 1
fi

# Check both the public tag and a full pull by digest without any registry credentials.
[[ "$(digest --no-creds "$destination")" == "$expected" ]] || {
  echo 'The versioned image is not publicly readable at the verified digest.' >&2
  exit 1
}
skopeo copy --all --preserve-digests --src-no-creds \
  "docker://$image@$expected" "oci:$RUNNER_TEMP/public-runner" || exit 1
[[ "$(digest "oci:$RUNNER_TEMP/public-runner")" == "$expected" ]] || exit 1
skopeo --override-os linux --override-arch amd64 inspect --config "oci:$RUNNER_TEMP/public-runner" |
  jq -e '.os == "linux" and .architecture == "amd64"' >/dev/null
