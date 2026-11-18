---
name: release
description: Prepare or run a coordinated messagebus.ai client package release when the release owner authorizes publication.
---

# Release mbus

Read [the client release runbook](../../../docs/client-release.md) first.

1. Identify the normalized version and full SHA of the merged main commit.
   Confirm its latest post-merge Build and `org-strings` status succeeded.
   For a fresh run, the version must advance all eight packages.
2. A release request authorizes its specified version and target. Obtain
   separate authorization for public visibility and first publication
   at the checkpoints in the runbook.
3. Confirm the repository is public, Trusted Publishing is configured for
   the protected `nuget` environment, and publishing is enabled. Dispatch
   `release-mbus.yml` on main with the version. It uses the commit at dispatch.
   To pin a particular commit, pass its full SHA as `tagged_commit` or use
   the `mbus-v<version>` tag trigger at that SHA.
4. Report the selected commit and monitor the release.
5. Verify eight package versions on nuget.org and all 15 GitHub Release
   assets. For a partial result, preserve the verified artifact. After
   confirming current authorization for the same version and commit, rerun
   only the failed jobs in the original workflow run.
