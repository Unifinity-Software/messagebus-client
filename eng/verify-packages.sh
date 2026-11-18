#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 2 ]]; then
    echo "Usage: $0 <folder> <version>" >&2
    exit 2
fi

script_dir=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
folder=$(cd -- "$1" && pwd)
version=$2
python=python3
if [[ ${RUNNER_OS:-} == Windows ]]; then
    python=python
fi
"$python" "$script_dir/check-packages.py" "$folder" "$version"

scratch=$(mktemp -d "${TMPDIR:-/tmp}/mbus-packages.XXXXXXXX")
trap 'rm -rf -- "$scratch"' EXIT
export NUGET_PACKAGES="$scratch/packages"
export DOTNET_CLI_HOME="$scratch/home"
export NUGET_HTTP_CACHE_PATH="$scratch/http-cache"
export DOTNET_NOLOGO=1
unset NUGET_FALLBACK_PACKAGES
"$python" - "$folder" "$scratch/NuGet.Config" <<'PY'
import sys
from pathlib import Path
from xml.etree.ElementTree import Element, ElementTree, SubElement

root = Element("configuration")
SubElement(SubElement(root, "fallbackPackageFolders"), "clear")
sources = SubElement(root, "packageSources")
SubElement(sources, "clear")
SubElement(sources, "add", key="local", value=str(Path(sys.argv[1]).resolve()))
ElementTree(root).write(sys.argv[2], encoding="utf-8")
PY
cd "$scratch"
dotnet tool install MessageBus.Ai.Cli --version "$version" --configfile NuGet.Config \
    --tool-path tools --no-cache
mbus=./tools/mbus
if [[ -f "$mbus.exe" ]]; then
    mbus="$mbus.exe"
fi
actual=$("$mbus" --version)
actual=${actual%$'\r'}
if [[ ${actual%%+*} != "${version%%+*}" ]]; then
    printf 'Expected mbus version %s, got %s\n' "$version" "$actual" >&2
    exit 1
fi
"$mbus" --help >/dev/null
"$mbus" runner install --help >/dev/null
printf 'Verified eight packages, seven symbol packages and mbus %s\n' "$actual"
