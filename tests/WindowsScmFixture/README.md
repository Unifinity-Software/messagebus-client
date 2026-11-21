# Windows service acceptance fixture

Run this from an elevated PowerShell terminal in an approved disposable Windows VM, outside the
messagebus runner. Use a trusted checkout built by the administrator, a machine-wide .NET 10 SDK
in Program Files, and a VM where the fixed `mbus-runner` service and both `MessageBus.Runner`
installation directories are unused. Build first so the NuGet dependency cache is available.
The fixture uses the supplied local packages and synthetic enrollment values.

```powershell
dotnet pack tests/WindowsScmFixture -c Release -p:ScmFixturePack=true -p:PackageVersion=0.0.1-scm-fixture.1 -o out/windows-scm-fixture-feed
dotnet pack tests/WindowsScmFixture -c Release -p:ScmFixturePack=true -p:PackageVersion=0.0.1-scm-fixture.2 -o out/windows-scm-fixture-feed
dotnet tests/WindowsScmFixture/bin/Release/net10.0/publish/mbus.ScmFixture.dll --run-disposable-scm-fixture out/windows-scm-fixture-feed
```

Keep the two dummy packages local. The normal solution pack excludes the fixture. The fixture
copies its dispatcher, exact registration, offline configuration, and packages into the
administrator-owned host tree. It then registers the real virtual-account service and verifies the
SCM-owned pipe, DPAPI reads in the service and child worker, update after the synthetic session drains,
candidate confirmation using package identity, and EOF on SCM stop. The actual service supervisor
runs both workers and the applier. The fixture substitutes a local feed only for the exact forward
candidate command. The synthetic worker supplies the drain and connection signals.

The exercise has a four-minute deadline and a separate bounded cleanup. Cleanup uses the service
uninstaller and checks that the service registration and both directories are gone. If cleanup
fails, discard or revert the disposable VM. Signed NuGet download and verification, real server
enrollment, and real task drain and connection still require a signed end-to-end acceptance run.
