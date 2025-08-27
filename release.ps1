param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$VersionTag,
    [string[]]$Runtimes = @('win-x64','osx-arm64','linux-x64'),
    [string]$Notes = ''
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
  Write-Error "GitHub CLI (gh) not found. Install and run 'gh auth login' first."
  exit 1
}

if ([string]::IsNullOrWhiteSpace($Notes)) { $Notes = "Release $VersionTag" }

Write-Host "Releasing $VersionTag for: $($Runtimes -join ', ')" -ForegroundColor Cyan

foreach ($rid in $Runtimes) {
  Write-Host "-- Runtime: $rid" -ForegroundColor Yellow
  & ./version.ps1 -VersionTag $VersionTag -Runtime $rid -Notes $Notes
  if ($LASTEXITCODE -ne 0) {
    Write-Error "version.ps1 failed for $rid"
    exit 1
  }
}

Write-Host "Release $VersionTag complete for all runtimes." -ForegroundColor Green


