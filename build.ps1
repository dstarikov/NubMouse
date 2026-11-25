<#
.SYNOPSIS
    Builds NubMouse.exe with the C# compiler that ships with the .NET Framework.

.DESCRIPTION
    No Visual Studio or .NET SDK is needed: every Windows 10/11 install has
    csc.exe under %WINDIR%\Microsoft.NET\Framework64\v4.0.30319. That compiler
    only understands C# 5, so the sources in src\ stick to C# 5 syntax.

.PARAMETER Version
    Version stamped into the exe's file properties. A leading "v" and any
    pre-release/build suffix ("-beta", "+abc") are stripped for the numeric
    file version; the full string is kept as the product version.

.PARAMETER OutDir
    Folder the exe is written to (created if missing).

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Version v0.2.0 -OutDir dist
#>
[CmdletBinding()]
param(
    [string]$Version = "0.0.0-dev",
    [string]$OutDir = "dist"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "csc.exe not found. Is the .NET Framework 4.x installed?" }

$display = $Version.TrimStart("v", "V")
$numeric = ($display -split "[-+]")[0]
if ($numeric -notmatch '^\d+(\.\d+){0,3}$') { throw "Version '$Version' does not start with a numeric x.y.z version." }

$outPath = if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $root $OutDir }
New-Item -ItemType Directory -Force $outPath | Out-Null
$exe = Join-Path $outPath "NubMouse.exe"

# Assembly metadata is generated so the version comes from the build, not from a checked-in file.
$info = Join-Path ([IO.Path]::GetTempPath()) ("NubMouse.AssemblyInfo.{0}.cs" -f [Guid]::NewGuid())
@"
using System.Reflection;
[assembly: AssemblyTitle("NubMouse")]
[assembly: AssemblyProduct("NubMouse")]
[assembly: AssemblyCompany("dstarikov")]
[assembly: AssemblyCopyright("Copyright (c) 2026 dstarikov. MIT License.")]
[assembly: AssemblyDescription("ThinkPad-style middle click and TrackPoint scrolling for Windows")]
[assembly: AssemblyVersion("$numeric")]
[assembly: AssemblyFileVersion("$numeric")]
[assembly: AssemblyInformationalVersion("$display")]
"@ | Set-Content -Encoding UTF8 $info

# Toast notifications use Windows Runtime APIs. Their metadata (.winmd) ships with Windows 10/11,
# and the .NET Framework has the assemblies that bridge to it, so no SDK is needed here either.
$fw = Split-Path $csc
$winmd = Join-Path $env:WINDIR "System32\WinMetadata"
$winrt = @(
    "System.Runtime.dll", "System.Runtime.WindowsRuntime.dll", "System.Runtime.InteropServices.WindowsRuntime.dll"
) | ForEach-Object { "-r:" + (Join-Path $fw $_) }
$winrt += @("Windows.Foundation.winmd", "Windows.Data.winmd", "Windows.UI.winmd") | ForEach-Object { "-r:" + (Join-Path $winmd $_) }
foreach ($r in $winrt) { if (-not (Test-Path $r.Substring(3))) { throw "$($r.Substring(3)) not found. Windows 10 or later is needed to build." } }

try {
    $src = Join-Path $root "src"
    $sources = @(Get-ChildItem (Join-Path $src "*.cs") | ForEach-Object FullName) + $info
    $icon = Join-Path $root "docs\images\app-icon.ico"
    if (Test-Path $icon) { $winrt += "-win32icon:$icon" }
    & $csc -nologo -optimize+ -warnaserror+ -target:winexe -platform:anycpu "-out:$exe" `
        "-win32manifest:$(Join-Path $src 'app.manifest')" `
        -r:System.Web.Extensions.dll -r:System.Windows.Forms.dll -r:System.Drawing.dll `
        @winrt @sources
    if ($LASTEXITCODE -ne 0) { throw "csc.exe failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item $info -ErrorAction SilentlyContinue
}

Write-Host "Built $exe (version $display)"
