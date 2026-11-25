<#
.SYNOPSIS
    Builds an MSIX package (x64, arm64, or neutral) for Microsoft Store submission.

.DESCRIPTION
    Builds NubMouse.exe (if not already provided via -ExePath), generates the required
    MSIX tile assets from docs\images\app-icon.png, fills in AppxManifest.xml, and packs an
    unsigned .msix with makeappx.exe from the Windows SDK.

    When uploaded to Microsoft Partner Center, Microsoft automatically re-signs the MSIX with
    its trusted Microsoft Store certificate (free for individual developer accounts), so users
    install it with zero SmartScreen warnings.

    Note on ARM64: NubMouse compiles with -platform:anycpu. On Windows 11 ARM64, the
    built-in .NET Framework 4.8.1 CLR executes anycpu binaries natively as ARM64. You can
    package as -Arch neutral (one package for x64 + arm64) or -Arch all (separate x64 and
    arm64 .msix files).

.EXAMPLE
    .\packaging\msix\build-msix.ps1 -Version 0.1.0 -Arch all
    .\packaging\msix\build-msix.ps1 -Version 0.1.0 -Arch neutral -IdentityName "12345dstarikov.NubMouse" -Publisher "CN=..."
#>
[CmdletBinding()]
param(
    [string]$Version = "0.1.0",
    [ValidateSet("neutral", "x64", "arm64", "all")]
    [string]$Arch = "all",
    [string]$IdentityName = "dstarikov.NubMouse",
    [string]$Publisher = "CN=5CEB058C-D861-4834-BC26-FE4712BCA5BB",
    [string]$PublisherDisplayName = "dstarikov",
    [string]$ExePath,
    [string]$OutDir = "dist"
)

$ErrorActionPreference = "Stop"
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

# MSIX requires a 4-part numeric version (major.minor.build.revision) with major > 0 for Store
# if desired; we normalize x.y.z -> x.y.z.0.
$cleanVer = ($Version.TrimStart("v", "V") -split "[-+]")[0]
$parts = $cleanVer.Split(".")
while ($parts.Count -lt 4) { $parts += "0" }
$msixVer = ($parts[0..3]) -join "."

$outPath = if ([IO.Path]::IsPathRooted($OutDir)) { $OutDir } else { Join-Path $root $OutDir }
New-Item -ItemType Directory -Force $outPath | Out-Null

if (-not $ExePath) {
    & (Join-Path $root "build.ps1") -Version $Version -OutDir $outPath
    $ExePath = Join-Path $outPath "NubMouse.exe"
}
if (-not (Test-Path $ExePath)) { throw "NubMouse.exe not found at $ExePath" }

# Locate makeappx.exe from Windows 10/11 SDK
$makeappx = Get-Command makeappx.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source
if (-not $makeappx) {
    $kits = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path $kits) {
        $makeappx = Get-ChildItem $kits -Filter "makeappx.exe" -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }
}
if (-not $makeappx) {
    throw "makeappx.exe not found. Install the Windows 10/11 SDK (or run in GitHub Actions windows-latest)."
}

Add-Type -AssemblyName System.Drawing
function Save-Tile([System.Drawing.Image]$src, [string]$dest, [int]$w, [int]$h, [double]$scale = 0.80) {
    $bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $side = [int]([Math]::Min($w, $h) * $scale)
    $x = [int](($w - $side) / 2)
    $y = [int](($h - $side) / 2)
    $g.DrawImage($src, (New-Object System.Drawing.Rectangle($x, $y, $side, $side)))
    $g.Dispose()
    $bmp.Save($dest, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

$iconPng = Join-Path $root "docs\images\app-icon.png"
if (-not (Test-Path $iconPng)) { $iconPng = Join-Path $root "docs\images\logo.png" }
if (-not (Test-Path $iconPng)) { throw "App icon PNG not found in docs\images\." }

$arches = if ($Arch -eq "all") { @("x64", "arm64") } else { @($Arch) }
$template = Get-Content -Raw (Join-Path $PSScriptRoot "AppxManifest.template.xml")

$srcImg = [System.Drawing.Image]::FromFile($iconPng)
try {
    foreach ($a in $arches) {
        $stage = Join-Path ([IO.Path]::GetTempPath()) ("NubMouse-msix-$a-" + [Guid]::NewGuid())
        $assets = Join-Path $stage "Assets"
        New-Item -ItemType Directory -Force $assets | Out-Null
        try {
            Copy-Item $ExePath (Join-Path $stage "NubMouse.exe")

            Save-Tile $srcImg (Join-Path $assets "StoreLogo.png") 50 50 0.88
            Save-Tile $srcImg (Join-Path $assets "Square44x44Logo.png") 44 44 0.88
            Save-Tile $srcImg (Join-Path $assets "Square44x44Logo.targetsize-48_altform-unplated.png") 48 48 0.92
            Save-Tile $srcImg (Join-Path $assets "Square150x150Logo.png") 150 150 0.72
            Save-Tile $srcImg (Join-Path $assets "Wide310x150Logo.png") 310 150 0.72

            $manifest = $template `
                -replace '\{\{IDENTITY_NAME\}\}', $IdentityName `
                -replace '\{\{PUBLISHER\}\}', $Publisher `
                -replace '\{\{PUBLISHER_DISPLAY_NAME\}\}', $PublisherDisplayName `
                -replace '\{\{VERSION\}\}', $msixVer `
                -replace '\{\{ARCH\}\}', $a
            [System.IO.File]::WriteAllText((Join-Path $stage "AppxManifest.xml"), $manifest, (New-Object System.Text.UTF8Encoding($false)))

            $outMsix = Join-Path $outPath ("NubMouse-$cleanVer-$a.msix")
            & $makeappx pack /d $stage /p $outMsix /o | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "makeappx.exe failed for $a with exit code $LASTEXITCODE" }
            Write-Host "Built $outMsix (version $msixVer, arch $a)"
        }
        finally {
            Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}
finally {
    $srcImg.Dispose()
}
