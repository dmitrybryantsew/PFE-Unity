# PowerShell script to copy Priority 1 tiles from Flash game to Unity project
# Run this script from the PFE_Unity root folder

$repoRoot = Split-Path $PSScriptRoot -Parent
$sourceDir = if ($env:PFE_IMPORT_ROOT) { Join-Path $env:PFE_IMPORT_ROOT "texture.swf\images" } else { "" }
$destDir = Join-Path $repoRoot "docs\Tiles"

# Create destination folder structure
$folders = @(
    "$destDir\Walls",
    "$destDir\Platforms",
    "$destDir\Stairs",
    "$destDir\Hazards",
    "$destDir\Special",
    "$destDir\Background"
)

Write-Host "Creating folder structure..." -ForegroundColor Cyan
foreach ($folder in $folders) {
    if (!(Test-Path $folder)) {
        New-Item -ItemType Directory -Path $folder -Force | Out-Null
        Write-Host "  Created: $folder" -ForegroundColor Gray
    } else {
        Write-Host "  Exists: $folder" -ForegroundColor DarkGray
    }
}

# Priority 1 files to copy (Core tiles required for basic gameplay)
# Files have numeric prefixes in source, renamed for clarity in destination
$priority1Mappings = @(
    # Walls
    @{ Source = "26_tConcrete.jpg"; Dest = "$destDir\Walls\tWall1.jpg"; Description = "Primary wall tile" },
    @{ Source = "28_tBrick.jpg"; Dest = "$destDir\Walls\tWallBrick.jpg"; Description = "Brick wall variant" },
    @{ Source = "16_tMetal.jpg"; Dest = "$destDir\Walls\tWallMetal.jpg"; Description = "Metal wall" },
    @{ Source = "14_tMetalPlates.jpg"; Dest = "$destDir\Walls\tWallMetalPlates.jpg"; Description = "Metal plates wall" },
    
    # Platforms
    @{ Source = "20_tFloor.jpg"; Dest = "$destDir\Platforms\tPlat1.jpg"; Description = "Primary platform" },
    @{ Source = "39_tRoofFloor.jpg"; Dest = "$destDir\Platforms\tPlat2.jpg"; Description = "Platform variant" },
    @{ Source = "1_tWoodPlanks.jpg"; Dest = "$destDir\Platforms\tPlatWood.jpg"; Description = "Wood platform" },
    @{ Source = "37_tSetka.png"; Dest = "$destDir\Platforms\tPlatGrate.png"; Description = "Grate/platform with holes" }
)

Write-Host "`nCopying Priority 1 tiles..." -ForegroundColor Cyan
Write-Host "Source: $sourceDir" -ForegroundColor Gray
Write-Host "Destination: $destDir" -ForegroundColor Gray
Write-Host ""

$successCount = 0
$failCount = 0
$skippedCount = 0

foreach ($mapping in $priority1Mappings) {
    $sourcePath = Join-Path $sourceDir $mapping.Source
    $destPath = $mapping.Dest
    
    if (Test-Path $sourcePath) {
        try {
            Copy-Item -Path $sourcePath -Destination $destPath -Force
            Write-Host "  [OK] $($mapping.Source) -> $(Split-Path $destPath -Leaf)" -ForegroundColor Green
            Write-Host "       ($($mapping.Description))" -ForegroundColor DarkGray
            $successCount++
        } catch {
            Write-Host "  [FAIL] $($mapping.Source) - Error: $_" -ForegroundColor Red
            $failCount++
        }
    } else {
        Write-Host "  [NOT FOUND] $($mapping.Source)" -ForegroundColor Yellow
        $failCount++
    }
}

# Also copy some additional useful files (Priority 2 - Environmental Variety)
Write-Host "`nCopying Priority 2 tiles (Environmental Variety)..." -ForegroundColor Cyan

$priority2Mappings = @(
    @{ Source = "15_tDarkMetal.jpg"; Dest = "$destDir\Walls\tWallDarkMetal.jpg" },
    @{ Source = "38_tRust.jpg"; Dest = "$destDir\Walls\tWallRust.jpg" },
    @{ Source = "11_tRustPlates.jpg"; Dest = "$destDir\Walls\tWallRustPlates.jpg" },
    @{ Source = "49_tMoss.jpg"; Dest = "$destDir\Walls\tWallMoss.jpg" },
    @{ Source = "32_tCave.jpg"; Dest = "$destDir\Walls\tWallCave.jpg" },
    @{ Source = "33_tStones.jpg"; Dest = "$destDir\Walls\tWallStones.jpg" },
    @{ Source = "34_tDirt.jpg"; Dest = "$destDir\Walls\tWallDirt.jpg" },
    @{ Source = "7_tTiles.jpg"; Dest = "$destDir\Platforms\tPlatTiles.jpg" },
    @{ Source = "22_tSuperCon.jpg"; Dest = "$destDir\Platforms\tPlatSuperCon.jpg" },
    @{ Source = "59_tFloorMetal.jpg"; Dest = "$destDir\Platforms\tPlatMetal.jpg" }
)

foreach ($mapping in $priority2Mappings) {
    $sourcePath = Join-Path $sourceDir $mapping.Source
    $destPath = $mapping.Dest
    
    if (Test-Path $sourcePath) {
        try {
            Copy-Item -Path $sourcePath -Destination $destPath -Force
            Write-Host "  [OK] $($mapping.Source)" -ForegroundColor Green
            $successCount++
        } catch {
            Write-Host "  [FAIL] $($mapping.Source)" -ForegroundColor Red
            $failCount++
        }
    } else {
        Write-Host "  [NOT FOUND] $($mapping.Source) (optional)" -ForegroundColor DarkYellow
        $skippedCount++
    }
}

# Copy background tiles
Write-Host "`nCopying Background tiles..." -ForegroundColor Cyan

$backgroundMappings = @(
    @{ Source = "9_tBackWall.jpg"; Dest = "$destDir\Background\tBackWall.jpg" },
    @{ Source = "19_tConcreteBack.jpg"; Dest = "$destDir\Background\tConcreteBack.jpg" },
    @{ Source = "72_tCloud.jpg"; Dest = "$destDir\Background\tCloud.jpg" }
)

foreach ($mapping in $backgroundMappings) {
    $sourcePath = Join-Path $sourceDir $mapping.Source
    $destPath = $mapping.Dest
    
    if (Test-Path $sourcePath) {
        try {
            Copy-Item -Path $sourcePath -Destination $destPath -Force
            Write-Host "  [OK] $($mapping.Source)" -ForegroundColor Green
            $successCount++
        } catch {
            Write-Host "  [FAIL] $($mapping.Source)" -ForegroundColor Red
            $failCount++
        }
    } else {
        Write-Host "  [NOT FOUND] $($mapping.Source) (optional)" -ForegroundColor DarkYellow
        $skippedCount++
    }
}

# Copy additional useful walls and platforms
Write-Host "`nCopying Additional tiles..." -ForegroundColor Cyan

$additionalMappings = @(
    @{ Source = "10_tConcreteMoss.jpg"; Dest = "$destDir\Walls\tWallConcreteMoss.jpg" },
    @{ Source = "12_tMetalRust.jpg"; Dest = "$destDir\Walls\tWallMetalRust.jpg" },
    @{ Source = "13_tMetalVert.jpg"; Dest = "$destDir\Walls\tWallMetalVert.jpg" },
    @{ Source = "21_tConPlates.jpg"; Dest = "$destDir\Walls\tWallConPlates.jpg" },
    @{ Source = "23_tCracked.jpg"; Dest = "$destDir\Walls\tWallCracked.jpg" },
    @{ Source = "24_tConcreteD.jpg"; Dest = "$destDir\Walls\tWallConcreteD.jpg" },
    @{ Source = "25_tConcreteDirt.jpg"; Dest = "$destDir\Walls\tWallConcreteDirt.jpg" },
    @{ Source = "27_tBlocks.jpg"; Dest = "$destDir\Walls\tWallBlocks.jpg" },
    @{ Source = "30_tStConcrete.jpg"; Dest = "$destDir\Walls\tWallStConcrete.jpg" },
    @{ Source = "35_tShtuk.jpg"; Dest = "$destDir\Walls\tWallShtuk.jpg" },
    @{ Source = "36_tShelf.png"; Dest = "$destDir\Walls\tWallShelf.png" },
    @{ Source = "40_tRoof.jpg"; Dest = "$destDir\Walls\tWallRoof.jpg" },
    @{ Source = "41_tRock.jpg"; Dest = "$destDir\Walls\tWallRock.jpg" },
    @{ Source = "8_tWood.jpg"; Dest = "$destDir\Walls\tWallWood.jpg" },
    @{ Source = "2_tWoodBeam.png"; Dest = "$destDir\Walls\tWoodBeam.png" }
)

foreach ($mapping in $additionalMappings) {
    $sourcePath = Join-Path $sourceDir $mapping.Source
    $destPath = $mapping.Dest
    
    if (Test-Path $sourcePath) {
        try {
            Copy-Item -Path $sourcePath -Destination $destPath -Force
            Write-Host "  [OK] $($mapping.Source)" -ForegroundColor DarkGreen
            $successCount++
        } catch {
            Write-Host "  [FAIL] $($mapping.Source)" -ForegroundColor Red
            $failCount++
        }
    } else {
        Write-Host "  [NOT FOUND] $($mapping.Source) (optional)" -ForegroundColor DarkYellow
        $skippedCount++
    }
}

# Summary
Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Copy Operation Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Successful: $successCount" -ForegroundColor Green
Write-Host "Failed: $failCount" -ForegroundColor $(if ($failCount -gt 0) { "Red" } else { "Gray" })
Write-Host "Skipped (optional): $skippedCount" -ForegroundColor DarkYellow
Write-Host ""
Write-Host "Files copied to: $destDir" -ForegroundColor Cyan
Write-Host "`nNext steps:" -ForegroundColor Yellow
Write-Host "1. Copy these files from docs/Tiles to Assets/_PFE/Art/Tiles in Unity" -ForegroundColor White
Write-Host "2. In Unity, select all imported images" -ForegroundColor White
Write-Host "3. Set Texture Type: Sprite (2D and UI)" -ForegroundColor White
Write-Host "4. Set Pixels Per Unit: 100" -ForegroundColor White
Write-Host "5. Set Filter Mode: Point (no filter)" -ForegroundColor White
Write-Host "6. Create TileAssetDatabase ScriptableObject" -ForegroundColor White
Write-Host "7. Configure tile mappings" -ForegroundColor White
