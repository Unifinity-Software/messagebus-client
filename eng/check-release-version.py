"""Check and publish a coordinated release on nuget.org."""
from importlib import import_module
import json
from pathlib import Path
import re
import sys
import subprocess
import time
from urllib.error import HTTPError
from urllib.request import Request, urlopen
from xml.etree import ElementTree
from zipfile import BadZipFile, ZipFile

PACKAGES = import_module("check-packages").PACKAGES
VERSION = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?")


def precedence(version):
    match = VERSION.fullmatch(version)
    if not match:
        raise ValueError(f"Unsupported version: {version}")
    major, minor, patch, suffix = match.groups()
    labels = tuple((0, int(item)) if item.isdigit() else (1, item.lower())
                   for item in suffix.split(".")) if suffix else ()
    return (int(major), int(minor), int(patch)), suffix is None, labels


def release_version(version, baseline):
    core, stable, labels = precedence(version)
    if version != version.lower() or (not stable and any(
        item.isdigit() and len(item) > 1 and item.startswith("0")
        for item in version.split("-", 1)[1].split(".")
    )):
        raise ValueError("Use a normalized SemVer version without build metadata.")
    if core < precedence(baseline)[0]:
        raise ValueError(f"Version core must be at least {baseline}.")
    return core, stable, labels


def published_versions(package):
    url = f"https://api.nuget.org/v3-flatcontainer/{package.lower()}/index.json"
    try:
        with urlopen(Request(url, headers={"Cache-Control": "no-cache"}), timeout=30) as response:
            return json.load(response)["versions"]
    except HTTPError as error:
        if error.code == 404:
            return []
        raise


# Order follows the package references, with the executable always last.
PUBLISH_ORDER = tuple("MessageBus.Ai." + suffix for suffix in (
    "Contracts", "Crypto", "Attachments.Client", "Runner.Transport", "Mcp",
    "Cli.Core", "Testing", "Cli",
))
FEED = "https://api.nuget.org"
SIGNATURE = ".signature.p7s"


def check_versions(version, candidate, resume):
    existing = set()
    for package in PUBLISH_ORDER:
        for published in published_versions(package):
            if resume and published == version:
                existing.add(package)
            elif precedence(published) >= candidate:
                raise ValueError(f"{version} must be newer than every published version of {package}.")
    return existing


def compare_payload(candidate, published):
    # NuGet adds one case-sensitive root entry to an unsigned payload.
    # https://github.com/NuGet/Home/wiki/Package-Signatures-Technical-Details
    with ZipFile(candidate) as local, ZipFile(published) as remote:
        local_names = [entry.orig_filename for entry in local.infolist()]
        remote_names = [entry.orig_filename for entry in remote.infolist()]
        if (len(local_names) != len(set(local_names))
                or len(remote_names) != len(set(remote_names))
                or any("\0" in name for name in local_names + remote_names)
                or SIGNATURE in local_names
                or set(remote_names) != set(local_names) | {SIGNATURE}):
            raise ValueError(f"Published archive entries differ from {candidate.name}.")
        for name in local_names:
            with local.open(name) as left, remote.open(name) as right:
                while True:
                    chunk = left.read(65536)
                    if chunk != right.read(65536):
                        raise ValueError(f"Published payload differs from {candidate.name}.")
                    if not chunk:
                        break


def verify_published(package, version, path, download, wait=False):
    name = package.lower()
    url = f"{FEED}/v3-flatcontainer/{name}/{version}/{name}.{version}.nupkg"
    for attempt in range(60 if wait else 1):
        try:
            with urlopen(Request(url, headers={"Cache-Control": "no-cache"}), timeout=30) as response:
                with download.open("wb") as output:
                    while chunk := response.read(65536):
                        output.write(chunk)
            break
        except HTTPError as error:
            if error.code != 404:
                raise
            if attempt < 59 and wait:
                time.sleep(10)
    else:
        raise ValueError(f"Could not verify {path.name} on nuget.org. Retry this run after indexing.")

    compare_payload(path, download)
    # Run on the Linux publishing runner with NuGet's signature verification.
    subprocess.run(["dotnet", "nuget", "verify", "--all", str(download)], check=True)


def publish(folder, version, candidate, resume, download):
    if set(PUBLISH_ORDER) != PACKAGES or len(PUBLISH_ORDER) != len(PACKAGES):
        raise ValueError("The publication order must cover all package IDs exactly once.")
    existing = check_versions(version, candidate, resume)
    # Verify every existing package before making any writes.
    for package in PUBLISH_ORDER:
        if package in existing:
            verify_published(package, version, folder / f"{package}.{version}.nupkg", download)

    for package in PUBLISH_ORDER:
        path = folder / f"{package}.{version}.nupkg"
        if package not in existing:
            push(path, "--no-symbols", require_upload=not resume)
            # A duplicate response is not proof of a successful or matching upload.
            verify_published(package, version, path, download, wait=True)
        symbols = path.with_suffix(".snupkg")
        if symbols.is_file():
            # Retry the same symbols even when NuGet already accepted the .nupkg.
            push(symbols)


def push(path, *args, require_upload=False):
    command = ["dotnet", "nuget", "push", str(path), "--source", f"{FEED}/v3/index.json",
               "--skip-duplicate", "--force-english-output", *args]
    if require_upload:
        result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        print(result.stdout, end="", flush=True)
        result.check_returncode()
        # With --skip-duplicate, NuGet exits zero for both successful uploads and conflicts.
        if "Your package was pushed." not in (line.strip() for line in result.stdout.splitlines()):
            raise ValueError(f"Fresh publication did not confirm a new upload for {path.name}.")
    else:
        subprocess.run(command, check=True)


if __name__ == "__main__":
    try:
        props = Path(__file__).resolve().parents[1] / "Directory.Build.props"
        version = sys.argv[1]
        candidate = release_version(version, ElementTree.parse(props).findtext(".//MbusSourceVersion"))
        if len(sys.argv) == 2:
            check_versions(version, candidate, resume=False)
        else:
            folder, prepared_attempt, current_attempt, download = sys.argv[2:]
            if not 0 < int(prepared_attempt) <= int(current_attempt):
                raise ValueError("Invalid workflow attempt.")
            publish(Path(folder), version, candidate, prepared_attempt != current_attempt, Path(download))
    except (ValueError, BadZipFile, OSError, subprocess.CalledProcessError) as error:
        sys.exit(str(error))
