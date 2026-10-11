<#
.SYNOPSIS
    Generates the MSIX tile/logo PNG set (installer/msix/Assets) from src/DesktopOrganizer.UI/Resources/app.ico.

.DESCRIPTION
    Run once whenever the app icon changes and commit the result: build-msix.ps1 and CI only copy the
    checked-in PNGs, they never regenerate them.

    The source is the single 256x256 PNG frame inside app.ico (read by parsing ICONDIR directly, so the
    full-quality frame is used rather than whatever Icon.ToBitmap would pick). Every output is a 32bppArgb
    bitmap on a transparent background, resized with System.Drawing HighQualityBicubic.

    Generated sets (base name -> variants):
      StoreLogo          scale-100/125/150/200/400                (50 px base, full frame)
      Square150x150Logo  scale-100/125/150/200/400                (150 px base, icon at 66 % centred)
      Square44x44Logo    scale-100/125/150/200/400                (44 px base, full frame)
                         targetsize-16/24/32/48/256, plus _altform-unplated and _altform-lightunplated copies
      Wide310x150Logo    scale-100/200                            (310x150 base, icon at 66 % of height centred)

.EXAMPLE
    pwsh ./installer/msix/Generate-Assets.ps1
#>
param(
    [string]$IconPath = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) "src\DesktopOrganizer.UI\Resources\app.ico"),
    [string]$OutDir = (Join-Path $PSScriptRoot "Assets")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing

$pngSignature = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
$maxFileBytes = 200KB

function Read-LargestPngFrame {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        throw "Icon not found: $Path"
    }

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 6 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0 -or [BitConverter]::ToUInt16($bytes, 2) -ne 1) {
        throw "$Path is not an ICO file (bad ICONDIR header)."
    }

    $count = [int][BitConverter]::ToUInt16($bytes, 4)
    if ([int64]6 + 16 * $count -gt $bytes.Length) {
        throw "$Path is truncated: the ICONDIR declares $count entries but the directory does not fit in $($bytes.Length) bytes."
    }

    $best = $null
    for ($i = 0; $i -lt $count; $i++) {
        $entry = 6 + 16 * $i
        $width = if ($bytes[$entry] -eq 0) { 256 } else { [int]$bytes[$entry] }
        $size = [int64][BitConverter]::ToUInt32($bytes, $entry + 8)
        $offset = [int64][BitConverter]::ToUInt32($bytes, $entry + 12)

        if ($offset + $size -gt $bytes.Length) {
            throw "$Path is corrupt: image $i spans bytes $offset..$($offset + $size) but the file has only $($bytes.Length)."
        }

        # A frame shorter than the PNG signature cannot be a PNG (and must not be probed past its end).
        $isPng = $size -ge $pngSignature.Length
        for ($s = 0; $isPng -and $s -lt $pngSignature.Length; $s++) {
            if ($bytes[$offset + $s] -ne $pngSignature[$s]) { $isPng = $false }
        }

        if ($isPng -and (($null -eq $best) -or ($width -gt $best.Width))) {
            $best = [pscustomobject]@{ Width = $width; Offset = $offset; Size = $size }
        }
    }

    if ($null -eq $best) {
        throw "$Path contains no PNG-compressed frame."
    }

    $frame = New-Object byte[] ([int]$best.Size)
    [Array]::Copy($bytes, $best.Offset, $frame, 0, $best.Size)
    # The leading comma keeps PowerShell from unrolling the byte[] into object[] on return.
    return , $frame
}

function Round-Px {
    param([double]$Value)
    return [int][Math]::Round($Value, [MidpointRounding]::AwayFromZero)
}

function Save-Tile {
    param(
        [System.Drawing.Image]$Source,
        [string]$FileName,
        [int]$Width,
        [int]$Height,
        [double]$IconFraction   # icon side as a fraction of min(Width, Height); 1.0 = full frame
    )

    $side = Round-Px ([Math]::Min($Width, $Height) * $IconFraction)
    $left = [int][Math]::Floor(($Width - $side) / 2)
    $top = [int][Math]::Floor(($Height - $side) / 2)

    $bitmap = New-Object System.Drawing.Bitmap($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

            # A wrap mode of TileFlipXY avoids a semi-transparent fringe on the edges when bicubic samples past the border.
            $attributes = New-Object System.Drawing.Imaging.ImageAttributes
            try {
                $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
                $destination = New-Object System.Drawing.Rectangle($left, $top, $side, $side)
                $graphics.DrawImage($Source, $destination, 0, 0, $Source.Width, $Source.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)
            }
            finally {
                $attributes.Dispose()
            }
        }
        finally {
            $graphics.Dispose()
        }

        $path = Join-Path $OutDir $FileName
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }

    $length = (Get-Item $path).Length
    if ($length -ge $maxFileBytes) {
        throw "$FileName is $length bytes; the limit is $maxFileBytes."
    }

    Write-Host ("  {0,-62} {1,4}x{2,-4} {3,7} bytes" -f $FileName, $Width, $Height, $length)
}

$frameBytes = Read-LargestPngFrame -Path $IconPath
$stream = New-Object System.IO.MemoryStream(, $frameBytes)
$source = [System.Drawing.Image]::FromStream($stream)
try {
    if ($source.Width -ne 256 -or $source.Height -ne 256) {
        throw "Expected a 256x256 source frame, got $($source.Width)x$($source.Height)."
    }

    New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
    Get-ChildItem -Path $OutDir -Filter "*.png" -File | Remove-Item -Force

    Write-Host "Generating MSIX assets from $IconPath into $OutDir"

    $scales = 100, 125, 150, 200, 400

    foreach ($scale in $scales) {
        $px = Round-Px (50 * $scale / 100)
        Save-Tile $source "StoreLogo.scale-$scale.png" $px $px 1.0
    }

    foreach ($scale in $scales) {
        $px = Round-Px (150 * $scale / 100)
        Save-Tile $source "Square150x150Logo.scale-$scale.png" $px $px 0.66
    }

    foreach ($scale in $scales) {
        $px = Round-Px (44 * $scale / 100)
        Save-Tile $source "Square44x44Logo.scale-$scale.png" $px $px 1.0
    }

    foreach ($size in 16, 24, 32, 48, 256) {
        Save-Tile $source "Square44x44Logo.targetsize-$size.png" $size $size 1.0
        Save-Tile $source "Square44x44Logo.targetsize-${size}_altform-unplated.png" $size $size 1.0
        Save-Tile $source "Square44x44Logo.targetsize-${size}_altform-lightunplated.png" $size $size 1.0
    }

    foreach ($scale in 100, 200) {
        Save-Tile $source "Wide310x150Logo.scale-$scale.png" (310 * $scale / 100) (150 * $scale / 100) 0.66
    }
}
finally {
    $source.Dispose()
    $stream.Dispose()
}

Write-Host "Done: $((Get-ChildItem -Path $OutDir -Filter '*.png' -File).Count) files."
