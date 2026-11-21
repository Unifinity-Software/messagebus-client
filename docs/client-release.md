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
  **branch**. Publishing runs automatically after the release checks. Store
  `NUGET_USER` as a secret in that environment, with the nuget.org profile
  name that can publish all eight packages.
- Create the `ghcr` environment with deployment restricted to the protected
  main **branch**. Allow the repository's GitHub Actions token to create and
  write the organization package `ghcr.io/unifinity-software/messagebus-runner`.
  If the package already exists, link it to this repository and grant this
  repository write access under **Manage Actions access**. Reserve versioned
  tag writes for the release workflow.
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
4. On first publication, the protected promotion job may create a private GHCR
   package and then stop at the anonymous pull check. With owner approval,
   change the package visibility to **Public** in package settings. Then use
   **Re-run failed jobs** on the original run. Promotion verifies the existing
   versioned tag and pulls the same OCI digest anonymously before allowing any
   NuGet uploads.

## Publish

Wait for a successful latest post-merge Build and `org-strings` status on
the exact release commit. Choose a normalized SemVer version whose numeric core is at least `MbusSourceVersion`
and whose precedence is newer than every published version of all eight
packages. For example, `0.2.0-preview.1` is compatible with a `0.2.0` source
baseline and sorts after `0.1.2-preview.2`.

Run **Release mbus** from main with the version. The workflow captures the
main commit at dispatch, checks it, and releases that commit. To select an
earlier merged commit, pass its full SHA as `tagged_commit` or push
`mbus-v<version>` at that commit. The tag trigger dispatches the release on
main so the environment's branch restriction applies. The workflow packs
and verifies the packages and builds a Linux amd64 OCI image once from the
exact release commit. Preparation verifies the image's source provenance,
non-root configuration and all five CLI versions. The scan reports high and
critical vulnerabilities, including those without an upstream fix. It blocks
publication on secret findings and high or critical findings with a fix.
The protected `ghcr` job promotes the exact
OCI index by digest and verifies a full anonymous pull before NuGet publishing
can use GitHub OIDC and a short-lived nuget.org credential.

The ACI template defaults to the first official runner image, `1.3.0`.
For controlled upgrades, use the digest from the newer release notes and keep
the existing runner name and identity storage.

The publish job rechecks Build and the disclosure status before publishing. It
submits the libraries in dependency order so NuGet can index them together.
It verifies their signatures and payloads before uploading the mbus tool.
GitHub stages a draft, verifies the eight `.nupkg` and seven `.snupkg` assets by SHA-256,
and publishes the completed release with the image digest, source commit and
bundled CLI versions in the notes. A prerelease version produces a GitHub
prerelease.

## Recover a partial release

Preserve the original run and its verified artifact. Confirm the release
owner currently authorizes resuming that exact version and commit. Use
**Re-run failed jobs** in the original workflow run. This retains the
immutable package and OCI artifact IDs from the successful prepare job.
An existing image tag can be reused only if its digest matches that candidate.
If the digest differs, stop and report the mismatch to the release owner.
A fresh dispatch or **Re-run all jobs** builds a new candidate and applies the
fresh version guard. If the original artifact is gone, coordinate a new version
with the owner.

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
