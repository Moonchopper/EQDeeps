# Generates the EQDeeps application icon: a dark rounded tile with three
# meter bars in the app's categorical palette (the live-meter mark).
# Output: src/EQDeeps.Server/Assets/eqdeeps.ico (multi-size, PNG-compressed).
# Repeatable — the icon is generated, not a binary blob with unknown origin.

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$assetDir = Join-Path $root "src\EQDeeps.Server\Assets"
New-Item -ItemType Directory -Force $assetDir | Out-Null

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Dark tile with rounded corners (surface color from the palette).
    $bg = [System.Drawing.Color]::FromArgb(255, 0x1a, 0x1a, 0x19)
    $radius = [Math]::Max(2.0, $size * 0.19)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $radius * 2
    $w = $size - 1
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90)
    $path.AddArc(0, $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.SolidBrush($bg)
    $g.FillPath($brush, $path)

    # Three meter bars, ranked lengths, categorical slots 1-3.
    $colors = @(
        [System.Drawing.Color]::FromArgb(255, 0x39, 0x87, 0xe5),
        [System.Drawing.Color]::FromArgb(255, 0xd9, 0x59, 0x26),
        [System.Drawing.Color]::FromArgb(255, 0x19, 0x9e, 0x70)
    )
    $fractions = @(0.62, 0.44, 0.28)
    $barH = [Math]::Max(1.0, $size * 0.13)
    $gap = [Math]::Max(1.0, $size * 0.09)
    $left = $size * 0.17
    $totalH = 3 * $barH + 2 * $gap
    $y = ($size - $totalH) / 2
    for ($i = 0; $i -lt 3; $i++) {
        $barBrush = New-Object System.Drawing.SolidBrush($colors[$i])
        $barW = $size * $fractions[$i]
        $r = [Math]::Min($barH / 2, [Math]::Max(1.0, $size * 0.04))
        $bar = New-Object System.Drawing.Drawing2D.GraphicsPath
        $bd = $r * 2
        $bar.AddArc($left, $y, $bd, $bd, 90, 180)
        $bar.AddArc($left + $barW - $bd, $y, $bd, $bd, 270, 180)
        $bar.CloseFigure()
        $g.FillPath($barBrush, $bar)
        $barBrush.Dispose()
        $y += $barH + $gap
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $ms.ToArray()
}

# Assemble the ICO container (PNG-compressed entries; fine on Vista+).
# The [byte[]] cast matters: PowerShell unrolls the function's return into
# Object[], which would bind BinaryWriter.Write to the wrong overload.
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @{}
foreach ($s in $sizes) { $pngs[$s] = [byte[]](New-IconPng $s) }

$out = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter($out)
$writer.Write([uint16]0)                 # reserved
$writer.Write([uint16]1)                 # type: icon
$writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($s in $sizes) {
    $bytes = $pngs[$s]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $writer.Write([byte]$dim)            # width (0 = 256)
    $writer.Write([byte]$dim)            # height
    $writer.Write([byte]0)               # palette
    $writer.Write([byte]0)               # reserved
    $writer.Write([uint16]1)             # planes
    $writer.Write([uint16]32)            # bpp
    $writer.Write([uint32]$bytes.Length)
    $writer.Write([uint32]$offset)
    $offset += $bytes.Length
}
foreach ($s in $sizes) { $writer.Write([byte[]]$pngs[$s]) }
$writer.Flush()

$icoPath = Join-Path $assetDir "eqdeeps.ico"
[System.IO.File]::WriteAllBytes($icoPath, $out.ToArray())
Write-Host "Wrote $icoPath ($([math]::Round($out.Length / 1KB, 1)) KB, sizes: $($sizes -join ', '))"
