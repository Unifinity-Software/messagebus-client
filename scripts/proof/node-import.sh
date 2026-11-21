#!/bin/bash
# Run only in a disposable Linux container, as root. Tests themselves run nonroot.
set -euo pipefail
test "$(uname -s)" = Linux
test "$(id -u)" = 0
if [[ ! -e /run/.containerenv && ! -e /.dockerenv ]]; then
    echo 'Refusing host setup: a disposable container is required.' >&2
    exit 1
fi
for tool in /usr/bin/bwrap /usr/bin/node /usr/bin/npm; do test -x "$tool"; done
for tool in python3 openssl update-ca-certificates runuser timeout dotnet git; do command -v "$tool" >/dev/null; done
[[ "$(/usr/bin/node --version)" == v24.* ]]
[[ "$(dotnet --version)" == 10.* ]]
: "${NUGET_PACKAGES:?Mount a populated offline NuGet package cache and set NUGET_PACKAGES}"
source_root=$(cd "$(dirname "$0")/../.." && pwd)
proof_root=$(mktemp -d /tmp/mbus-node-proof.XXXXXXXX)
echo "Retained proof/cleanup root: $proof_root"
mkdir "$proof_root/source" "$proof_root/served" "$proof_root/home" "$proof_root/feed"
tar -C "$source_root" --exclude=.git --exclude=bin --exclude=obj --exclude=node_modules -cf - . |
    tar -C "$proof_root/source" -xf -
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -subj /CN=fixture.test \
    -addext subjectAltName=DNS:fixture.test \
    -keyout "$proof_root/server.key" -out "$proof_root/server.crt" 2>"$proof_root/openssl.log"
cp "$proof_root/server.crt" /usr/local/share/ca-certificates/mbus-proof.crt
update-ca-certificates >"$proof_root/ca.log" 2>&1
echo '127.0.0.1 fixture.test' >> /etc/hosts
id mbusproof >/dev/null 2>&1 || useradd --no-create-home mbusproof
chown -R mbusproof:mbusproof "$proof_root"
python3 "$source_root/scripts/proof/https-git.py" "$proof_root/served" \
    "$proof_root/server.crt" "$proof_root/server.key" >"$proof_root/https.log" 2>&1 &
server_pid=$!
trap 'kill "$server_pid" 2>/dev/null || true' EXIT
# env -i prevents real runner configuration or proxy settings entering this fixture.
# Restore is offline and bounded. No packages, tools or images are downloaded here.
runuser -u mbusproof -- env -i PATH="$PATH" HOME="$proof_root/home" \
    NUGET_PACKAGES="$NUGET_PACKAGES" DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    MBUS_LINUX_PROOF_ROOT="$proof_root" \
    timeout 600 bash -euc '
        cd "$MBUS_LINUX_PROOF_ROOT/source"
        dotnet restore tests/MessageBus.Cli.Tests --source "$MBUS_LINUX_PROOF_ROOT/feed"
        dotnet test --project tests/MessageBus.Cli.Tests --no-restore \
            -- --filter-class "*LinuxNodeImportProofTests"
    ' 2>&1 | tee "$proof_root/proof.log"
test "$(cat "$proof_root/return.passed")" = passed
test "$(cat "$proof_root/alternates.passed")" = passed
echo 'MBUS_LINUX_IMPORT_RETURN_PROOF_PASSED cases=2'
# Intentionally retain scratch/builds for the runner/container owner to remove.
