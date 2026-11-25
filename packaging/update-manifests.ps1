<#
.SYNOPSIS
    Updates the Winget and Scoop manifests with a new release version and SHA-256 hash.

.EXAMPLE
    .\packaging\update-manifests.ps1 -Version 0.1.0 -Sha256 <64-hex-chars>
    .\packaging\update-manifests.ps1 -Version 0.1.0 -ExePath dist\NubMouse.exe
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [string]$Sha256,
    [string]$ExePath
)

$ErrorActionPreference = "Stop"
$ver = $Version.TrimStart("v", "V")
if (-not $Sha256) {
    if (-not $ExePath) { throw "Provide either -Sha256 or -ExePath." }
    $Sha256 = (Get-FileHash $ExePath -Algorithm SHA256).Hash.ToUpperInvariant()
} else {
    $Sha256 = $Sha256.Trim().ToUpperInvariant()
}

$wingetDir = Join-Path $PSScriptRoot "winget"
$scoopFile = Join-Path $PSScriptRoot "scoop\nubmouse.json"
$url = "https://github.com/dstarikov/NubMouse/releases/download/v$ver/NubMouse.exe"

foreach ($f in @("dstarikov.NubMouse.yaml", "dstarikov.NubMouse.installer.yaml", "dstarikov.NubMouse.locale.en-US.yaml")) {
    $p = Join-Path $wingetDir $f
    $t = [System.IO.File]::ReadAllText($p)
    $t = [regex]::Replace($t, '(?m)^PackageVersion: .*$', "PackageVersion: $ver")
    $t = [regex]::Replace($t, '(?m)^(\s+InstallerUrl: ).*$', "`${1}$url")
    $t = [regex]::Replace($t, '(?m)^(\s+InstallerSha256: ).*$', "`${1}$Sha256")
    [System.IO.File]::WriteAllText($p, $t, (New-Object System.Text.UTF8Encoding($false)))
}

if (Test-Path $scoopFile) {
    $t = [System.IO.File]::ReadAllText($scoopFile)
    $t = [regex]::Replace($t, '"version"\s*:\s*"[^"]*"', "`"version`": `"$ver`"")
    $t = [regex]::Replace($t, '"url"\s*:\s*"https://github\.com/dstarikov/NubMouse/releases/download/[^"]+"', "`"url`": `"$url`"")
    $t = [regex]::Replace($t, '"hash"\s*:\s*"[^"]*"', "`"hash`": `"$($Sha256.ToLowerInvariant())`"")
    [System.IO.File]::WriteAllText($scoopFile, $t, (New-Object System.Text.UTF8Encoding($false)))
}

Write-Host "Updated Winget and Scoop manifests to v$ver (SHA256: $Sha256)"
