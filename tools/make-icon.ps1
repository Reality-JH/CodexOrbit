# Generates assets\orbit.ico — violet orbit ring, PNG-compressed multi-size ICO.
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'assets\orbit.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$sizes = @(16, 32, 48, 256)
$pngs = @()
foreach ($n in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $n, $n
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $c = [System.Drawing.Color]::FromArgb(139, 124, 246)
    $pen = New-Object System.Drawing.Pen $c, ([Math]::Max(1.5, $n * 0.08))
    $g.DrawEllipse($pen, $n * 0.12, $n * 0.12, $n * 0.76, $n * 0.76)
    $ang = -40 * [Math]::PI / 180
    $cx = $n / 2 + ($n / 2 - $n * 0.12) * [Math]::Cos($ang)
    $cy = $n / 2 + ($n / 2 - $n * 0.12) * [Math]::Sin($ang)
    $brush = New-Object System.Drawing.SolidBrush $c
    $r = $n * 0.21
    $g.FillEllipse($brush, $cx - $r / 2, $cy - $r / 2, $r, $r)
    $r2 = $n * 0.18
    $g.FillEllipse($brush, $n / 2 - $r2 / 2, $n / 2 - $r2 / 2, $r2, $r2)
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $g.Dispose(); $bmp.Dispose()
}
$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $n = $sizes[$i]; $data = $pngs[$i]
    if ($n -ge 256) { $sz = 0 } else { $sz = $n }
    $bw.Write([byte]$sz)
    $bw.Write([byte]$sz)
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length)
    $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Close()
Write-Output "icon -> $out"
