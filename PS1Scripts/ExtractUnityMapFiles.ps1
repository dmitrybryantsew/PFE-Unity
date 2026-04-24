# ExtractUnityMapFiles.ps1
# Extracts and concatenates your Unity map + movement files for AI context
# Usage: .\ExtractUnityMapFiles.ps1 -SourcePath "<repo>\Assets\_PFE"

param(
    [string]$SourcePath = "",
    [string]$OutputPath = ".\unity_map_context"
)

if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    $SourcePath = Join-Path (Split-Path $PSScriptRoot -Parent) "Assets\_PFE"
}

if (-not (Test-Path $OutputPath)) {
    New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
}

# Files grouped by relevance to map gen + player movement
$fileGroups = @{
    "01_Core" = @(
        "Core\GameManager.cs",
        "Core\GameLoopManager.cs",
        "Core\Installers\GameLifetimeScope.cs",
        "Core\Input\InputReader.cs",
        "Core\Messages\GameMessages.cs"
    )
    "02_MapSystem" = @(
        "Systems\Map\LandMap.cs",
        "Systems\Map\RoomGenerator.cs",
        "Systems\Map\RoomInstance.cs",
        "Systems\Map\RoomTemplate.cs",
        "Systems\Map\WorldBuilder.cs",
        "Systems\Map\WorldConstants.cs",
        "Systems\Map\WorldCoordinates.cs",
        "Systems\Map\TileData.cs",
        "Systems\Map\TilePhysicsType.cs",
        "Systems\Map\TileCollisionSystem.cs",
        "Systems\Map\DoorInstance.cs",
        "Systems\Map\SpawnPoint.cs",
        "Systems\Map\RoomDifficulty.cs",
        "Systems\Map\RoomEnvironment.cs",
        "Systems\Map\MaterialType.cs",
        "Systems\Map\MapGenerationDiagnostics.cs"
    )
    "03_MapRendering" = @(
        "Systems\Map\Rendering\TileRenderer.cs",
        "Systems\Map\Rendering\SimpleTileRenderer.cs",
        "Systems\Map\Rendering\TileVisualManager.cs",
        "Systems\Map\Rendering\RoomVisualController.cs",
        "Systems\Map\Rendering\TileAssetDatabase.cs",
        "Systems\Map\Rendering\TileCollider.cs",
        "Systems\Map\Rendering\MapRendererBootstrapper.cs"
    )
    "04_MapStreaming" = @(
        "Systems\Map\Streaming\RoomTransitionManager.cs",
        "Systems\Map\Streaming\RoomStreamingManager.cs",
        "Systems\Map\Streaming\DoorTrigger.cs",
        "Systems\Map\Streaming\RoomObjectPool.cs"
    )
    "05_MapBridge" = @(
        "Systems\Map\MapBridge.cs"
    )
    "06_PlayerMovement" = @(
        "Entities\Player\PlayerController.cs",
        "Entities\Units\UnitController.cs",
        "Entities\Units\UnitStats.cs",
        "Systems\Physics\PhysicsBody.cs",
        "Systems\Physics\PhysicsMovement.cs",
        "Systems\Physics\PhysicsConfig.cs",
        "Systems\Physics\ParticlePhysics.cs"
    )
    "07_DataMigration" = @(
        "Systems\Map\DataMigration\AS3RoomParser.cs",
        "Systems\Map\DataMigration\AS3RoomData.cs",
        "Systems\Map\DataMigration\AS3ToUnityConverter.cs",
        "Systems\Map\DataMigration\AS3ObjectMapping.cs"
    )
    "08_Camera" = @(
        "Core\CameraFollow.cs"
    )
}

Write-Host "`n=== Extracting Unity Map + Movement Files ===" -ForegroundColor Cyan

$concatAll = ""
$totalLines = 0
$totalFiles = 0
$missingFiles = @()

foreach ($group in $fileGroups.GetEnumerator() | Sort-Object Key) {
    $groupName = $group.Key
    Write-Host "`n--- $groupName ---" -ForegroundColor Yellow
    
    $groupConcat = ""
    
    foreach ($relPath in $group.Value) {
        $fullPath = Join-Path $SourcePath $relPath
        
        if (Test-Path $fullPath) {
            $content = Get-Content $fullPath -Raw -ErrorAction SilentlyContinue
            $lineCount = if ($content) { ($content -split "`n").Count } else { 0 }
            $totalLines += $lineCount
            $totalFiles++
            
            Write-Host ("  OK   {0,-55} {1,5} lines" -f $relPath, $lineCount) -ForegroundColor Green
            
            $header = "`n" + ("=" * 80) + "`n"
            $header += "// FILE: $relPath ($lineCount lines)`n"
            $header += ("=" * 80) + "`n`n"
            
            $groupConcat += $header + $content + "`n`n"
        }
        else {
            $missingFiles += $relPath
            Write-Host ("  MISS {0}" -f $relPath) -ForegroundColor Red
        }
    }
    
    # Save individual group file
    $groupPath = Join-Path $OutputPath "$groupName.txt"
    $groupConcat | Set-Content -Path $groupPath -Encoding UTF8
    
    $concatAll += $groupConcat
}

# Save full concatenation
$fullPath = Join-Path $OutputPath "_ALL_MAP_FILES.txt"
$concatAll | Set-Content -Path $fullPath -Encoding UTF8

# Save a smaller "core only" version (most important files)
$coreFiles = @(
    "Core\GameManager.cs",
    "Core\GameLoopManager.cs",
    "Core\Installers\GameLifetimeScope.cs",
    "Systems\Map\LandMap.cs",
    "Systems\Map\RoomGenerator.cs",
    "Systems\Map\RoomInstance.cs",
    "Systems\Map\RoomTemplate.cs",
    "Systems\Map\WorldBuilder.cs",
    "Systems\Map\WorldConstants.cs",
    "Systems\Map\WorldCoordinates.cs",
    "Systems\Map\TileData.cs",
    "Systems\Map\TileCollisionSystem.cs",
    "Systems\Map\DoorInstance.cs",
    "Systems\Map\MapBridge.cs",
    "Entities\Player\PlayerController.cs",
    "Entities\Units\UnitController.cs",
    "Systems\Physics\PhysicsBody.cs",
    "Systems\Physics\PhysicsMovement.cs",
    "Core\CameraFollow.cs"
)

$coreConcat = ""
$coreLines = 0
foreach ($relPath in $coreFiles) {
    $fp = Join-Path $SourcePath $relPath
    if (Test-Path $fp) {
        $content = Get-Content $fp -Raw -ErrorAction SilentlyContinue
        $lineCount = if ($content) { ($content -split "`n").Count } else { 0 }
        $coreLines += $lineCount
        $coreConcat += "`n// ===== $relPath ($lineCount lines) =====`n`n"
        $coreConcat += $content + "`n`n"
    }
}

$corePath = Join-Path $OutputPath "_CORE_MAP_FILES.txt"
$coreConcat | Set-Content -Path $corePath -Encoding UTF8

# Summary
Write-Host "`n=== EXTRACTION COMPLETE ===" -ForegroundColor Green
Write-Host "  Files found:    $totalFiles"
Write-Host "  Files missing:  $($missingFiles.Count)"
Write-Host "  Total lines:    $totalLines"
Write-Host "  Core lines:     $coreLines"
Write-Host ""
Write-Host "  Output: $OutputPath" -ForegroundColor Yellow
Write-Host "    _ALL_MAP_FILES.txt        - everything ($totalLines lines)"
Write-Host "    _CORE_MAP_FILES.txt       - essential files only ($coreLines lines)"
Write-Host "    01_Core.txt               - GameManager, LifetimeScope"
Write-Host "    02_MapSystem.txt          - LandMap, RoomGenerator, WorldBuilder"
Write-Host "    03_MapRendering.txt       - TileRenderer, RoomVisualController"
Write-Host "    04_MapStreaming.txt        - RoomTransition, DoorTrigger"
Write-Host "    05_MapBridge.txt          - MapBridge"
Write-Host "    06_PlayerMovement.txt     - PlayerController, Physics"
Write-Host "    07_DataMigration.txt      - AS3 parsers/converters"
Write-Host "    08_Camera.txt             - CameraFollow"

if ($missingFiles.Count -gt 0) {
    Write-Host "`n  Missing files:" -ForegroundColor Red
    foreach ($mf in $missingFiles) {
        Write-Host "    $mf" -ForegroundColor Red
    }
}

# Token estimate
$tokenEstimate = [Math]::Round($totalLines * 4.5)
$coreTokenEstimate = [Math]::Round($coreLines * 4.5)
Write-Host "`n  Token estimates:" -ForegroundColor Cyan
Write-Host "    All files:  ~$tokenEstimate tokens"
Write-Host "    Core only:  ~$coreTokenEstimate tokens"

if ($coreTokenEstimate -gt 50000) {
    Write-Host "`n  Core is too large for one prompt." -ForegroundColor Red
    Write-Host "  Share group files separately:" -ForegroundColor Red
    Write-Host "    Prompt 1: 02_MapSystem.txt + 05_MapBridge.txt"
    Write-Host "    Prompt 2: 06_PlayerMovement.txt + 08_Camera.txt"
    Write-Host "    Prompt 3: 03_MapRendering.txt"
    Write-Host "    Prompt 4: 04_MapStreaming.txt"
}
elseif ($coreTokenEstimate -gt 30000) {
    Write-Host "`n  Core fits in one large prompt." -ForegroundColor Yellow
    Write-Host "  Share _CORE_MAP_FILES.txt" -ForegroundColor Yellow
}
else {
    Write-Host "`n  Core fits easily. Share _CORE_MAP_FILES.txt" -ForegroundColor Green
}

Write-Host "`n  NEXT: Share _CORE_MAP_FILES.txt (or group files)" -ForegroundColor Cyan
Write-Host "  alongside the AS3 Location.as you already shared."
