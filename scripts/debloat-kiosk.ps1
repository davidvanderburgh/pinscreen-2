<#
.SYNOPSIS
  Pinscreen kiosk debloat: disable/uninstall Windows components a kiosk
  display device does not need.

.DESCRIPTION
  Run this on the pinscreen device in an elevated PowerShell. It is idempotent
  -- re-running it is safe. Each section is gated by a switch parameter so you
  can enable only the changes you want. By default, the safe sections run and
  the riskier ones (Defender, OneDrive uninstall) require explicit opt-in.

  The pinscreen is a kiosk: no user accounts logging in interactively, no
  productivity apps, no peripherals beyond the display. So we can be much more
  aggressive than on a normal PC.

.PARAMETER All
  Run every section, including -DisableDefender and -RemoveOneDrive. Use only
  if you've read what those do.

.PARAMETER DisableTelemetry
  Disable Compatibility Appraiser + DiagTrack + related scheduled tasks. (default on)

.PARAMETER DisableSearchIndexer
  Stop and disable Windows Search (WSearch). The pinscreen never searches files. (default on)

.PARAMETER DisableSuperfetch
  Stop and disable SysMain. Helps SSD wear, near-zero benefit on kiosk. (default on)

.PARAMETER DisableXbox
  Disable Xbox services + scheduled tasks. (default on)

.PARAMETER DisablePrintSpooler
  Stop and disable Print Spooler. Skip if you actually print from this box. (default on)

.PARAMETER DisableBackgroundApps
  Block UWP apps from running in background. (default on)

.PARAMETER RemoveStoreApps
  Remove preinstalled Microsoft Store / UWP bloat (Solitaire, Bing News, etc).
  Keeps Calculator, Photos, and the Store itself. (default on)

.PARAMETER DisableCortana
  Disable Cortana. (default on)

.PARAMETER DisableStartupApps
  Empty the per-user Run keys + common startup folders. (default on)

.PARAMETER DisableDefender
  Disable Microsoft Defender real-time scanning. REQUIRES Tamper Protection
  to be turned off first (Settings -> Privacy & security -> Windows Security
  -> Virus & threat protection -> Manage settings -> Tamper Protection: Off).
  Without that, Defender will silently re-enable itself. Off by default.

.PARAMETER RemoveOneDrive
  Uninstall OneDrive system-wide and remove its leftover folders/registry
  shortcuts. Off by default.

.PARAMETER DryRun
  Print what would be changed without changing anything.

.EXAMPLE
  pwsh -File .\debloat-kiosk.ps1 -DryRun

.EXAMPLE
  pwsh -File .\debloat-kiosk.ps1 -All

.NOTES
  Reverse most changes by re-enabling the relevant service / scheduled task
  via Services.msc / Task Scheduler. Removed UWP apps can be reinstalled from
  the Microsoft Store. OneDrive can be reinstalled from microsoft.com/onedrive.
  Defender real-time can be re-enabled via Settings or Set-MpPreference.
#>

[CmdletBinding()]
param(
    [switch]$All,
    [switch]$DryRun,

    [bool]$DisableTelemetry      = $true,
    [bool]$DisableSearchIndexer  = $true,
    [bool]$DisableSuperfetch     = $true,
    [bool]$DisableXbox           = $true,
    [bool]$DisablePrintSpooler   = $true,
    [bool]$DisableBackgroundApps = $true,
    [bool]$RemoveStoreApps       = $true,
    [bool]$DisableCortana        = $true,
    [bool]$DisableStartupApps    = $true,

    [switch]$DisableDefender,
    [switch]$RemoveOneDrive
)

if ($All) {
    $DisableDefender = $true
    $RemoveOneDrive  = $true
}

# Require admin
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "Run this script in an elevated PowerShell (Run as Administrator)."
    exit 1
}

$ErrorActionPreference = 'Continue'
$WhatIfPreference = [bool]$DryRun

function Section($name) {
    Write-Host ""
    Write-Host "== $name ==" -ForegroundColor Cyan
}

function Disable-Svc($name) {
    $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
    if (-not $svc) { Write-Host "  [skip] $name (not present)"; return }
    if ($DryRun) { Write-Host "  [dry] would stop+disable $name"; return }
    try {
        if ($svc.Status -ne 'Stopped') { Stop-Service -Name $name -Force -ErrorAction SilentlyContinue }
        Set-Service -Name $name -StartupType Disabled -ErrorAction SilentlyContinue
        Write-Host "  [done] $name disabled"
    } catch { Write-Warning "  [fail] $name -> $_" }
}

function Disable-Task($path, $names) {
    foreach ($n in $names) {
        $t = Get-ScheduledTask -TaskPath $path -TaskName $n -ErrorAction SilentlyContinue
        if (-not $t) { continue }
        if ($DryRun) { Write-Host "  [dry] would disable task $path$n"; continue }
        try {
            Disable-ScheduledTask -TaskPath $path -TaskName $n -ErrorAction Stop | Out-Null
            Write-Host "  [done] task disabled: $path$n"
        } catch { Write-Warning "  [fail] $path$n -> $_" }
    }
}

function Set-Reg($path, $name, $value, $type='DWord') {
    if ($DryRun) { Write-Host "  [dry] would set $path::$name = $value"; return }
    try {
        if (-not (Test-Path $path)) { New-Item -Path $path -Force | Out-Null }
        New-ItemProperty -Path $path -Name $name -Value $value -PropertyType $type -Force | Out-Null
        Write-Host "  [done] $path::$name = $value"
    } catch { Write-Warning "  [fail] $path::$name -> $_" }
}

if ($DisableTelemetry) {
    Section "Telemetry & Compatibility Appraiser"
    Disable-Svc 'DiagTrack'
    Disable-Svc 'dmwappushservice'
    Disable-Task '\Microsoft\Windows\Application Experience\' @(
        'Microsoft Compatibility Appraiser',
        'ProgramDataUpdater',
        'StartupAppTask',
        'PcaPatchDbTask'
    )
    Disable-Task '\Microsoft\Windows\Customer Experience Improvement Program\' @(
        'Consolidator','UsbCeip','KernelCeipTask'
    )
    Disable-Task '\Microsoft\Windows\Autochk\' @('Proxy')
    Disable-Task '\Microsoft\Windows\Feedback\Siuf\' @('DmClient','DmClientOnScenarioDownload')
    Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\DataCollection' 'AllowTelemetry' 0
}

if ($DisableSearchIndexer) {
    Section "Windows Search indexer"
    Disable-Svc 'WSearch'
}

if ($DisableSuperfetch) {
    Section "SysMain (Superfetch)"
    Disable-Svc 'SysMain'
}

if ($DisableXbox) {
    Section "Xbox services & tasks"
    Disable-Svc 'XblAuthManager'
    Disable-Svc 'XblGameSave'
    Disable-Svc 'XboxNetApiSvc'
    Disable-Svc 'XboxGipSvc'
    Disable-Task '\Microsoft\XblGameSave\' @('XblGameSaveTask','XblGameSaveTaskLogon')
}

if ($DisablePrintSpooler) {
    Section "Print Spooler"
    Disable-Svc 'Spooler'
}

if ($DisableBackgroundApps) {
    Section "Background UWP apps"
    Set-Reg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications' 'GlobalUserDisabled' 1
    Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy' 'LetAppsRunInBackground' 2
}

if ($DisableCortana) {
    Section "Cortana"
    Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search' 'AllowCortana' 0
    Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search' 'DisableWebSearch' 1
    Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\Windows Search' 'ConnectedSearchUseWeb' 0
}

if ($DisableStartupApps) {
    Section "Startup apps"
    $runKeys = @(
        'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run',
        'HKLM:\Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run'
    )
    foreach ($rk in $runKeys) {
        if (-not (Test-Path $rk)) { continue }
        Get-Item $rk | Select-Object -ExpandProperty Property | ForEach-Object {
            $name = $_
            if ($name -in @('Pinscreen2 Server','Pinscreen2.Server')) { return } # keep ours
            if ($DryRun) { Write-Host "  [dry] would remove $rk::$name"; return }
            try {
                Remove-ItemProperty -Path $rk -Name $name -ErrorAction Stop
                Write-Host "  [done] removed $rk::$name"
            } catch { Write-Warning "  [fail] $rk::$name -> $_" }
        }
    }
}

if ($RemoveStoreApps) {
    Section "Preinstalled Store / UWP apps"
    # Keep: Calculator, Photos, Store itself, Notepad, Terminal
    $kill = @(
        'Microsoft.3DBuilder','Microsoft.BingNews','Microsoft.BingWeather','Microsoft.GetHelp',
        'Microsoft.Getstarted','Microsoft.MicrosoftOfficeHub','Microsoft.MicrosoftSolitaireCollection',
        'Microsoft.MixedReality.Portal','Microsoft.OneConnect','Microsoft.People','Microsoft.SkypeApp',
        'Microsoft.Wallet','Microsoft.WindowsAlarms','Microsoft.WindowsCamera',
        'Microsoft.WindowsCommunicationsApps','Microsoft.WindowsFeedbackHub','Microsoft.WindowsMaps',
        'Microsoft.WindowsSoundRecorder','Microsoft.Xbox.TCUI','Microsoft.XboxApp',
        'Microsoft.XboxGameOverlay','Microsoft.XboxGamingOverlay','Microsoft.XboxIdentityProvider',
        'Microsoft.XboxSpeechToTextOverlay','Microsoft.YourPhone','Microsoft.ZuneMusic',
        'Microsoft.ZuneVideo','MicrosoftTeams','Clipchamp.Clipchamp','Microsoft.Todos',
        'Microsoft.PowerAutomateDesktop','MicrosoftCorporationII.QuickAssist',
        'MicrosoftWindows.Client.WebExperience'
    )
    foreach ($p in $kill) {
        $pkg = Get-AppxPackage -AllUsers -Name $p -ErrorAction SilentlyContinue
        if ($pkg) {
            if ($DryRun) { Write-Host "  [dry] would remove appx $p"; continue }
            try {
                $pkg | Remove-AppxPackage -AllUsers -ErrorAction Stop
                Write-Host "  [done] removed $p"
            } catch { Write-Warning "  [fail] $p -> $_" }
        }
        $prov = Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq $p
        if ($prov) {
            if ($DryRun) { Write-Host "  [dry] would remove provisioned $p"; continue }
            try {
                Remove-AppxProvisionedPackage -Online -PackageName $prov.PackageName -ErrorAction Stop | Out-Null
                Write-Host "  [done] deprovisioned $p"
            } catch { Write-Warning "  [fail] deprovision $p -> $_" }
        }
    }
}

if ($RemoveOneDrive) {
    Section "OneDrive removal"
    if ($DryRun) {
        Write-Host "  [dry] would stop OneDrive.exe and run uninstaller"
    } else {
        Get-Process -Name OneDrive -ErrorAction SilentlyContinue | Stop-Process -Force
        $uninst = @(
            "$env:SystemRoot\System32\OneDriveSetup.exe",
            "$env:SystemRoot\SysWOW64\OneDriveSetup.exe"
        ) | Where-Object { Test-Path $_ } | Select-Object -First 1
        if ($uninst) {
            Write-Host "  [done] running $uninst /uninstall"
            Start-Process -FilePath $uninst -ArgumentList '/uninstall' -Wait
        }
        Remove-Item -Recurse -Force "$env:USERPROFILE\OneDrive" -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force "$env:LOCALAPPDATA\Microsoft\OneDrive" -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force "$env:PROGRAMDATA\Microsoft OneDrive" -ErrorAction SilentlyContinue
        Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\OneDrive' 'DisableFileSyncNGSC' 1
    }
}

if ($DisableDefender) {
    Section "Microsoft Defender real-time scanning"
    Write-Warning "Tamper Protection MUST already be off in Windows Security UI; otherwise these changes silently revert."
    if (-not $DryRun) {
        try { Set-MpPreference -DisableRealtimeMonitoring $true -ErrorAction Stop }
        catch { Write-Warning "  [fail] Set-MpPreference -DisableRealtimeMonitoring -> $_" }
        try { Set-MpPreference -DisableBehaviorMonitoring $true -ErrorAction SilentlyContinue } catch {}
        try { Set-MpPreference -DisableBlockAtFirstSeen $true -ErrorAction SilentlyContinue } catch {}
        try { Set-MpPreference -DisableScriptScanning $true -ErrorAction SilentlyContinue } catch {}
        try { Set-MpPreference -MAPSReporting Disabled -ErrorAction SilentlyContinue } catch {}
        Set-Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows Defender' 'DisableAntiSpyware' 1
        Disable-Task '\Microsoft\Windows\Windows Defender\' @(
            'Windows Defender Cache Maintenance',
            'Windows Defender Cleanup',
            'Windows Defender Scheduled Scan',
            'Windows Defender Verification'
        )
    }
}

Section "Done"
Write-Host "Reboot to ensure all changes take effect." -ForegroundColor Yellow
