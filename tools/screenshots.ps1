<#
.SYNOPSIS
    Regenerates the README screenshots in docs\images from the current sources.

.DESCRIPTION
    Compiles tools\Screenshots.cs together with src\*.cs (same compiler as build.ps1), then runs
    it: it shows the install dialog and the tray menu over a plain backdrop for a moment each and
    saves them, plus the tray icon drawn large as logo.png / logo-paused.png.

    Run it on an interactive desktop and don't touch the mouse or keyboard for the ~3 seconds it
    takes. Use the Windows theme, accent colour and display scaling you want in the images
    (100% or 150% scaling gives crisp results). Nothing is installed and no settings are saved;
    exit any running NubMouse first so the tray menu can be created.

.PARAMETER Version
    Version shown in the dialog title and menu header. Defaults to the latest git tag.

.EXAMPLE
    git pull; .\tools\screenshots.ps1
    .\tools\screenshots.ps1 -Version 0.2.0
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutDir = (Join-Path $PSScriptRoot "..\docs\images")
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent

if (-not $Version) {
    $Version = (git -C $root describe --tags --abbrev=0 2>$null)
    if (-not $Version) { $Version = "0.0.0" }
}
$display = $Version.TrimStart("v", "V")
$numeric = ($display -split "[-+]")[0]

if (Get-Process NubMouse -ErrorAction SilentlyContinue) {
    throw "NubMouse is running. Exit it from its tray menu first."
}

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }

$work = Join-Path ([IO.Path]::GetTempPath()) ("NubMouse-screenshots-" + [Guid]::NewGuid())
New-Item -ItemType Directory $work | Out-Null
try {
    $info = Join-Path $work "AssemblyInfo.cs"
    @"
using System.Reflection;
[assembly: AssemblyVersion("$numeric")]
[assembly: AssemblyInformationalVersion("$display")]
"@ | Set-Content -Encoding UTF8 $info

    $fw = Split-Path $csc
    $winmd = Join-Path $env:WINDIR "System32\WinMetadata"
    $winrt = @(
        "System.Runtime.dll", "System.Runtime.WindowsRuntime.dll", "System.Runtime.InteropServices.WindowsRuntime.dll"
    ) | ForEach-Object { "-r:" + (Join-Path $fw $_) }
    $winrt += @("Windows.Foundation.winmd", "Windows.Data.winmd", "Windows.UI.winmd") | ForEach-Object { "-r:" + (Join-Path $winmd $_) }

    $exe = Join-Path $work "Screenshots.exe"
    $sources = @(Get-ChildItem (Join-Path $root "src\*.cs") | ForEach-Object FullName) +
               (Join-Path $PSScriptRoot "Screenshots.cs") + $info
    & $csc -nologo -optimize+ -target:exe -main:NubMouse.Tools.Screenshots "-out:$exe" `
        "-win32manifest:$(Join-Path $root 'src\app.manifest')" `
        -r:System.Web.Extensions.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll @winrt @sources
    if ($LASTEXITCODE -ne 0) { throw "csc.exe failed with exit code $LASTEXITCODE" }

    $out = [IO.Path]::GetFullPath($OutDir)
    Write-Host "Capturing NubMouse $display into $out (hands off the mouse for a few seconds)..."
    $p = Start-Process $exe -ArgumentList "`"$out`"" -Wait -PassThru -NoNewWindow
    if ($p.ExitCode -ne 0) { throw "Screenshots.exe failed with exit code $($p.ExitCode)" }
    Get-ChildItem $out -Filter *.png | Format-Table Name, Length, LastWriteTime -AutoSize
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
