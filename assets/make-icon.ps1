# Renders logo.svg and logo-small.svg with Microsoft Edge (headless) and packs them into app.ico.
# Also writes logo.png (512 px). Run this again after editing either SVG, then run ..\build.ps1.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
  Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw 'Microsoft Edge was not found; it is needed to render the SVG files.' }

$tmp = Join-Path $here '.render'
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory $tmp | Out-Null

function Render([string]$svgName, [int]$px) {
  $svg = Join-Path $here $svgName
  $html = Join-Path $tmp ($svgName + '.html')
  $png = Join-Path $tmp ($svgName + '.png')
  $src = ([Uri]$svg).AbsoluteUri
  "<!doctype html><html><body style='margin:0;background:transparent;overflow:hidden'><img src='$src' width='$px' height='$px' style='display:block'></body></html>" |
    Set-Content -Path $html -Encoding utf8
  $profileDir = Join-Path $tmp 'profile'
  $edgeArgs = "--headless --disable-gpu --hide-scrollbars --no-first-run --force-device-scale-factor=1 " +
    "--window-size=$px,$px --default-background-color=00000000 --user-data-dir=`"$profileDir`" " +
    "--screenshot=`"$png`" `"$(([Uri]$html).AbsoluteUri)`""
  Start-Process -FilePath $edge -ArgumentList $edgeArgs -Wait
  for ($i = 0; $i -lt 20 -and -not (Test-Path $png); $i++) { Start-Sleep -Milliseconds 250 }
  if (-not (Test-Path $png)) { throw "Edge did not produce $png" }
  return $png
}

function Resize([System.Drawing.Image]$img, [int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
  $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
  $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
  $attr = New-Object System.Drawing.Imaging.ImageAttributes
  $attr.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)   # no dark fringe at the edges
  $g.DrawImage($img, (New-Object System.Drawing.Rectangle 0, 0, $size, $size), 0, 0, $img.Width, $img.Height, [System.Drawing.GraphicsUnit]::Pixel, $attr)
  $g.Dispose()
  return $bmp
}

function ToPng([System.Drawing.Bitmap]$bmp) {
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  return ,$ms.ToArray()
}

# Classic icon image: BITMAPINFOHEADER, 32-bit BGRA rows bottom-up, then a 1-bit mask.
# Sizes below 256 use this form because older readers (GDI+, the C# compiler) can't read PNG there.
function ToDib([System.Drawing.Bitmap]$bmp) {
  $s = $bmp.Width
  $rect = New-Object System.Drawing.Rectangle 0, 0, $s, $s
  $bits = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $px = New-Object byte[] ($s * $s * 4)
  [System.Runtime.InteropServices.Marshal]::Copy($bits.Scan0, $px, 0, $px.Length)
  $bmp.UnlockBits($bits)
  $maskRow = [int]([math]::Floor(($s + 31) / 32)) * 4
  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter $ms
  $bw.Write([uint32]40); $bw.Write([int32]$s); $bw.Write([int32]($s * 2))
  $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
  $bw.Write([uint32]($px.Length + $maskRow * $s))
  $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
  for ($y = $s - 1; $y -ge 0; $y--) { $bw.Write($px, $y * $s * 4, $s * 4) }
  $bw.Write((New-Object byte[] ($maskRow * $s)))
  $bw.Flush()
  return ,$ms.ToArray()
}

function Encode([System.Drawing.Image]$img, [int]$size) {
  $bmp = Resize $img $size
  $data = if ($size -ge 256) { ToPng $bmp } else { ToDib $bmp }
  $bmp.Dispose()
  return ,$data
}

$full = [System.Drawing.Image]::FromFile((Render 'logo.svg' 1024))
$small = [System.Drawing.Image]::FromFile((Render 'logo-small.svg' 1024))

# Detailed artwork from 32 px up, simplified artwork below that.
$images = @()
foreach ($s in 16, 20, 24) { $images += @{ Size = $s; Data = (Encode $small $s) } }
foreach ($s in 32, 40, 48, 64, 128, 256) { $images += @{ Size = $s; Data = (Encode $full $s) } }

$big = Resize $full 512
[System.IO.File]::WriteAllBytes((Join-Path $here 'logo.png'), (ToPng $big))
$big.Dispose()

# .ico file: 6-byte header, one 16-byte entry per image, then the PNG data.
$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $ico
# Sizes below 256 are stored as bitmaps and 256 as PNG.
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($im in $images) {
  $dim = if ($im.Size -ge 256) { 0 } else { $im.Size }
  $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32)
  $w.Write([uint32]$im.Data.Length); $w.Write([uint32]$offset)
  $offset += $im.Data.Length
}
foreach ($im in $images) { $w.Write([byte[]]$im.Data) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $here 'app.ico'), $ico.ToArray())

$full.Dispose(); $small.Dispose()
try { Remove-Item $tmp -Recurse -Force } catch { Write-Host "Could not remove $tmp (delete it by hand)." }
Write-Host "Wrote app.ico ($($images.Count) sizes) and logo.png"
