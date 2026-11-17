# Renders every icon the app and its packages need from tools\IconFactory.cs
# (the same drawing as the Windows Forms app), so all platforms show one picture:
#
#   assets\app.ico            Windows .exe icon (multi-size)
#   assets\app.icns           macOS bundle icon
#   assets\app-256.png        Linux .desktop icon, and the window icon
#   assets\icon-<state>.png   64 px window/tray icons with the status badge
#                             (state: off, busy, good, bad)
#
# Run with Windows PowerShell 5.1 (System.Drawing); build.ps1 does it when the
# drawing code is newer than the outputs.

param([string]$OutDir = (Join-Path (Split-Path -Parent $PSScriptRoot) 'assets'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -Path (Join-Path $PSScriptRoot 'IconFactory.cs') -ReferencedAssemblies System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null

$style = [CloudSqlProxyManager.IconFactory]::DefaultStyle

function Render([int]$size, $badge) {
    return [CloudSqlProxyManager.IconFactory]::Render($size, $badge, $style)
}
function Png([System.Drawing.Bitmap]$bmp) {
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return ,$ms.ToArray()
}

# --- PNGs ---
$bmp = Render 256 $null
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'app-256.png'), (Png $bmp)); $bmp.Dispose()
$states = @{
    off  = $null
    busy = [System.Drawing.Color]::FromArgb(242, 153, 0)
    good = [System.Drawing.Color]::FromArgb(30, 142, 62)
    bad  = [System.Drawing.Color]::FromArgb(217, 48, 37)
}
foreach ($name in $states.Keys) {
    $bmp = Render 64 $states[$name]
    [System.IO.File]::WriteAllBytes((Join-Path $OutDir "icon-$name.png"), (Png $bmp)); $bmp.Dispose()
}

# --- Windows .ico: small sizes as 32-bit bitmaps, 256 as PNG ---
function IcoImage([int]$size) {
    $bmp = Render $size $null
    $ms = New-Object System.IO.MemoryStream
    if ($size -ge 256) {
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    } else {
        $bw = New-Object System.IO.BinaryWriter($ms)
        $bw.Write([int32]40); $bw.Write([int32]$size); $bw.Write([int32]($size * 2))
        $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int32]0)
        $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([int32]0)
        for ($y = $size - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $size; $x++) { $bw.Write([int32]$bmp.GetPixel($x, $y).ToArgb()) }
        }
        $maskRow = New-Object byte[] ([int]([Math]::Ceiling($size / 32.0) * 4))
        for ($y = 0; $y -lt $size; $y++) { $bw.Write($maskRow) }
        $bw.Flush()
    }
    $bmp.Dispose()
    return ,$ms.ToArray()
}
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) { ,(IcoImage $s) }
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'app.ico'), $out.ToArray())

# --- macOS .icns: a container of PNGs, one entry per size (type code + length + data) ---
$icns = @(
    @{ type = 'icp4'; size = 16 },  @{ type = 'icp5'; size = 32 },  @{ type = 'icp6'; size = 64 },
    @{ type = 'ic07'; size = 128 }, @{ type = 'ic08'; size = 256 }, @{ type = 'ic09'; size = 512 },
    @{ type = 'ic11'; size = 32 },  @{ type = 'ic12'; size = 64 },  @{ type = 'ic13'; size = 256 },
    @{ type = 'ic14'; size = 512 }, @{ type = 'ic10'; size = 1024 }
)
$entries = foreach ($e in $icns) {
    $bmp = Render $e.size $null
    $data = Png $bmp; $bmp.Dispose()
    ,@{ type = $e.type; data = $data }
}
$total = 8; foreach ($e in $entries) { $total += 8 + $e.data.Length }
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
function BigEndian([uint32]$v) { $b = [BitConverter]::GetBytes($v); [Array]::Reverse($b); return ,$b }
$bw.Write([Text.Encoding]::ASCII.GetBytes('icns')); $bw.Write([byte[]](BigEndian $total))
foreach ($e in $entries) {
    $bw.Write([Text.Encoding]::ASCII.GetBytes($e.type))
    $bw.Write([byte[]](BigEndian ([uint32](8 + $e.data.Length))))
    $bw.Write([byte[]]$e.data)
}
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'app.icns'), $ms.ToArray())

Get-ChildItem $OutDir | ForEach-Object { "{0,-16} {1,8:N0} bytes" -f $_.Name, $_.Length }
