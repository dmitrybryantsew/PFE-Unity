Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path $PSScriptRoot -Parent
$basePath = Join-Path $repoRoot "Assets\_PFE\Art\Imported\TileMasks\MyStoneBorderMask"
$outputPath = Join-Path $PSScriptRoot "CroppedCorners"

if (-not (Test-Path $outputPath)) {
    New-Item -ItemType Directory -Path $outputPath | Out-Null
}

function Crop-Region {
    param(
        [System.Drawing.Bitmap]$Source,
        [int]$X,
        [int]$Y,
        [int]$Width,
        [int]$Height
    )
    $rect = New-Object System.Drawing.Rectangle($X, $Y, $Width, $Height)
    return $Source.Clone($rect, $Source.PixelFormat)
}

function Save-Rotations {
    param(
        [System.Drawing.Bitmap]$Cropped,
        [string]$OutputDir,
        [string]$BaseName
    )

    $rotations = @(
        @{ Suffix = "0";   Flip = [System.Drawing.RotateFlipType]::RotateNoneFlipNone }
        @{ Suffix = "90";  Flip = [System.Drawing.RotateFlipType]::Rotate90FlipNone }
        @{ Suffix = "180"; Flip = [System.Drawing.RotateFlipType]::Rotate180FlipNone }
        @{ Suffix = "270"; Flip = [System.Drawing.RotateFlipType]::Rotate270FlipNone }
    )

    foreach ($rot in $rotations) {
        $copy = $Cropped.Clone(
            (New-Object System.Drawing.Rectangle(0, 0, $Cropped.Width, $Cropped.Height)),
            $Cropped.PixelFormat
        )
        $copy.RotateFlip($rot.Flip)
        $outFile = Join-Path $OutputDir ("{0}_{1}deg.png" -f $BaseName, $rot.Suffix)
        $copy.Save($outFile, [System.Drawing.Imaging.ImageFormat]::Png)
        $copy.Dispose()
        Write-Host "Saved: $outFile"
    }
}

# --- Process MyStoneBorderMask - 2.png (top-left 20x20 corner) ---
$file2 = Join-Path $basePath "2.png"
Write-Host "`nProcessing: $file2"
$bmp2 = New-Object System.Drawing.Bitmap($file2)
$corner2 = Crop-Region -Source $bmp2 -X 0 -Y 0 -Width 20 -Height 20
Save-Rotations -Cropped $corner2 -OutputDir $outputPath -BaseName "Mask2_TopLeft"
$corner2.Dispose()
$bmp2.Dispose()

# --- Process MyStoneBorderMask - 3.png (top-right 20x20 corner) ---
$file3 = Join-Path $basePath "3.png"
Write-Host "`nProcessing: $file3"
$bmp3 = New-Object System.Drawing.Bitmap($file3)
$corner3 = Crop-Region -Source $bmp3 -X ($bmp3.Width - 20) -Y 0 -Width 20 -Height 20
Save-Rotations -Cropped $corner3 -OutputDir $outputPath -BaseName "Mask3_TopRight"
$corner3.Dispose()
$bmp3.Dispose()

Write-Host "`nDone! 8 images saved to: $outputPath"
