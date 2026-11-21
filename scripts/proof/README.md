Linux Node import and return proof (#571)
=======================================

Run this only outside an mbus task sandbox, in a disposable Linux container. It
does not enroll a runner, use machine keys, publish packages or contact a live Git
remote. Runtime enrollment remains gated by shared readiness (#496).

Prepare an image from the coordinator's cached Node 24 and SDK 10 images with
Git, non-setuid bubblewrap (supporting `--disable-userns`), Python 3, OpenSSL,
ca-certificates, coreutils, util-linux and shadow/useradd installed. Node and npm
must be at `/usr/bin/node` and `/usr/bin/npm`; a Node installation only under
`/usr/local` is insufficient because those files are intentionally not mounted by
the verifier. npm's libraries must be under `/usr/lib` or `/usr/share/nodejs`.
The launcher checks versions and the test calls the real boundary probe. Use
preinstalled tools and an offline, populated NuGet cache; the launcher installs
nothing and caps restore/build/test at ten minutes.

An offline image build template is included. In a separate run-owned build
context, copy `Containerfile` and provide `offline-debs/` containing the complete
Debian package dependency set matching the SDK image for bubblewrap, Git, Python 3, OpenSSL,
ca-certificates, util-linux and passwd (including their dependencies). With both
base images already cached, build without network or pulls:

```sh
timeout 300 podman build --pull=never --network none \
  --build-arg SDK_IMAGE=local/cached-sdk10 \
  --build-arg NODE_IMAGE=local/cached-node24 \
  -t local/mbus-node24-sdk10-bwrap -f Containerfile .
```

Set `NODE_IMAGE` and `SDK_IMAGE` to the coordinator's cached, Debian-compatible
image names. The offline build deliberately fails on missing packages;
there is no automatic network fallback. The template is provided for coordinator
execution and has not been built on the macOS worker.

From the coordinator checkout, for example (replace the local image/cache names):

```sh
podman run --name mbus-571-proof --network none --user 0 \
  --security-opt seccomp=unconfined --security-opt unmask=ALL --cap-add SYS_ADMIN \
  -v "$PWD:/source:ro" -v "$NUGET_PACKAGES:/packages:ro" \
  -e NUGET_PACKAGES=/packages local/mbus-node24-sdk10-bwrap \
  bash /source/scripts/proof/node-import.sh
```

The additional container permissions permit nested unprivileged bubblewrap.
Default OCI read-only system paths refuse `--disable-userns`; the proof requires
`unmask=ALL` only on its disposable outer container. These flags must never be
copied into production child launch or interpreted as default ACI eligibility.
The inner check must have zero effective capabilities and cannot connect to the
outer fixture listener. No host sysctl is changed.

For this fixture only,
use no host home, socket, credentials or other repository mounts. The container
has no external network, only its loopback HTTPS fixture. The copied source,
generated TLS key/certificate, dummy Basic authorization, served repositories,
sentinels, logs and build output all live in the printed `/tmp/mbus-node-proof.*`
root. Only the disposable container's CA store, hosts file and fixture user are
also changed. Do not run the setup script on a real host. Use a unique container
name for concurrent proofs.

Success requires both test cases, `return.passed`, `alternates.passed`, and the
final `MBUS_LINUX_IMPORT_RETURN_PROOF_PASSED cases=2` marker. An unavailable
boundary, skipped test, failed restore, wrong tool version or missing marker
fails the launcher. If the read-only cache lacks an SDK package, supply an
operator-prepared directory of exact `.nupkg` files through `MBUS_PROOF_FEED`.
The launcher copies these into its disposable feed and writes restored packages
only beneath its printed proof root, using the mounted cache as read-only fallback. Ordinary test suites skip these two cases without the
fixture variable; those skips are not Linux proof.

The positive case fetches from authenticated fixture HTTPS using real Git,
prepares through `NodeVerification`, commits an agent change, imports through
its real isolated bundle export/import, restores and checks with real npm and
bubblewrap. Check code attempts actual reads of credential/control sentinels
outside its mounts. Hostile agent metadata includes an external malformed Git
config include (import would fail if exposed), fsmonitor and hook paths. The
negative case puts all objects outside the agent metadata and confirms import
is blocked despite those objects being readable on the fixture host. The real
return path independently rejects both hostile agent repositories before any
HTTP or key lookup. The real
`RunCodeReturn` uses fake HTTP/MCP only at the service boundary; the helper
decrypts uploaded bytes, checks the bundle's advertised head against the checked
revision, and verifies that a moved branch cannot upload again.

Collect logs with `podman cp mbus-571-proof:/tmp/<printed-root> ./proof-results`.
Leave these paths to the runner's cleanup policy, or remove the disposable
container through the coordinator's normal container cleanup. The script never
recursively deletes caches or build directories. No production protocol or
customer-facing behavior changes.
