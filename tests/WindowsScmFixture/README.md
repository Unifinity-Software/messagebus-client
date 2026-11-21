# Windows service acceptance fixture

Use an approved disposable Windows VM with a machine-wide .NET 10 SDK in Program Files.
Run elevated PowerShell from a trusted checkout, outside the messagebus runner. Before each
fresh run, confirm that all three checks print `True`. If any check prints `False`, use a
clean VM snapshot.

```powershell
$null -eq (Get-Service mbus-runner -ErrorAction SilentlyContinue)
-not (Test-Path -LiteralPath "$env:ProgramFiles\MessageBus.Runner")
-not (Test-Path -LiteralPath "$env:ProgramData\MessageBus.Runner")
```

Build and pack both synthetic versions before the offline exercise. The feed and synthetic enrollment stay local during the exercise. Use an installing administrator
with an existing local Windows profile. The fixture creates a randomly named empty file in
that profile, with a protected ACL granting access only to the installing user. It deletes
that file during cleanup or before handing the VM back for reboot.

```powershell
$feed = 'out/windows-scm-fixture-feed'
$fixture = 'tests/WindowsScmFixture/bin/Release/net10.0/publish/mbus.ScmFixture.dll'
dotnet pack tests/WindowsScmFixture -c Release -p:ScmFixturePack=true -p:PackageVersion=0.0.1-scm-fixture.1 -o $feed
dotnet pack tests/WindowsScmFixture -c Release -p:ScmFixturePack=true -p:PackageVersion=0.0.1-scm-fixture.2 -o $feed
```

## Successful update and rollback

```powershell
dotnet $fixture --run-disposable-scm-fixture $feed
dotnet $fixture --run-disposable-scm-rollback $feed
```

Run one command at a time and require exit code 0 before proceeding. Both runs must print
`Passed virtual-account access check (AccessDenied) for the synthetic profile file.`
This assertion uses the virtual-account worker's actual file open. Only Windows error
`0x80070005` passes.

The default run must print `Passed SCM boot, protected control requests and DPAPI, drain, unattended offline update, and SCM stop.`
The rollback run enables deliberate failure in the installed candidate's `--version` check. It
must print `Passed offline rollback (RolledBack). Candidate start failed, version 0.0.1-scm-fixture.1 resumed under the same SCM host, and SCM stop reached EOF.`
The fixture checks the terminal update attempt, the failed start diagnostic, one recovery attempt,
and the restored package version. Recovery uses the production applier's saved, checksum-verified
package and local recovery configuration. The test substitutes its local feed only for the exact
forward candidate command.

Both modes uninstall in a bounded cleanup and verify that the service and both installation
directories are gone. If cleanup fails, discard or revert the disposable VM.

## Reboot and automatic start without sign-in

Run this in a fresh VM with the same preconditions and packages. Disable automatic sign-in.
Arrange a headless way to run elevated commands and collect output after reboot, such as the
VM's management channel or a startup task running as SYSTEM. Use an absolute, administrator-owned
path to the checkout or a copy of the fixture publish directory for the headless commands.

1. Prepare the reboot fixture and record the current boot time and service configuration.

   ```powershell
   dotnet $fixture --prepare-disposable-scm-reboot $feed
   (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
   sc.exe qc mbus-runner
   sc.exe queryex mbus-runner
   ```

   Expect exit code 0, the successful update and AccessDenied messages, and
   `Prepared reboot fixture. SCM service is stopped and the confirmed candidate is retained.`
   Expect `AUTO_START (DELAYED)`, `NT SERVICE\mbus-runner`, and `STOPPED` in the SCM output.

2. Restart the VM through the operator or management channel. Leave the Windows sign-in screen
   untouched. Run the following commands headlessly before the first interactive sign-in.
   Allow up to five minutes for delayed automatic start. Only SCM should start the service.

   ```powershell
   (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
   dotnet $fixture --check-disposable-scm-reboot
   sc.exe queryex mbus-runner
   ```

   Save the headless transcript. The boot time must advance from step 1. Expect exit code 0,
   `Passed post-reboot worker check. SCM PID <pid>, candidate 0.0.1-scm-fixture.2, DPAPI reads succeeded.`
   and `RUNNING` with the same PID in the SCM output. The check waits for a fresh worker marker
   written after both DPAPI reads. The operator's boot timestamps and headless transcript establish
   the reboot and timing relative to interactive sign-in.

3. Uninstall through the cleanup stage, even if post-reboot verification fails.

   ```powershell
   dotnet $fixture --cleanup-disposable-scm-reboot
   ```

   Expect exit code 0 and `Removed the fixture SCM registration and both installation directories.`
   Repeat the three precondition checks to capture the absence of the installation.

The normal solution pack excludes the fixture. Signed NuGet download and verification, real server
enrollment, and real task drain and connection still require a signed end-to-end acceptance run.
