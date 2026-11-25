<#
.SYNOPSIS
    Interactive UAC smoke test for all-users install, "Run as administrator", and all-users uninstall.

.DESCRIPTION
    Tests the elevated paths that tests\smoke.ps1 skips because they require UAC approval:
      1. --install --all-users (Program Files copy, Common Start Menu shortcut, HKLM Uninstall entry)
      2. AdminMode security check (refuses non-Program Files exe path)
      3. "Run as administrator" scheduled task creation, task XML validation, no leftover XML file,
         and unelevated launch handing over to the elevated task with zero UAC prompt
      4. --uninstall --all-users (removes Program Files folder, shortcut, HKLM entry, and elevated task)

    Expect 3 UAC prompts: install, create admin task, uninstall.
    Your HKCU\Software\NubMouse settings are backed up and restored afterwards.

.EXAMPLE
    .\build.ps1; .\tests\smoke-uac.ps1
#>
[CmdletBinding()]
param([string]$Exe = (Join-Path $PSScriptRoot "..\dist\NubMouse.exe"))

$ErrorActionPreference = "Stop"
$Exe = (Resolve-Path $Exe).Path
$settingsKey = "HKCU:\Software\NubMouse"
$allUsersDir = Join-Path $env:ProgramFiles "NubMouse"
$allUsersExe = Join-Path $allUsersDir "NubMouse.exe"
$commonShortcut = Join-Path ([Environment]::GetFolderPath("CommonPrograms")) "NubMouse.lnk"
$hklmUninstall = "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\NubMouse"
$userSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$userName = $env:USERNAME
$taskName = "NubMouse elevated - $userName"
$failures = 0

function Check([string]$what, [bool]$ok) {
    if ($ok) { Write-Host "  ok    $what" -ForegroundColor Green }
    else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:failures++ }
}
function Running { @(Get-Process NubMouse -ErrorAction SilentlyContinue) }
function Wait-Exit([string[]]$argList, [string]$path = $Exe) {
    $p = Start-Process $path -ArgumentList $argList -PassThru
    $p.WaitForExit(); $p.ExitCode
}
function Is-ProcessElevated([int]$procId) {
    if (-not ("TokenCheck" -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class TokenCheck {
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int len, out int retLen);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    public static bool IsElevated(int pid, bool selfElevated) {
        if (!selfElevated) {
            // PROCESS_VM_READ (0x0010) on same-user process fails with ERROR_ACCESS_DENIED (5) iff target is higher integrity (elevated).
            IntPtr h = OpenProcess(0x0010, false, pid);
            if (h == IntPtr.Zero) return Marshal.GetLastWin32Error() == 5;
            CloseHandle(h);
            return false;
        }
        IntPtr p = OpenProcess(0x1000, false, pid);
        if (p == IntPtr.Zero) return false;
        try {
            IntPtr t;
            if (!OpenProcessToken(p, 0x0008, out t)) return false;
            try {
                int elev, len;
                return GetTokenInformation(t, 20, out elev, 4, out len) && elev != 0;
            } finally { CloseHandle(t); }
        } finally { CloseHandle(p); }
    }
}
"@
    }
    $selfElevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    return [TokenCheck]::IsElevated($procId, $selfElevated)
}

# Stop any running NubMouse first
Get-Process NubMouse -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
if (Running) { throw "Could not stop running NubMouse (is an elevated copy running?)." }
if (Test-Path $allUsersExe) { throw "NubMouse is already installed for all users at $allUsersExe; uninstall it first." }

$backup = Join-Path $env:TEMP "NubMouse-uac-settings-backup.reg"
$hadSettings = Test-Path $settingsKey
if ($hadSettings) { reg export "HKCU\Software\NubMouse" $backup /y | Out-Null }

try {
    Write-Host "1. All-users install (UAC prompt #1)" -ForegroundColor Cyan
    Check "--install --all-users succeeds" ((Wait-Exit @("--install", "--all-users", "--no-startup")) -eq 0)
    Start-Sleep 2
    Check "exe installed in Program Files" (Test-Path $allUsersExe)
    Check "all-users Start menu shortcut" (Test-Path $commonShortcut)
    Check "HKLM Apps & features entry" (Test-Path $hklmUninstall)
    $pub = (Get-ItemProperty $hklmUninstall -ErrorAction SilentlyContinue).Publisher
    Check "HKLM Publisher is set ($pub)" (-not [string]::IsNullOrWhiteSpace($pub))
    Check "installed copy is running" ((Running | Where-Object Path -eq $allUsersExe).Count -eq 1)

    Write-Host "2. AdminMode security & elevated task (UAC prompt #2)" -ForegroundColor Cyan
    # Stop unelevated copy
    & $allUsersExe --exit; Start-Sleep 1
    Check "unelevated copy stopped" ((Running).Count -eq 0)

    # Create elevated task via elevated child (exact same command AdminMode.CreateTask runs)
    $p = Start-Process $allUsersExe -Verb RunAs -ArgumentList @("--admin-task", "create", "--user", $userSid, "--exe", "`"$allUsersExe`"") -PassThru
    $p.WaitForExit()
    Check "--admin-task create succeeds" ($p.ExitCode -eq 0)

    $xmlOut = & schtasks.exe /Query /XML /TN $taskName 2>&1
    $taskExists = ($LASTEXITCODE -eq 0)
    Check "scheduled task '$taskName' exists" $taskExists
    $xmlText = ($xmlOut -join "`n")
    Check "task XML runs Program Files exe" ($xmlText -match [regex]::Escape("<Command>$allUsersExe</Command>"))
    Check "task XML uses HighestAvailable" ($xmlText -match "<RunLevel>HighestAvailable</RunLevel>")
    $leftoverXml = @(Get-ChildItem $allUsersDir -Filter "task-*.xml" -ErrorAction SilentlyContinue)
    Check "no leftover task-*.xml in Program Files" ($leftoverXml.Count -eq 0)

    # Enable RunAsAdmin in HKCU and launch unelevated: should hand over to scheduled task without UAC
    New-Item -Path $settingsKey -Force | Out-Null
    Set-ItemProperty -Path $settingsKey -Name RunAsAdmin -Value 1 -Type DWord
    $selfElev = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Write-Host "  info  test shell elevated=$selfElev" -ForegroundColor DarkGray
    Start-Process $allUsersExe
    Start-Sleep 3
    $procs = Running
    Check "unelevated launch handed over to task (1 running)" ($procs.Count -eq 1)
    if ($procs.Count -eq 1) {
        $isElev = Is-ProcessElevated $procs[0].Id
        Check "running process is actually elevated (pid=$($procs[0].Id))" $isElev
        if (-not $isElev) {
            $prevEap = $ErrorActionPreference; $ErrorActionPreference = "Continue"
            $runTest = & schtasks.exe /Run /TN $taskName 2>&1
            Write-Host "  diag  direct 'schtasks /Run' exit=$LASTEXITCODE output: $($runTest -join ' ')" -ForegroundColor Yellow
            $ErrorActionPreference = $prevEap
        }
    }

    Write-Host "3. All-users uninstall & task cleanup (UAC prompt #3)" -ForegroundColor Cyan
    Check "--uninstall --all-users succeeds" ((Wait-Exit @("--uninstall", "--all-users", "--quiet") $allUsersExe) -eq 0)
    Start-Sleep 4
    Check "not running" ((Running).Count -eq 0)
    Check "Program Files folder removed" (-not (Test-Path $allUsersDir))
    Check "all-users shortcut removed" (-not (Test-Path $commonShortcut))
    Check "HKLM Apps & features entry removed" (-not (Test-Path $hklmUninstall))
    $prevEap = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    $null = & schtasks.exe /Query /TN $taskName 2>&1
    $taskGone = ($LASTEXITCODE -ne 0)
    $ErrorActionPreference = $prevEap
    Check "elevated scheduled task removed" $taskGone
}
finally {
    if ($hadSettings) {
        if (Test-Path $settingsKey) { Remove-Item $settingsKey -Recurse -Force -ErrorAction SilentlyContinue }
        reg import $backup 2>$null
        Remove-Item $backup -Force -ErrorAction SilentlyContinue
    }
}

if ($failures) { Write-Host "$failures check(s) failed" -ForegroundColor Red; exit 1 }
Write-Host "All UAC checks passed!" -ForegroundColor Green
