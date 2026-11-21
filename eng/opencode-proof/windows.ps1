param([Parameter(Mandatory=$true)][string]$Scratch, [string]$Python = 'python')
$ErrorActionPreference = 'Stop'
# Run only in a native coordinator context authorized to run the entire suite.
if ($env:MBUS_RUN_TOOLS) { throw 'Run from a native coordinator outside a task; do not suppress InsideRun.' }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
& $Python (Join-Path $PSScriptRoot 'prepare.py') --platform windows-x64 --scratch $Scratch
if ($LASTEXITCODE -ne 0) { throw 'Package verification failed; no binary may execute' }
$manifest = Get-Content -LiteralPath (Join-Path $Scratch 'manifest.json') -Raw | ConvertFrom-Json
$names = @('MBUS_TEST_OPENCODE_EXE','MBUS_TEST_OPENCODE_SHA256','MBUS_TEST_OPENCODE_EVIDENCE','MBUS_TEST_PROOF_PYTHON','MBUS_TEST_LMSTUDIO_MODEL','MBUS_TEST_LMSTUDIO_URL')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:MBUS_TEST_OPENCODE_EXE = $manifest.executable
    $env:MBUS_TEST_OPENCODE_SHA256 = $manifest.binarySha256
    $env:MBUS_TEST_OPENCODE_EVIDENCE = $manifest.evidence
    $env:MBUS_TEST_PROOF_PYTHON = $manifest.python
    # This packet is the fake-provider proof; real LM Studio trials are a separate explicit invocation.
    Remove-Item Env:MBUS_TEST_LMSTUDIO_MODEL -ErrorAction SilentlyContinue
    Remove-Item Env:MBUS_TEST_LMSTUDIO_URL -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $manifest.evidence -Force | Out-Null
    Push-Location $repo
    try {
        & dotnet build MessageBus.Client.slnx -m:1 --disable-build-servers '-p:UseSharedCompilation=false' 2>&1 | Tee-Object -FilePath (Join-Path $manifest.evidence 'build.log')
        if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
        & dotnet test --solution MessageBus.Client.slnx --no-build 2>&1 | Tee-Object -FilePath (Join-Path $manifest.evidence 'suite.log')
        if ($LASTEXITCODE -ne 0) { throw 'Full client suite failed' }
        & $Python eng/check-public-contracts.py
        if ($LASTEXITCODE -ne 0) { throw 'Contract check failed' }
        & git diff --check
        if ($LASTEXITCODE -ne 0) { throw 'Whitespace check failed' }
    } finally { Pop-Location }
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
# Keep evidence for review. Run cleanup.ps1 explicitly after preserving it.
