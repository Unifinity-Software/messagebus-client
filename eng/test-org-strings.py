"""Exercise the trusted inline scanner against real Git objects."""
import os
from pathlib import Path
import subprocess
import sys
from tempfile import TemporaryDirectory
from textwrap import dedent
import unittest

WORKFLOW = Path(__file__).resolve().parents[1] / ".github/workflows/org-strings.yml"
SCANNER = dedent(WORKFLOW.read_text().split("python3 - <<'PY'\n", 1)[1].split("\n          PY", 1)[0])


class OrgStringsTests(unittest.TestCase):
    def setUp(self):
        self.scratch = TemporaryDirectory(prefix="org-strings-", dir=os.environ.get("RUNNER_TEMP"))
        self.addCleanup(self.scratch.cleanup)
        self.root = Path(self.scratch.name)
        self.repo = self.root / "repo"
        self.repo.mkdir()
        self.git("init", "-q")
        self.git("config", "user.name", "Fixture Author")
        self.git("config", "user.email", "noreply@example.invalid")
        self.git("config", "core.hooksPath", str(self.repo / "no-hooks"))
        self.base = self.commit("Start")
        gh = self.root / "gh"
        gh.write_text("#!/bin/sh\nprintf 'maintainer\\tmaintainer\\n'\n")
        gh.chmod(0o700)

    def git(self, *args, env=None):
        return subprocess.check_output(["git", *args], cwd=self.repo, env=env).decode().strip()

    def commit(self, message, **identity):
        self.git("add", "-A", "--", ".")
        self.git("commit", "-qm", message, "--allow-empty", env={**os.environ, **identity})
        return self.git("rev-parse", "HEAD")

    def scan(self, sha, patterns="fixture-restricted", **overrides):
        return subprocess.run(
            [sys.executable, "-c", SCANNER], cwd=self.repo, capture_output=True, text=True,
            env={**os.environ, "PATH": f"{self.root}{os.pathsep}{os.environ['PATH']}",
                 "SCAN_SHA": sha, "BASE_SHA": self.base, "ORG_STRINGS": patterns,
                 "MAINTAINERS": "maintainer", "GH_REPO": "example/fixture", **overrides},
        )

    def test_removed_blob_and_filename_are_scanned_without_disclosure(self):
        hidden = self.repo / "fIXTURE-rESTRICTED.txt"
        hidden.write_bytes(b"\0Fixture-Restricted\n")
        self.commit("Add file")
        hidden.unlink()
        result = self.scan(self.commit("Remove file"))
        self.assertEqual(result.returncode, 1)
        self.assertIn("Restricted historical filename", result.stdout)
        self.assertIn("Restricted content in blob", result.stdout)
        self.assertNotIn("fixture-restricted", (result.stdout + result.stderr).lower())

    def test_tip_includes_objects_reachable_from_the_base(self):
        (self.repo / "fIXTURE-rESTRICTED").write_text("Fixture-Restricted")
        self.base = self.commit("Add file to base")
        result = self.scan(self.commit("Empty change"))
        self.assertEqual(result.returncode, 1)
        self.assertIn("Restricted filename in tree", result.stdout)
        self.assertIn("[redacted filename]:1", result.stdout)
        self.assertNotIn("fixture-restricted", (result.stdout + result.stderr).lower())

    def test_tip_content_reports_safe_filename_and_line_only(self):
        (self.repo / "canary.txt").write_text("first line\nFixture-Restricted\nlast line\n")
        result = self.scan(self.commit("Add canary"))
        self.assertEqual(result.returncode, 1)
        self.assertIn('"canary.txt":2', result.stdout)
        self.assertNotIn("fixture-restricted", (result.stdout + result.stderr).lower())

    def test_commit_names_emails_and_message_are_scanned(self):
        sha = self.commit("Fixture-Restricted message",
                          GIT_AUTHOR_NAME="Fixture-Restricted Author",
                          GIT_AUTHOR_EMAIL="noreply@fixture-restricted.invalid",
                          GIT_COMMITTER_NAME="Fixture-Restricted Committer",
                          GIT_COMMITTER_EMAIL="noreply@fixture-restricted.invalid")
        result = self.scan(sha)
        self.assertEqual(result.returncode, 1)
        for field in ("author name", "author email", "committer name", "committer email", "commit message"):
            self.assertIn(f"Restricted {field}", result.stdout)
        self.assertNotIn("fixture-restricted", (result.stdout + result.stderr).lower())

    def test_comments_whitespace_case_and_crlf(self):
        file = self.repo / "file"
        file.write_text("# comment")
        patterns = "  # comment\r\n\r\n  Fixture-Restricted \r\n"
        self.assertEqual(self.scan(self.commit("Comment only"), patterns).returncode, 0)
        file.write_text("fIXTURE-rESTRICTED")
        self.assertEqual(self.scan(self.commit("Add restricted content"), patterns).returncode, 1)

    def test_empty_inventory_missing_object_and_failed_attribution_fail_closed(self):
        sha = self.commit("Valid")
        self.assertEqual(self.scan(sha, " # comment\r\n ").returncode, 1)
        missing = self.scan(sha, BASE_SHA="a" * 40)
        self.assertEqual(missing.returncode, 1)
        self.assertIn("could not verify", missing.stderr)
        (self.root / "gh").write_text("#!/bin/sh\necho fixture-restricted >&2\nexit 1\n")
        failed = self.scan(sha)
        self.assertEqual(failed.returncode, 1)
        self.assertNotIn("fixture-restricted", failed.stderr)


if __name__ == "__main__":
    unittest.main()
