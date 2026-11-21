param([Parameter(Mandatory=$true)][string]$Scratch)
$ErrorActionPreference = 'Stop'
$target = (Resolve-Path -LiteralPath $Scratch).Path
$marker = Join-Path $target '.opencode-proof-owned'
if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'Missing ownership marker' }
$recorded = [IO.Path]::GetFullPath((Get-Content -LiteralPath $marker -Raw).Trim())
if ($target -ne $recorded -or $target -eq [IO.Path]::GetPathRoot($target)) { throw 'Cleanup target mismatch' }
# Exact directory explicitly supplied by the native coordinator; no computed parent cleanup.
Write-Output "Removing verified task-owned proof directory: $target"
Remove-Item -LiteralPath $target -Recurse -Force
if (Test-Path -LiteralPath $target) { throw 'Proof cleanup incomplete' }
