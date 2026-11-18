---
name: release
description: Prepare or run a coordinated messagebus.ai client package release when the release owner authorizes publication.
---

# Release mbus

Read [the client release runbook](../../../docs/client-release.md) first.

1. Confirm the release owner authorized the version and publication. Verify
   the repository is public and Trusted Publishing is configured for the
   protected `nuget` environment.
2. Find the exact merged main commit. Confirm its latest post-merge Build
   succeeded and choose a version newer than every published client package.
3. Dispatch `release-mbus.yml` from main with that version or use its
   `mbus-v<version>` tag trigger for a specific eligible commit.
4. Report the candidate commit and verification results to the release owner.
   The environment reviewer handles the approval in GitHub.
5. Follow the run and verify the eight package versions on nuget.org and all
   15 assets on the GitHub Release. Report any partial result and preserve
   the verified workflow artifact for recovery.
