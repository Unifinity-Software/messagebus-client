"""Exercise signed payload comparison and resumable publication without uploading."""
from importlib import import_module
import os
from contextlib import redirect_stdout
from io import StringIO
from pathlib import Path
from subprocess import CompletedProcess
from tempfile import mkdtemp
import unittest
from unittest.mock import patch
from zipfile import ZipFile

release = import_module("check-release-version")
VERSION = "0.2.0"
CANDIDATE = release.release_version(VERSION, "0.2.0")


class ReleaseVersionTests(unittest.TestCase):
    def setUp(self):
        self.folder = Path(mkdtemp(prefix="release-fixture-", dir=os.environ.get("RUNNER_TEMP")))

    def archive(self, filename, entries):
        path = self.folder / filename
        with ZipFile(path, "w") as archive:
            for name, data in entries.items():
                archive.writestr(name, data)
        return path

    def test_only_the_exact_root_signature_may_be_added(self):
        entries = {"lib/product.dll": b"payload", "product.nuspec": b"commit 1"}
        local = self.archive("local.nupkg", entries)
        signed = {**entries, release.SIGNATURE: b"mock"}
        release.compare_payload(local, self.archive("signed.nupkg", signed))
        cases = {
            "changed bytes": {**signed, "lib/product.dll": b"modified"},
            "different source": {**signed, "product.nuspec": b"commit 2"},
            "missing entry": {release.SIGNATURE: b"mock", "product.nuspec": b"commit 1"},
            "extra entry": {**signed, "lib/.signature.p7s": b"extra"},
            "wrong signature path": {**entries, ".Signature.p7s": b"mock"},
        }
        for name, published in cases.items():
            with self.subTest(name=name), self.assertRaises(ValueError):
                release.compare_payload(local, self.archive("different.nupkg", published))

    def test_fresh_run_rejects_existing_and_resume_rejects_newer(self):
        for versions, resume in (([VERSION], False), ([VERSION, "0.3.0"], True)):
            with self.subTest(resume=resume), patch.object(release, "published_versions", return_value=versions):
                with self.assertRaisesRegex(ValueError, "must be newer"):
                    release.check_versions(VERSION, CANDIDATE, resume)

    def test_fresh_push_rejects_a_duplicate_even_if_nuget_exits_zero(self):
        result = CompletedProcess([], 0, stdout="Conflict\n")
        with patch.object(release.subprocess, "run", return_value=result) as command, redirect_stdout(StringIO()):
            with self.assertRaisesRegex(ValueError, "did not confirm a new upload"):
                release.push(self.folder / "candidate.nupkg", "--no-symbols", require_upload=True)
            self.assertIn("--skip-duplicate", command.call_args.args[0])

    def test_resume_checks_all_existing_before_push_and_retries_symbols(self):
        first, second, *_ = release.PUBLISH_ORDER

        def published(package):
            return [VERSION] if package in {first, second} else []

        self.folder.joinpath(f"{first}.{VERSION}.snupkg").touch()
        download = self.folder / "published.nupkg"
        with (patch.object(release, "published_versions", side_effect=published),
              patch.object(release, "verify_published", side_effect=[None, ValueError("different")]),
              patch.object(release, "push") as push):
            with self.assertRaisesRegex(ValueError, "different"):
                release.publish(self.folder, VERSION, CANDIDATE, True, download)
            push.assert_not_called()

        with (patch.object(release, "published_versions", side_effect=published),
              patch.object(release, "verify_published") as verify,
              patch.object(release, "push") as push):
            release.publish(self.folder, VERSION, CANDIDATE, True, download)
            self.assertEqual([call.args[0] for call in verify.call_args_list], list(release.PUBLISH_ORDER))
            self.assertEqual([call.args[0].name for call in push.call_args_list],
                             [f"{first}.{VERSION}.snupkg",
                              *(f"{p}.{VERSION}.nupkg" for p in release.PUBLISH_ORDER[2:])])


if __name__ == "__main__":
    unittest.main()
