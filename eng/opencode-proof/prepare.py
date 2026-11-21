"""Explicit native setup only. No install scripts, model calls, global installs or tests.

Registry metadata retrieved 2026-11-21. Integrity authenticates the pinned registry
artifact, not a reproducible source-to-binary binding (registry exposes no gitHead).
"""
import argparse
import base64
import hashlib
import io
import json
import os
from pathlib import Path
import sys
import tarfile
import urllib.request

PINS = {
    "windows-x64": "sha512-xCVbrA8A/qQTAdBIKyhK5tx3/mcq18yF82Mi8phry0MJb2631nnnpxF9m6FFfWAVHyer4qFGrO7hbWXesHQ1Gg==",
    "darwin-arm64": "sha512-0MbSbKkmwVHtgP31GJyyECe6QVy4T7W4Wp7s2TsmXHTPPyf01ne3dWSX97X9dkNK28Lc6kt+zMPQUP/9GgDLZg==",
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--platform", choices=PINS, required=True)
    parser.add_argument("--scratch", type=Path, required=True, help="New task-owned directory; must not exist")
    args = parser.parse_args()
    root = args.scratch.resolve()
    root.mkdir(parents=True, exist_ok=False)
    (root / ".opencode-proof-owned").write_text(str(root), encoding="utf-8")
    name = "opencode-" + args.platform
    url = "https://registry.npmjs.org/" + name
    expected_tarball = f"{url}/-/{name}-1.19.5.tgz"
    with urllib.request.urlopen(url + "/1.19.5", timeout=30) as response:
        metadata_bytes = response.read(1024 * 1024)
    (root / "metadata.json").write_bytes(metadata_bytes)
    metadata = json.loads(metadata_bytes)
    assert metadata["name"] == name and metadata["version"] == "1.19.5"
    assert metadata["dist"]["tarball"] == expected_tarball
    assert metadata["dist"]["integrity"] == PINS[args.platform], "Registry pin changed"
    request = urllib.request.Request(expected_tarball)
    with urllib.request.urlopen(request, timeout=120) as response:
        assert response.url == expected_tarball, "Unexpected artifact redirect"
        archive = response.read(256 * 1024 * 1024)
    actual = "sha512-" + base64.b64encode(hashlib.sha512(archive).digest()).decode("ascii")
    assert actual == PINS[args.platform], "Archive digest mismatch"
    (root / "package.tgz").write_bytes(archive)
    binary_member = "package/bin/opencode" + (".exe" if args.platform == "windows-x64" else "")
    # Only these two regular files are expected. Reject links, devices, extra paths and traversal BEFORE writing.
    with tarfile.open(fileobj=io.BytesIO(archive), mode="r:gz") as tar:
        members = tar.getmembers()
        assert len(members) == 2 and {m.name for m in members} == {"package/package.json", binary_member}
        assert all(m.isfile() and m.size < 240 * 1024 * 1024 for m in members)
        package = json.load(tar.extractfile("package/package.json"))
        assert package["name"] == name and package["version"] == "1.19.5"
        binary = tar.extractfile(binary_member).read()
        destination = root / binary_member
        destination.parent.mkdir(parents=True)
        destination.write_bytes(binary)
        (root / "package/package.json").write_text(json.dumps(package, indent=2), encoding="utf-8")
        if os.name != "nt":
            destination.chmod(0o700)
    manifest = {
        "package": name, "version": "1.19.5", "registry": url + "/1.19.5",
        "tarball": expected_tarball, "sri": actual,
        "archiveSha256": hashlib.sha256(archive).hexdigest(),
        "binarySha256": hashlib.sha256(binary).hexdigest(),
        "executable": str(destination), "python": sys.executable,
        "evidence": str(root / "evidence"), "scratch": str(root),
        "upstreamSourceReviewed": "810c0eba1a7348036f8bdf61e6632d0c36c6876c",
        "sourceToBinaryBinding": "Not established by registry metadata; runtime compatibility must be proven",
    }
    (root / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    main()
