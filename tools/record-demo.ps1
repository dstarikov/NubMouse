<#
.SYNOPSIS
    Records a window to an optimized GIF for the README (needs ffmpeg on PATH).

.DESCRIPTION
    After a countdown, records the window that is in front (or a given region) with ffmpeg's
    gdigrab, including the mouse pointer, then converts the clip to a GIF with a per-clip palette
    so text stays sharp and the file stays small.

    The demo itself has to be done by hand with a real TrackPoint: NubMouse ignores
    synthetic input, so it can't be scripted. See docs\media.md for the shot list.

.PARAMETER Seconds
    How long to record.

.PARAMETER Out
    The GIF to write. The intermediate MP4 is kept next to it with -KeepVideo.

.PARAMETER Width
    Width of the GIF in pixels (height follows). 800 fits the README column on GitHub.

.PARAMETER Fps
    Frames per second of the GIF. 15-20 is smooth enough for scrolling and keeps it small.

.PARAMETER Region
    "x,y,width,height" in physical screen pixels, instead of the window in front.

.PARAMETER Delay
    Seconds of countdown before recording starts, to bring the window to the front.

.EXAMPLE
    .\tools\record-demo.ps1 -Seconds 12
    .\tools\record-demo.ps1 -Seconds 8 -Out docs\images\autoscroll.gif
#>
[CmdletBinding()]
param(
    [int]$Seconds = 12,
    [string]$Out = (Join-Path $PSScriptRoot "..\docs\images\demo.gif"),
    [int]$Width = 800,
    [int]$Fps = 20,
    [string]$Region,
    [int]$Delay = 3,
    [switch]$KeepVideo
)

$ErrorActionPreference = "Stop"
if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) { throw "ffmpeg was not found on PATH." }

Add-Type -Namespace Rec -Name Win -MemberDefinition @"
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
[System.Runtime.InteropServices.DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(System.IntPtr hwnd, int attr, out RECT rect, int size);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(System.IntPtr value);
"@
# Physical pixels everywhere, whatever the display scaling.
[void][Rec.Win]::SetProcessDpiAwarenessContext([IntPtr]-4)

for ($i = $Delay; $i -gt 0; $i--) {
    Write-Host "Recording in $i... bring the window to record to the front."
    Start-Sleep -Seconds 1
}

if ($Region) {
    $x, $y, $w, $h = $Region -split "," | ForEach-Object { [int]$_ }
}
else {
    # The visible frame, without the invisible resize borders GetWindowRect includes.
    $r = New-Object Rec.Win+RECT
    [void][Rec.Win]::DwmGetWindowAttribute([Rec.Win]::GetForegroundWindow(), 9, [ref]$r, 16)
    $x = $r.Left; $y = $r.Top; $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
}
# Encoders want even sizes.
$w -= $w % 2; $h -= $h % 2
if ($w -le 0 -or $h -le 0) { throw "Nothing to record (region $x,$y ${w}x$h)." }

$Out = [IO.Path]::GetFullPath($Out)
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$video = [IO.Path]::ChangeExtension($Out, ".mp4")

Write-Host "Recording ${w}x$h at $x,$y for $Seconds s..."
& ffmpeg -hide_banner -loglevel error -y -f gdigrab -framerate 30 -draw_mouse 1 `
    -offset_x $x -offset_y $y -video_size "${w}x$h" -i desktop -t $Seconds `
    -c:v libx264 -preset veryfast -crf 12 -pix_fmt yuv444p $video
if ($LASTEXITCODE -ne 0) { throw "Recording failed." }

Write-Host "Converting to GIF..."
# One palette for the whole clip; only changed rectangles are re-dithered, which keeps static
# parts (the window frame, text that isn't moving) identical between frames and the file small.
$filter = "fps=$Fps,scale=${Width}:-1:flags=lanczos,split[a][b];" +
          "[a]palettegen=max_colors=128:stats_mode=diff[p];" +
          "[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle"
& ffmpeg -hide_banner -loglevel error -y -i $video -filter_complex $filter -loop 0 $Out
if ($LASTEXITCODE -ne 0) { throw "GIF conversion failed." }

if (-not $KeepVideo) { Remove-Item $video }
$size = (Get-Item $Out).Length / 1MB
Write-Host ("Wrote {0} ({1:0.0} MB)" -f $Out, $size)
if ($size -gt 5) { Write-Warning "Over 5 MB: try fewer -Seconds, a smaller -Width or a lower -Fps." }
