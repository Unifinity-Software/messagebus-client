"""Check that a release advances every package on nuget.org."""
from importlib import import_module
import json
from pathlib import Path
import re
import sys
from urllib.error import HTTPError
from urllib.request import Request, urlopen
from xml.etree import ElementTree

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


if __name__ == "__main__":
    try:
        props = Path(__file__).resolve().parents[1] / "Directory.Build.props"
        version = sys.argv[1]
        candidate = release_version(version, ElementTree.parse(props).findtext(".//MbusSourceVersion"))
        for package in sorted(PACKAGES):
            if any(precedence(existing) >= candidate for existing in published_versions(package)):
                raise ValueError(f"{version} must be newer than every published version of {package}.")
    except (ValueError, OSError) as error:
        sys.exit(str(error))
