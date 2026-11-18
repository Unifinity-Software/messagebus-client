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
  **branch** only. Store `ORG_STRINGS` there as one literal per line. Set the
  repository variable `MBUS_MAINTAINERS` to the space-separated GitHub logins
  authorized to dispatch the scanner. The scanner also uses the list to check
  GitHub-attributed commit authors and committers for noreply addresses.
- From main, dispatch **Organization strings** with a pull request number.
  It scans the exact head SHA. Dispatch again when the head changes. Before
  making the check required, verify on a canary PR that a synthetic restricted
  string produces a failing status and diagnostics with only paths and line
  numbers. A filename containing a restricted value is redacted.
- Create the `nuget` environment with deployment restricted to the main
  **branch** and add the release owner as a required reviewer. Disable
  self-review and administrative bypass where available. Set `NUGET_USER` in
  that environment to the nuget.org profile name that can publish all eight
  packages.
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
3. Set `MBUS_RELEASE_ENABLED` to `true` when the owner authorizes publication.
   Keep it unset until then.

## Publish

Wait for the post-merge Build on the exact release commit to pass. Choose one
normalized SemVer version whose numeric core is at least `MbusSourceVersion`
and whose precedence is newer than every published version of all eight
packages. For example, `0.2.0-preview.1` is compatible with a `0.2.0` source
baseline and sorts after `0.1.2-preview.2`.

Run **Release mbus** from main with the version and leave `tagged_commit`
empty. Alternatively, push `mbus-v<version>` at the selected merged commit.
The tag trigger dispatches the release on main so the environment's branch
restriction applies. The workflow packs and verifies the packages and builds
the complete runner image before requesting approval. It uses GitHub OIDC
and a short-lived nuget.org credential for publishing.

The GitHub Release attaches eight `.nupkg` and seven `.snupkg` archives. A
prerelease version produces a GitHub prerelease. The runner image build is a
check. If publishing fails after some packages are accepted, retain the
verified artifact and inspect nuget.org before attempting recovery. The
normal release guard requires a version newer than any already published.
