<#
.SYNOPSIS
    Build the Pinscreen 2 Windows installer (Inno Setup).

.DESCRIPTION
    Publishes Pinscreen2.App self-contained for win-x64 (which also publishes
    Pinscreen2.Updater into the same folder via the AfterTargets hook), then
    runs ISCC.exe against installer/pinscreen2.iss to produce a single .exe
    setup. The output lands in installer/Output/Pinscreen2_Setup_v<ver>.exe.

.PARAMETER Version
    Version stamped into the installer (e.g. 1.6.0). Defaults to "0.0.0".

.PARAMETER InnoSetupPath
    Optional explicit path to ISCC.exe. Auto-detected if omitted.

.PARAMETER SkipPublish
    Skip the dotnet publish step and reuse the existing publish folder.
#>
param(
    [string]$Version = "0.0.0",
    [string]$InnoSetupPath = "",
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$ScriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir = Split-Path -Parent $ScriptDir
$PublishDir = Join-Path $ProjectDir "Pinscreen2.App\bin\Release\net9.0\win-x64\publish"

# Locate ISCC.exe
if (-not $InnoSetupPath) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { $InnoSetupPath = $c; break } }
}
if (-not $InnoSetupPath -or -not (Test-Path $InnoSetupPath)) {
    Write-Error "Inno Setup compiler (ISCC.exe) not found. Install Inno Setup 6 or pass -InnoSetupPath."
    exit 1
}
Write-Host "Using Inno Setup: $InnoSetupPath" -ForegroundColor Cyan

# Ensure icon exists; generate if missing
$IconPath = Join-Path $ProjectDir "Pinscreen2.App\Assets\icon.ico"
if (-not (Test-Path $IconPath)) {
    Write-Host "Icon missing -- generating..." -ForegroundColor Yellow
    & python (Join-Path $ScriptDir "generate_icon.py")
    if ($LASTEXITCODE -ne 0) { Write-Error "Icon generation failed."; exit 1 }
}

if (-not $SkipPublish) {
    Write-Host "Publishing Pinscreen2.App for win-x64..." -ForegroundColor Cyan
    & dotnet publish (Join-Path $ProjectDir "Pinscreen2.App\Pinscreen2.App.csproj") `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=false -p:PublishReadyToRun=true `
        -p:Version=$Version -p:AssemblyVersion=$Version -p:FileVersion=$Version -p:InformationalVersion=$Version
    if ($LASTEXITCODE -ne 0) { Write-Error "App publish failed."; exit 1 }
}

if (-not (Test-Path $PublishDir)) {
    Write-Error "Publish directory not found: $PublishDir"
    exit 1
}

Write-Host "Compiling installer (v$Version)..." -ForegroundColor Cyan
$IssFile = Join-Path $ScriptDir "pinscreen2.iss"
& $InnoSetupPath /Qp "/DAppVersion=$Version" "/DProjectDir=$ProjectDir" "/DPublishDir=$PublishDir" $IssFile
if ($LASTEXITCODE -ne 0) { Write-Error "Inno Setup compilation failed (exit $LASTEXITCODE)"; exit 1 }

$OutputDir = Join-Path $ScriptDir "Output"
Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Build successful!" -ForegroundColor Green
Write-Host "  Output: $OutputDir" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
