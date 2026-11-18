"""Regression fixtures for package checks, independent of dotnet and NuGet."""
import importlib
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from zipfile import ZipFile

checker = importlib.import_module("check-packages")
release = importlib.import_module("check-release-version")
VERSION = "0.2.0-preview.0"
PACKAGE = "MessageBus.Ai.Contracts"


class Packages(unittest.TestCase):
    def setUp(self):
        tmp = TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.folder = Path(tmp.name)
        for package in checker.PACKAGES:
            self.write(package)
        for package in checker.LIBRARIES:
            self.write(package, symbols=True)

    def write(self, package, *, symbols=False, version=VERSION, dependency=VERSION,
              include_icon=True, pdb=b"BSJB"):
        suffix = ".snupkg" if symbols else ".nupkg"
        path = self.folder / f"{package}.{VERSION}{suffix}"
        with ZipFile(path, "w") as archive:
            archive.writestr(f"{package}.nuspec", f"""
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
  <metadata>
    <id>{package}</id><version>{version}</version>
    <readme>README.md</readme><icon>icon.png</icon><license type="expression">MIT</license>
    <dependencies><group><dependency id="MessageBus.Ai.Crypto" version="{dependency}"/></group></dependencies>
  </metadata>
</package>""")
            if symbols:
                archive.writestr("lib/net10.0/Library.pdb", pdb)
            else:
                archive.writestr("README.md", "Readme")
                if include_icon:
                    archive.writestr("icon.png", "PNG")

    def rejected(self, error):
        with self.assertRaisesRegex(ValueError, error):
            checker.verify(self.folder, VERSION)

    def test_complete_set(self):
        checker.verify(self.folder, VERSION)

    def test_extra_package(self):
        self.write("Unexpected")
        self.rejected("unexpected or duplicate package ID")

    def test_missing_package(self):
        (self.folder / f"{PACKAGE}.{VERSION}.nupkg").unlink()
        self.rejected("missing .nupkg")

    def test_wrong_version(self):
        self.write(PACKAGE, version="0.1.0")
        self.rejected("expected version")

    def test_wrong_dependency(self):
        self.write(PACKAGE, dependency="0.1.0")
        self.rejected("invalid internal dependency")

    def test_missing_metadata_file(self):
        self.write(PACKAGE, include_icon=False)
        self.rejected("missing icon file")

    def test_invalid_symbols(self):
        self.write(PACKAGE, symbols=True, pdb=b"not a PDB")
        self.rejected("missing portable PDB")

    def test_tool_symbols_rejected(self):
        self.write(checker.TOOL, symbols=True)
        self.rejected("unexpected or duplicate package ID")


class ReleaseVersion(unittest.TestCase):
    def test_prerelease_at_baseline_can_advance_an_older_package(self):
        candidate = release.release_version("0.2.0-preview.1", "0.2.0")
        self.assertGreater(candidate, release.precedence("0.1.2-preview.2"))
        self.assertLess(candidate, release.precedence("0.2.0"))
        self.assertLess(candidate, release.precedence("0.2.0-preview.10"))

    def test_rejects_ci_and_non_normalized_release_versions(self):
        for invalid in ("0.0.0-ci.1", "0.2.0-preview.01", "0.2.0+build.1"):
            with self.subTest(version=invalid), self.assertRaises(ValueError):
                release.release_version(invalid, "0.2.0")


if __name__ == "__main__":
    unittest.main()
