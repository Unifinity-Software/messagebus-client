"""Validate the NuGet archives before installing or publishing them."""
import sys
from pathlib import Path
from xml.etree import ElementTree
from zipfile import BadZipFile, ZipFile

TOOL = "MessageBus.Ai.Cli"
LIBRARIES = {"MessageBus.Ai." + suffix for suffix in (
    "Attachments.Client", "Cli.Core", "Contracts", "Crypto", "Mcp",
    "Runner.Transport", "Testing",
)}
PACKAGES = LIBRARIES | {TOOL}
PACKAGE_IDS_LOWER = {package.lower() for package in PACKAGES}


def check_archive(path, version, expected, symbols, commit):
    with ZipFile(path) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)):
            raise ValueError("duplicate archive entries")
        specs = [name for name in names if name.endswith(".nuspec") and "/" not in name]
        if len(specs) != 1:
            raise ValueError("expected one root nuspec")
        metadata = ElementTree.fromstring(archive.read(specs[0])).find("{*}metadata")
        package = metadata.findtext("{*}id") if metadata is not None else None
        if package not in expected:
            raise ValueError(f"unexpected or duplicate package ID: {package}")
        if metadata.findtext("{*}version") != version:
            raise ValueError(f"expected version {version}")
        if commit is not None:
            repository = metadata.find("{*}repository")
            if repository is None or repository.get("commit") != commit:
                raise ValueError("repository commit must match the verified candidate")
        if path.name != f"{package}.{version}{path.suffix}":
            raise ValueError("filename must agree with package ID and version")
        for dependency in metadata.findall("{*}dependencies//{*}dependency"):
            identity = dependency.get("id", "")
            if identity.lower().startswith("messagebus.ai.") and (
                identity.lower() not in PACKAGE_IDS_LOWER
                or dependency.get("version") not in {version, f"[{version}]"}
            ):
                raise ValueError(f"invalid internal dependency: {identity}")
        if symbols:
            if not any(name.endswith(".pdb") and archive.open(name).read(4) == b"BSJB"
                       for name in names):
                raise ValueError("missing portable PDB")
        else:
            license_node = metadata.find("{*}license")
            if license_node is None or license_node.get("type") != "expression" or license_node.text != "MIT":
                raise ValueError("expected MIT license expression")
            for field in ("readme", "icon"):
                entry = metadata.findtext(f"{{*}}{field}")
                if entry not in names or not archive.getinfo(entry).file_size:
                    raise ValueError(f"missing {field} file")
        if corrupt := archive.testzip():
            raise ValueError(f"corrupt archive entry: {corrupt}")
        expected.remove(package)


def verify(folder, version, commit=None):
    remaining = {".nupkg": PACKAGES.copy(), ".snupkg": LIBRARIES.copy()}
    for path in sorted(folder.iterdir()):
        suffix = path.suffix.lower()
        if suffix not in remaining:
            continue
        try:
            check_archive(path, version, remaining[suffix], suffix == ".snupkg", commit)
        except (ValueError, BadZipFile, ElementTree.ParseError, OSError) as error:
            raise ValueError(f"{path.name}: {error}") from error
    for suffix, missing in remaining.items():
        if missing:
            raise ValueError(f"missing {suffix}: {', '.join(sorted(missing))}")


if __name__ == "__main__":
    try:
        verify(Path(sys.argv[1]), *sys.argv[2:])
    except (ValueError, OSError) as error:
        sys.exit(str(error))
