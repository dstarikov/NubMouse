<#
.SYNOPSIS
    End-to-end smoke test of a built NubMouse.exe on this machine.

.DESCRIPTION
    Runs the real exe: portable start, pause/resume, single instance, hidden icon, per-user
    install, in-place update while running, and uninstall. It needs an interactive desktop
    session (the mouse hook and tray icon do), so it is meant to be run locally, not in CI.

    It installs into your own profile and removes everything again at the end. Stop any
    NubMouse you are running first; your saved settings are backed up and restored.
    Nothing here needs administrator rights; the all-users install and "Run as administrator"
    (which need a UAC prompt) are not covered.

.EXAMPLE
    .\build.ps1; .\tests\smoke.ps1
#>
[CmdletBinding()]
param([string]$Exe = (Join-Path $PSScriptRoot "..\dist\NubMouse.exe"))

$ErrorActionPreference = "Stop"
$Exe = (Resolve-Path $Exe).Path
$settingsKey = "HKCU:\Software\NubMouse"
$installed = Join-Path $env:LOCALAPPDATA "Programs\NubMouse\NubMouse.exe"
$failures = 0

function Check([string]$what, [bool]$ok) {
    if ($ok) { Write-Host "  ok    $what" -ForegroundColor Green }
    else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:failures++ }
}
function Running { @(Get-Process NubMouse -ErrorAction SilentlyContinue) }
function Setting([string]$name) { (Get-ItemProperty $settingsKey -ErrorAction SilentlyContinue).$name }
function Wait-Exit([string[]]$argList, [string]$path = $Exe) {
    $p = Start-Process $path -ArgumentList $argList -PassThru
    $p.WaitForExit(); $p.ExitCode
}

if (Running) { throw "Stop the running NubMouse before running the smoke test." }
if (Test-Path $installed) { throw "NubMouse is installed for this user; uninstall it first." }

$backup = Join-Path $env:TEMP "NubMouse-settings-backup.reg"
$hadSettings = Test-Path $settingsKey
if ($hadSettings) { reg export "HKCU\Software\NubMouse" $backup /y | Out-Null }

try {
    Write-Host "Portable run"
    Start-Process $Exe -ArgumentList "--portable"; Start-Sleep 2
    Check "starts" ((Running).Count -eq 1)
    & $Exe --toggle; Start-Sleep 1
    Check "--toggle pauses" ((Setting Enabled) -eq 0)
    & $Exe --toggle; Start-Sleep 1
    Check "--toggle resumes" ((Setting Enabled) -eq 1)
    Start-Process $Exe -ArgumentList "--portable"; Start-Sleep 2
    Check "second launch doesn't start a second copy" ((Running).Count -eq 1)
    & $Exe --exit; Start-Sleep 2
    Check "--exit stops it" ((Running).Count -eq 0)

    Write-Host "Install"
    Check "--install succeeds" ((Wait-Exit "--install", "--no-startup") -eq 0)
    Start-Sleep 2
    Check "exe installed" (Test-Path $installed)
    Check "Start menu shortcut" (Test-Path (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\NubMouse.lnk"))
    Check "Apps & features entry" (Test-Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NubMouse")
    Check "installed copy is running" ((Running | Where-Object Path -eq $installed).Count -eq 1)

    Write-Host "Hidden icon"
    & $installed --exit; Start-Sleep 1
    Start-Process $installed -ArgumentList "hidden"; Start-Sleep 2
    Check "runs with 'hidden'" ((Running).Count -eq 1)
    Start-Process $installed; Start-Sleep 2
    Check "relaunch shows it instead of starting another" ((Running).Count -eq 1)

    Write-Host "Update in place"
    $newDir = Join-Path $env:TEMP "NubMouse-smoke-update"
    & (Join-Path $PSScriptRoot "..\build.ps1") -Version 99.0.0 -OutDir $newDir | Out-Null
    Check "update succeeds while running" ((Wait-Exit "--install", "--quiet" (Join-Path $newDir "NubMouse.exe")) -eq 0)
    Start-Sleep 2
    Check "installed exe is the new version" ((Get-Item $installed).VersionInfo.FileVersion -eq "99.0.0")
    Check "new version is running" ((Running | Where-Object Path -eq $installed).Count -eq 1)
    Remove-Item -Recurse -Force $newDir

    Write-Host "Uninstall"
    Check "--uninstall succeeds" ((Wait-Exit "--uninstall", "--quiet" $installed) -eq 0)
    Start-Sleep 4
    Check "not running" ((Running).Count -eq 0)
    Check "folder removed" (-not (Test-Path (Split-Path $installed)))
    Check "shortcut removed" (-not (Test-Path (Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\NubMouse.lnk")))
    Check "Apps & features entry removed" (-not (Test-Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NubMouse"))
    Check "settings removed" (-not (Test-Path $settingsKey))
}
finally {
    Get-Process NubMouse -ErrorAction SilentlyContinue | Stop-Process -Force
    if ($hadSettings) { reg import $backup 2>$null; Remove-Item $backup }
}

if ($failures) { Write-Host "$failures check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "All checks passed" -ForegroundColor Green
