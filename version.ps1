param(
    [Parameter(Mandatory=$true, Position=0)]
    [string]$VersionTag,
    [Parameter(Position=1)]
    [ValidateSet('win-x64','osx-arm64','osx-x64','linux-x64')]
    [string]$Runtime = 'win-x64',
    [string]$Notes = ''
)

function Assert-Success($Message) {
    if ($LASTEXITCODE -ne 0) { Write-Error $Message; exit 1 }
}

if ([string]::IsNullOrWhiteSpace($Notes)) { $Notes = "Release $VersionTag" }

Write-Host "Preparing release $VersionTag for $Runtime" -ForegroundColor Cyan

# Ensure gh is available
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Error "GitHub CLI (gh) not found. Install via winget/choco or https://github.com/cli/cli/releases"
    exit 1
}

# Tag if needed
$tagExists = (& git tag -l $VersionTag) -ne $null
if ($tagExists) {
    Write-Host "Tag $VersionTag already exists. Skipping tagging." -ForegroundColor Yellow
} else {
    git tag -a $VersionTag -m "Pinscreen 2 $VersionTag"
    Assert-Success "git tag failed"
    git push origin $VersionTag
    Assert-Success "git push failed"
}

# Paths
$publishDir = Join-Path -Path (Get-Location) -ChildPath "publish\$Runtime"
Remove-Item -Recurse -Force $publishDir -ErrorAction Ignore | Out-Null
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

# Build app publish
dotnet publish .\Pinscreen2.App\Pinscreen2.App.csproj -c Release -r $Runtime --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false `
  -o $publishDir
Assert-Success "dotnet publish failed"

# Build updater
dotnet build .\Pinscreen2.Updater\Pinscreen2.Updater.csproj -c Release
Assert-Success "dotnet build updater failed"

# Copy updater next to app
$updaterName = if ($Runtime -like 'win-*') { 'Pinscreen2.Updater.exe' } else { 'Pinscreen2.Updater' }
$updaterSrc = Join-Path -Path ".\Pinscreen2.Updater\bin\Release\net9.0" -ChildPath $updaterName
if (-not (Test-Path $updaterSrc)) {
    Write-Error "Updater binary not found at $updaterSrc"
    exit 1
}
Copy-Item $updaterSrc -Destination (Join-Path $publishDir $updaterName) -Force

# Zip
$zipName = "Pinscreen2-$Runtime.zip"
if (Test-Path $zipName) { Remove-Item $zipName -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipName -Force
Assert-Success "Compress-Archive failed"

# Create (or update) GitHub release
Write-Host "Creating GitHub release $VersionTag" -ForegroundColor Cyan
& gh release create $VersionTag $zipName --title "Pinscreen 2 $VersionTag" --notes $Notes
if ($LASTEXITCODE -ne 0) {
    Write-Host "Release may already exist. Attempting to upload asset..." -ForegroundColor Yellow
    & gh release upload $VersionTag $zipName --clobber
    Assert-Success "gh release upload failed"
}

Write-Host "Release complete: $VersionTag ($Runtime)" -ForegroundColor Green


