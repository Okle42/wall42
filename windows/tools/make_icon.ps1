# 產生 Wall42.Win\wall42.ico：深色圓角方塊、中央一團深藍打光、幾顆帶白色亮核的藍白粒子與細連線（跟 kang preset 同一個感覺）。
# 16–256 px 各一張（256 用 PNG，其餘用未壓縮 32-bit DIB，舊程式也讀得懂）；小尺寸只畫最亮的幾顆，免得糊成一片。
# 用法：powershell -ExecutionPolicy Bypass -File tools\make_icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\Wall42.Win\wall42.ico'

# 粒子（0..1 座標、亮度、大小權重）；前 4 顆是小尺寸也畫的主幹
$pts = @(
  @(0.30, 0.34, 1.00, 1.00), @(0.70, 0.28, 0.90, 0.85), @(0.62, 0.70, 1.00, 0.95), @(0.26, 0.72, 0.80, 0.75),
  @(0.50, 0.50, 0.70, 0.60), @(0.84, 0.52, 0.55, 0.50), @(0.46, 0.18, 0.50, 0.45), @(0.16, 0.52, 0.45, 0.40),
  @(0.82, 0.82, 0.40, 0.40), @(0.44, 0.86, 0.45, 0.40)
)
$links = @(@(0, 1), @(1, 2), @(2, 3), @(3, 0), @(0, 4), @(4, 2), @(1, 5), @(5, 2), @(0, 6), @(6, 1), @(3, 7), @(7, 0), @(2, 8), @(3, 9), @(9, 2))

function Rounded($x, $y, $w, $h, $r) {
  $p = New-Object Drawing.Drawing2D.GraphicsPath
  $d = [float](2 * $r)
  $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
  $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90); $p.CloseFigure()
  return $p
}

$images = foreach ($s in 16, 24, 32, 48, 64, 128, 256) {
  $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [Drawing.Graphics]::FromImage($bmp); $g.SmoothingMode = 'AntiAlias'; $g.Clear([Drawing.Color]::Transparent)
  $pad = [float]([Math]::Max(0.5, $s * 0.03)); $w = [float]($s - 2 * $pad); $r = [float]([Math]::Max(2, $s * 0.2))
  $body = Rounded $pad $pad $w $w $r
  $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 6, 9, 20))), $body)
  # 中央的深藍打光（徑向漸層）
  $g.SetClip($body)
  $glow = New-Object Drawing.Drawing2D.GraphicsPath
  $gr = $s * 0.62; $glow.AddEllipse([float]($s / 2 - $gr), [float]($s / 2 - $gr), [float](2 * $gr), [float](2 * $gr))
  $pg = New-Object Drawing.Drawing2D.PathGradientBrush $glow
  $pg.CenterColor = [Drawing.Color]::FromArgb(255, 24, 48, 110); $pg.SurroundColors = @([Drawing.Color]::FromArgb(0, 6, 9, 20))
  $g.FillPath($pg, $glow)
  $n = if ($s -le 16) { 4 } elseif ($s -le 32) { 7 } else { $pts.Count }
  $map = { param($p) New-Object Drawing.PointF ([float]($pad + $p[0] * $w)), ([float]($pad + $p[1] * $w)) }
  # 連線：細、半透明藍
  $lw = [float]([Math]::Max(0.8, $s / 90.0))
  foreach ($l in $links) {
    if ($l[0] -ge $n -or $l[1] -ge $n) { continue }
    $a = [int](($pts[$l[0]][2] + $pts[$l[1]][2]) / 2 * $(if ($s -le 24) { 150 } else { 120 }))
    $pen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb($a, 120, 170, 255)), $lw
    $g.DrawLine($pen, (& $map $pts[$l[0]]), (& $map $pts[$l[1]])); $pen.Dispose()
  }
  # 粒子：柔邊藍色光暈＋白色亮核
  for ($i = 0; $i -lt $n; $i++) {
    $p = $pts[$i]; $c = & $map $p
    $halo = [float]([Math]::Max(1.6, $s * 0.085 * $p[3])); $core = [float]([Math]::Max(0.9, $s * 0.026 * $p[3]))
    $hp = New-Object Drawing.Drawing2D.GraphicsPath; $hp.AddEllipse($c.X - $halo, $c.Y - $halo, 2 * $halo, 2 * $halo)
    $hb = New-Object Drawing.Drawing2D.PathGradientBrush $hp
    $hb.CenterColor = [Drawing.Color]::FromArgb([int](200 * $p[2]), 110, 170, 255); $hb.SurroundColors = @([Drawing.Color]::FromArgb(0, 110, 170, 255))
    $g.FillPath($hb, $hp); $hb.Dispose(); $hp.Dispose()
    $g.FillEllipse((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb([int](255 * [Math]::Min(1, $p[2] + 0.15)), 235, 244, 255))), $c.X - $core, $c.Y - $core, 2 * $core, 2 * $core)
  }
  $g.ResetClip()
  $g.DrawPath((New-Object Drawing.Pen ([Drawing.Color]::FromArgb(90, 110, 150, 230)), ([float]([Math]::Max(1, $s / 64.0)))), $body)
  $g.Dispose()
  $ms = New-Object IO.MemoryStream
  if ($s -eq 256) { $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png) }
  else {
    # BITMAPINFOHEADER（高度加倍：XOR＋AND mask）、32-bit BGRA 由下往上、全 0 的 AND mask
    $bw = New-Object IO.BinaryWriter $ms
    $bw.Write([uint32]40); $bw.Write([int32]$s); $bw.Write([int32](2 * $s)); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]0); $bw.Write([uint32]0); $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($yy = $s - 1; $yy -ge 0; $yy--) { for ($xx = 0; $xx -lt $s; $xx++) { $c = $bmp.GetPixel($xx, $yy); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) } }
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4); $bw.Write((New-Object byte[] ($maskRow * $s)))
    $bw.Flush()
  }
  if ($args -contains '-Preview') { $bmp.Save((Join-Path $PSScriptRoot "..\snapshots\icon-$s.png"), [Drawing.Imaging.ImageFormat]::Png) }
  $bmp.Dispose()
  , @($s, $ms.ToArray())
}
$f = New-Object IO.BinaryWriter ([IO.File]::Create($out))
$f.Write([uint16]0); $f.Write([uint16]1); $f.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($p in $images) {
  $s = $p[0]; $data = $p[1]
  $f.Write([byte]($s % 256)); $f.Write([byte]($s % 256)); $f.Write([byte]0); $f.Write([byte]0)
  $f.Write([uint16]1); $f.Write([uint16]32); $f.Write([uint32]$data.Length); $f.Write([uint32]$offset)
  $offset += $data.Length
}
foreach ($p in $images) { $f.Write([byte[]]$p[1]) }
$f.Close()
"wrote $out ($((Get-Item $out).Length) bytes)"
