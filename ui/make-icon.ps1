# Regenerates ui\app.ico (purple tile, phone + signal arcs) at every size Windows asks for.
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File ui\make-icon.ps1
param([string]$Out = (Join-Path $PSScriptRoot 'app.ico'))
Add-Type -AssemblyName System.Drawing

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Max(1, 2 * $r)
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)

    # Background tile: violet gradient
    $m = [single]($s * 0.03)
    $tile = New-RoundRect $m $m ($s - 2 * $m) ($s - 2 * $m) ([single]($s * 0.22))
    $rect = New-Object System.Drawing.RectangleF(0, 0, $s, $s)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, [System.Drawing.Color]::FromArgb(255, 168, 85, 247), [System.Drawing.Color]::FromArgb(255, 91, 33, 182), [single]60)
    $g.FillPath($bg, $tile)
    # soft top highlight
    $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, [System.Drawing.Color]::FromArgb(70, 255, 255, 255), [System.Drawing.Color]::FromArgb(0, 255, 255, 255), [single]90)
    $g.FillPath($hl, $tile)

    $white = [System.Drawing.Color]::White
    $stroke = [single][Math]::Max(1.4, $s * 0.075)

    # Phone body (left of centre)
    $pw = [single]($s * 0.30); $ph = [single]($s * 0.52)
    $px = [single]($s * 0.20); $py = [single](($s - $ph) / 2)
    $phone = New-RoundRect $px $py $pw $ph ([single]($s * 0.07))
    $pen = New-Object System.Drawing.Pen($white, $stroke)
    $pen.LineJoin = 'Round'
    $g.DrawPath($pen, $phone)
    if ($s -ge 24) {
        $dot = [single]([Math]::Max(1.5, $s * 0.055))
        $g.FillEllipse((New-Object System.Drawing.SolidBrush($white)), [single]($px + $pw / 2 - $dot / 2), [single]($py + $ph - $stroke - $dot * 1.3), $dot, $dot)
    }

    # Signal arcs radiating to the right (tether)
    $cx = [single]($px + $pw + $s * 0.02); $cy = [single]($s / 2)
    $arcPen = New-Object System.Drawing.Pen($white, $stroke)
    $arcPen.StartCap = 'Round'; $arcPen.EndCap = 'Round'
    $radii = if ($s -ge 32) { @(0.17, 0.31) } else { @(0.22) }
    foreach ($k in $radii) {
        $r = [single]($s * $k)
        $g.DrawArc($arcPen, [single]($cx - $r), [single]($cy - $r), [single](2 * $r), [single](2 * $r), -50, 100)
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$pngs = foreach ($s in $sizes) { ,(New-IconPng $s) }

# ICO container with PNG-compressed entries (supported since Windows Vista)
$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter($fs)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $b = if ($s -ge 256) { 0 } else { $s }
    $w.Write([byte]$b); $w.Write([byte]$b); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
Write-Host "Wrote $Out ($((Get-Item $Out).Length) bytes, sizes: $($sizes -join ', '))"
