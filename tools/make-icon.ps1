#Requires -Version 7.0
<#
.SYNOPSIS
    ClipInspect のアイコンを描き、icon.png（元画像）と Resources\icon.ico を作る。

.DESCRIPTION
    図柄: 紫の角丸の台座、白いクリップボード、黄色のクリップ、右下の虫眼鏡（黄色の太い輪・濃い柄）。
    小さいサイズでも「白い板＋黄色の輪」の形が残るよう、線を太く、要素を3つに絞っている。
    文字は使わない。既存アプリ（ClipCompose: 青の台座とオレンジのクリップボード、HotkeyInspector: 青緑のキー）と色・形を変えた。

    出力:
      icon.png            1024px の元画像（MSIX の画像は build-msix.ps1 がこれから作る）
      Resources\icon.ico  16/20/24/32/40/48/64px は 32bpp BMP、256px は PNG
      publish\icon-check\ 確認用の各サイズの PNG（Git 管理外）

    小さいサイズは元画像を縮めず、そのサイズで描き直す（細部が潰れないように）。
    Python・ImageMagick に依存させないため、PowerShell 7 の System.Drawing で描く。

.EXAMPLE
    pwsh -File tools\make-icon.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.Drawing

$projectRoot = Split-Path -Parent $PSScriptRoot

function New-RoundedPath([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = [single]($r * 2)
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Get-Color([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

# size px の正方形に描く。$small は 32px 以下（行の線を省き、輪をさらに太くする）。
function Draw-Icon([int]$size) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    try {
        $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)
        $s = [single]($size / 100.0)
        $small = $size -le 32

        # 台座（紫のグラデーション）。小さいサイズは余白を詰める。
        $m = if ($small) { 1.5 } else { 4 }
        $tile = New-RoundedPath ($m * $s) ($m * $s) ((100 - 2 * $m) * $s) ((100 - 2 * $m) * $s) (20 * $s)
        $grad = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
            [System.Drawing.PointF]::new(0, 0), [System.Drawing.PointF]::new($size, $size),
            (Get-Color '#8B5CF6'), (Get-Color '#4C1D95'))
        $g.FillPath($grad, $tile)
        $grad.Dispose(); $tile.Dispose()

        # クリップボードの板（白）。
        $board = New-RoundedPath (18 * $s) (17 * $s) (52 * $s) (66 * $s) (7 * $s)
        $white = [System.Drawing.SolidBrush]::new((Get-Color '#FFFFFF'))
        $g.FillPath($white, $board)
        $board.Dispose()

        # 本文の行（大きいサイズだけ）。
        if (-not $small) {
            $line = [System.Drawing.SolidBrush]::new((Get-Color '#C4B5FD'))
            foreach ($ly in 36, 47) {
                $lp = New-RoundedPath (27 * $s) ($ly * $s) (30 * $s) (5 * $s) (2.5 * $s)
                $g.FillPath($line, $lp); $lp.Dispose()
            }
            $line.Dispose()
        }

        # クリップ（黄色）。
        $clip = New-RoundedPath (31 * $s) (10 * $s) (26 * $s) (14 * $s) (4 * $s)
        $amber = [System.Drawing.SolidBrush]::new((Get-Color '#FBBF24'))
        $g.FillPath($amber, $clip)
        $clipEdge = [System.Drawing.Pen]::new((Get-Color '#4C1D95'), [single](2.2 * $s))
        if (-not $small) { $g.DrawPath($clipEdge, $clip) }
        $clip.Dispose(); $clipEdge.Dispose()

        # 虫眼鏡: 柄（濃い色）→ レンズの縁取り（濃い色）→ 黄色の輪 → レンズ（淡い黄色）。
        $cx = [single](64 * $s); $cy = [single](62 * $s)
        $r = if ($small) { [single](19 * $s) } else { [single](17 * $s) }
        $ring = if ($small) { [single](9 * $s) } else { [single](7.5 * $s) }
        $handleWidth = if ($small) { 13 } else { 11 }
        $handle = [System.Drawing.Pen]::new((Get-Color '#1E1B4B'), [single]($handleWidth * $s))
        $handle.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $handle.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $g.DrawLine($handle, [single]($cx + $r * 0.7), [single]($cy + $r * 0.7), [single](88 * $s), [single](86 * $s))
        $handle.Dispose()
        $outline = [System.Drawing.SolidBrush]::new((Get-Color '#1E1B4B'))
        $ro = [single]($r + $ring / 2 + 2.2 * $s)
        $g.FillEllipse($outline, $cx - $ro, $cy - $ro, 2 * $ro, 2 * $ro)
        $outline.Dispose()
        $ringPen = [System.Drawing.Pen]::new((Get-Color '#FBBF24'), $ring)
        $lens = [System.Drawing.SolidBrush]::new((Get-Color '#FEF3C7'))
        $g.FillEllipse($lens, $cx - $r, $cy - $r, 2 * $r, 2 * $r)
        $g.DrawEllipse($ringPen, $cx - $r, $cy - $r, 2 * $r, 2 * $r)
        # レンズの光（大きいサイズだけ）。
        if (-not $small) {
            $shine = [System.Drawing.Pen]::new((Get-Color '#FFFFFF'), [single](3 * $s))
            $shine.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
            $shine.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
            $g.DrawArc($shine, $cx - $r * 0.6, $cy - $r * 0.6, $r * 1.2, $r * 1.2, 200, 70)
            $shine.Dispose()
        }
        $ringPen.Dispose(); $lens.Dispose(); $white.Dispose(); $amber.Dispose()
    }
    finally { $g.Dispose() }
    return $bmp
}

function Get-Pixels([System.Drawing.Bitmap]$bitmap) {
    $rect = [System.Drawing.Rectangle]::new(0, 0, $bitmap.Width, $bitmap.Height)
    $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $pixels = [byte[]]::new([Math]::Abs($data.Stride) * $bitmap.Height)
        [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $pixels, 0, $pixels.Length)
        return , $pixels
    }
    finally { $bitmap.UnlockBits($data) }
}

function ConvertTo-DibBytes([System.Drawing.Bitmap]$bitmap) {
    $w = $bitmap.Width; $h = $bitmap.Height
    $pixels = Get-Pixels $bitmap
    $stride = $w * 4
    $maskStride = [int]([Math]::Floor(($w + 31) / 32) * 4)
    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)
    # BITMAPINFOHEADER。ICO 内の DIB は XOR + AND の 2 枚分として高さを 2 倍で書く決まり。
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $bw.Write([int]($stride * $h + $maskStride * $h))
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($pixels, $y * $stride, $stride) }
    $row = [byte[]]::new($maskStride)
    for ($y = $h - 1; $y -ge 0; $y--) {
        [Array]::Clear($row)
        for ($x = 0; $x -lt $w; $x++) {
            if ($pixels[$y * $stride + $x * 4 + 3] -eq 0) {
                $row[[int][Math]::Floor($x / 8)] = $row[[int][Math]::Floor($x / 8)] -bor (0x80 -shr ($x % 8))
            }
        }
        $bw.Write($row)
    }
    $bw.Flush()
    return , $ms.ToArray()
}

function ConvertTo-PngBytes([System.Drawing.Bitmap]$bitmap) {
    $ms = [System.IO.MemoryStream]::new()
    $bitmap.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    return , $ms.ToArray()
}

# ---- 元画像 ----
$master = Draw-Icon 1024
$master.Save((Join-Path $projectRoot 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$master.Dispose()

# ---- 確認用 PNG ----
$checkDir = Join-Path $projectRoot 'publish\icon-check'
New-Item -ItemType Directory -Force $checkDir | Out-Null

# ---- ICO ----
$frames = [System.Collections.Generic.List[object]]::new()
foreach ($size in 16, 20, 24, 32, 40, 48, 64, 256) {
    $bmp = Draw-Icon $size
    $bmp.Save((Join-Path $checkDir "icon-$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = if ($size -ge 256) { ConvertTo-PngBytes $bmp } else { ConvertTo-DibBytes $bmp }
    $frames.Add([pscustomobject]@{ Size = $size; Bytes = $bytes })
    $bmp.Dispose()
}
$ico = [System.IO.MemoryStream]::new()
$w = [System.IO.BinaryWriter]::new($ico)
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$f.Bytes.Length); $w.Write([int]$offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $w.Write($f.Bytes) }
$w.Flush()
New-Item -ItemType Directory -Force (Join-Path $projectRoot 'Resources') | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $projectRoot 'Resources\icon.ico'), $ico.ToArray())
# ---- MSIX の小さい画像（Square44x44Logo.targetsize-*。build-msix.ps1 が使う） ----
$smallDir = Join-Path $projectRoot 'installer\msix\icon-small'
New-Item -ItemType Directory -Force $smallDir | Out-Null
foreach ($size in 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256) {
    $bmp = Draw-Icon $size
    $bmp.Save((Join-Path $smallDir "icon-$size.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}
Write-Host "icon.png / Resources\icon.ico / installer\msix\icon-small を作成しました（確認用: $checkDir）"
