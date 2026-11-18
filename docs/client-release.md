# Client releases

Build checks pull requests and pushes to main. The manual Build run also offers
Windows and macOS. The Linux run builds the runner image's build stage.
Each Build packs the eight NuGet packages at `0.0.0-ci.1`, checks their metadata,
and installs and runs the tool from an isolated local feed. It also checks the
public Contracts surface and scans for credentials.

## Set up GitHub

- Require the `Build` check and the `org-strings` commit
  status before merging to main.
- Create the `org-strings` environment and allow deployments from the main
  **branch** only. Store `ORG_STRINGS` as an environment secret with one literal
  per line. The scanner trims each line, ignores blank lines and comments that
  start with `#`, and matches ASCII letters case-insensitively. Set the repository variable `MBUS_MAINTAINERS` to the space-separated GitHub logins
  authorized to dispatch the scanner. The scanner also uses the list to check
  GitHub-attributed commit authors and committers for noreply addresses.
- From main, dispatch **Organization strings** with a pull request number.
  It scans the exact head SHA. Dispatch again when the head changes. Before
  making the check required, verify on a canary PR that a synthetic restricted
  string produces a failing status without including the matched literal or
  filename in diagnostics. The scan covers the tip tree, newly reachable
  history and commit identities and messages.
- Create the `nuget` environment with deployment restricted to the main
  **branch** and add the release owner as a required reviewer. For a sole
  owner, allow that reviewer to approve their own workflow dispatch. Store
  `NUGET_USER` as a secret in that environment, with the nuget.org profile
  name that can publish all eight packages.
- Protect `mbus-v*` tags against updates and deletion. Manual releases need
  permission for the release workflow to create the tag. Limit tag creation to
  the release workflow and authorized maintainers when configuring rules.

## Enable publishing

Perform these steps in order.

1. Make the GitHub repository public after the owner approves the visibility change.
2. Create the [nuget.org Trusted Publishing policy](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
   for this repository, workflow file `release-mbus.yml` and environment
   `nuget`. Ensure the account can publish all eight IDs. A policy created for
   a private repository can lapse after seven days without a successful publish.
3. Confirm the owner authorizes first publication for the exact version and
   full commit SHA. Then set `MBUS_RELEASE_ENABLED` to `true`. Keep it unset
   until then.

## Publish

Wait for a successful latest post-merge Build and `org-strings` status on
the exact release commit. Choose a normalized SemVer version whose numeric core is at least `MbusSourceVersion`
and whose precedence is newer than every published version of all eight
packages. For example, `0.2.0-preview.1` is compatible with a `0.2.0` source
baseline and sorts after `0.1.2-preview.2`.

Obtain the release owner’s current authorization for that exact version and
full merged commit SHA. Run **Release mbus** from main with the version and
pass the SHA as `tagged_commit`. Alternatively, push `mbus-v<version>` at that
commit. The tag trigger dispatches the release on main so the environment's branch
restriction applies. The workflow packs and verifies the packages and builds
the complete runner image before requesting approval. It uses GitHub OIDC
and a short-lived nuget.org credential for publishing.

The publish job rechecks Build and the disclosure status after review. It
uploads packages in dependency order and the mbus tool last. GitHub stages
a draft, verifies the eight `.nupkg` and seven `.snupkg` assets by SHA-256,
and publishes the completed release. A prerelease version produces a GitHub
prerelease.

## Recover a partial release

Preserve the original run and its verified artifact. Confirm the release
owner currently authorizes resuming that exact version and commit. Use
**Re-run failed jobs** in the original workflow run. This retains the
immutable artifact ID from the successful prepare job. A fresh dispatch or
**Re-run all jobs** applies the fresh version guard.

If NuGet publication is retried, the protected `nuget` review still applies.
For each already-published package, the job verifies its NuGet signature and
compares every uncompressed archive entry against the original candidate,
excluding only NuGet’s root `.signature.p7s` entry. It stops before any uploads
on a mismatch. It uploads missing packages, verifies the indexed payloads,
and retries their corresponding symbols from the same artifact.

If the GitHub Release job is retried, it verifies the tag’s commit, reuses
the draft, and uploads missing assets. A complete published release is
verified as-is. If an existing asset differs or an interrupted upload left
an unverified asset, report the mismatch to the owner for review. The
workflow preserves existing assets.
