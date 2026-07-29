<#
.SYNOPSIS
  Deploys Pinscreen2.Server and installs it so it starts automatically and
  restarts itself if it dies.

.DESCRIPTION
  Two supervision modes:

    Watchdog (default, no elevation)
      A hidden wscript watchdog relaunches the server whenever it exits, started
      from the user's Startup folder. Fixes crash-death, but only runs after
      login and dies with the session.

    Scheduled task (-AsService, requires an elevated shell)
      Registers a SYSTEM-level scheduled task with a boot trigger, so the server
      is up before anyone logs in and survives logout. A 5-minute repeat trigger
      combined with IgnoreNew relaunches it if the process is gone, and is a
      no-op while it is healthy.

.EXAMPLE
  ./scripts/install-server.ps1
  ./scripts/install-server.ps1 -AsService     # from an elevated PowerShell
#>
[CmdletBinding()]
param(
    [string]$Root = 'D:\Pinball\videos',
    [int]$Port = 8088,
    [string]$InstallDir = "$env:LOCALAPPDATA\Pinscreen2.Server",
    [switch]$AsService,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$taskName = 'Pinscreen2 Server'
$exePath = Join-Path $InstallDir 'Pinscreen2.Server.exe'
$vbsPath = Join-Path $InstallDir 'start-server.vbs'

if (-not (Test-Path $Root)) { throw "Library root not found: $Root" }
New-Item -ItemType Directory -Force $InstallDir | Out-Null

# ---------------------------------------------------------------- publish
if (-not $SkipBuild) {
    $staging = Join-Path ([IO.Path]::GetTempPath()) "pinscreen2-server-$PID"
    Write-Host "Publishing self-contained server..." -ForegroundColor Cyan
    dotnet publish (Join-Path $repo 'Pinscreen2.Server\Pinscreen2.Server.csproj') `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:PublishTrimmed=false -o $staging -v minimal --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

    Get-Process -Name 'Pinscreen2.Server' -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 2
    Copy-Item (Join-Path $staging 'Pinscreen2.Server.exe') $exePath -Force
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Installed $exePath" -ForegroundColor Green
}

# ---------------------------------------------------------------- config
$cfg = [ordered]@{
    Root             = $Root
    Port             = $Port
    AutoPushOnChange = $true
    RefreshMinutes   = 5
}
$cfg | ConvertTo-Json | Out-File (Join-Path $InstallDir 'server-config.json') -Encoding utf8

# ---------------------------------------------------------------- supervision
if ($AsService) {
    $principal = New-ScheduledTaskPrincipal -UserId 'S-1-5-18' -LogonType ServiceAccount -RunLevel Limited
    $action = New-ScheduledTaskAction -Execute $exePath `
        -Argument "--root `"$Root`" --port $Port" -WorkingDirectory $InstallDir

    # Repeat forever: IgnoreNew means this does nothing while the server is
    # healthy, and relaunches it within 5 minutes if the process has died.
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $trigger.Repetition = (New-ScheduledTaskTrigger -Once -At (Get-Date) `
        -RepetitionInterval (New-TimeSpan -Minutes 5)).Repetition

    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew `
        -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
        -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartInterval (New-TimeSpan -Minutes 1) -RestartCount 3

    Register-ScheduledTask -TaskName $taskName -Principal $principal -Action $action `
        -Trigger $trigger -Settings $settings -Force | Out-Null

    # The watchdog would double-start the server alongside the task.
    Remove-Item "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup\Pinscreen2 Server.lnk" -ErrorAction SilentlyContinue

    Start-ScheduledTask -TaskName $taskName
    Write-Host "Registered scheduled task '$taskName' (SYSTEM, starts at boot)." -ForegroundColor Green
}
else {
    Copy-Item (Join-Path $repo 'scripts\start-server.vbs') $vbsPath -Force
    # The VBS reads root/port from server-config.json, so no substitution needed.

    $startup = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Startup"
    $ws = New-Object -ComObject WScript.Shell
    $lnk = $ws.CreateShortcut((Join-Path $startup 'Pinscreen2 Server.lnk'))
    $lnk.TargetPath = "$env:SystemRoot\System32\wscript.exe"
    $lnk.Arguments = "`"$vbsPath`""
    $lnk.WorkingDirectory = $InstallDir
    $lnk.Description = 'Pinscreen 2 library server (watchdog: relaunches on crash)'
    $lnk.Save()

    if (-not (Get-Process -Name 'Pinscreen2.Server' -ErrorAction SilentlyContinue)) {
        Start-Process "$env:SystemRoot\System32\wscript.exe" -ArgumentList "`"$vbsPath`"" -WorkingDirectory $InstallDir
    }
    Write-Host "Installed watchdog launcher and Startup shortcut." -ForegroundColor Green
}

# ---------------------------------------------------------------- firewall
if (-not (Get-NetFirewallRule -DisplayName 'Pinscreen2 Server' -ErrorAction SilentlyContinue)) {
    try {
        New-NetFirewallRule -DisplayName 'Pinscreen2 Server' -Direction Inbound -Protocol TCP `
            -LocalPort $Port -Action Allow -Profile Private,Domain | Out-Null
        Write-Host "Added firewall rule for TCP $Port." -ForegroundColor Green
    }
    catch { Write-Warning "Could not add the firewall rule (needs elevation): $_" }
}

Write-Host "`nDashboard: http://localhost:$Port/" -ForegroundColor Cyan
