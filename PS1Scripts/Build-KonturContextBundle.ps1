param(
    [string]$OutputPath = ""
)

$repoRoot = Split-Path $PSScriptRoot -Parent
$defaultOutput = Join-Path $PSScriptRoot "kontur_context_bundle.txt"

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = $defaultOutput
}

$externalScriptRoot = if ($env:PFE_IMPORT_ROOT) {
    Join-Path $env:PFE_IMPORT_ROOT "pfe/scripts"
} else {
    ""
}

$files = @(
    @{
        Group = "REPORT"
        Path = Join-Path $repoRoot "docs\kontur.md"
        Note = "Handoff report and debugging summary."
    }

    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Systems\Map\Rendering\KonturCalculator.cs"
        Note = "Current Unity contour generation logic."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Systems\Map\Rendering\TileCompositor.cs"
        Note = "Current Unity mask/frame sampling and contour-driven compositing."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Systems\Map\Rendering\TileVisualManager.cs"
        Note = "Bridges TileData to TileCompositor and TileRenderer."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Systems\Map\Rendering\TileRenderer.cs"
        Note = "Per-tile rendered object."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Systems\Map\Rendering\RoomVisualController.cs"
        Note = "Room init, debug overlay, debug export."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Editor\RoomVisualControllerEditor.cs"
        Note = "Inspector buttons/toggles for debug export."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Systems\Map\TileDecoder.cs"
        Note = "Decodes room tile strings into TileData."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Systems\Map\TileData.cs"
        Note = "Tile container including kontur and pontur values."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Systems\Map\RoomTemplate.cs"
        Note = "Room template parse path."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Systems\Map\RoomInstance.cs"
        Note = "Runtime room tile grid."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Systems\Map\RoomSetup.cs"
        Note = "Post-generation room finalize flow."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Systems\Map\RoomGenerator.cs"
        Note = "Template-to-room generation."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Systems\Map\DataMigration\AS3ToUnityConverter.cs"
        Note = "AS3 room text conversion."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Systems\Map\WorldCoordinates.cs"
        Note = "Tile/pixel/world coordinate assumptions."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Systems\Map\Rendering\MaterialRenderDatabase.cs"
        Note = "Material-to-mask mapping database."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Systems\Map\Rendering\TileMaskLookup.cs"
        Note = "Mask name to sprite frames lookup."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Systems\Map\Rendering\TileTextureLookup.cs"
        Note = "Texture lookup used by TileCompositor."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Editor\Importers\MaterialDataImporter.cs"
        Note = "Imports material masks from AllData.as."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Editor\Importers\RoomGraphicsImporter.cs"
        Note = "Imports mask helper sprite exports and alias mappings."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Editor\Importers\TileTextureImporter.cs"
        Note = "Texture import settings relevant to compositor readability."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Systems\Map\MapBridge.cs"
        Note = "Runtime wiring from room generation to RoomVisualController."
    }
    @{
        Group = "UNITY"
        Path = Join-Path $repoRoot "Assets\_PFE\Core\Installers\GameLifetimeScope.cs"
        Note = "DI/runtime registration of texture/mask/material databases."
    }

    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "fe\loc\Location.as"
        Note = "Original tileKontur and insKontur source."
    }
    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "fe\graph\Grafon.as"
        Note = "Original setMCT and drawKusok tile mask application."
    }
    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "fe\graph\Material.as"
        Note = "Original material texture/mask resolution."
    }
    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "fe\loc\Form.as"
        Note = "Original form lookup creation."
    }
    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "fe\loc\Tile.as"
        Note = "Original tile fields and mainFrame setup."
    }
    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "fe\loc\Room.as"
        Note = "Original room structure."
    }
    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "fe\loc\Land.as"
        Note = "Original room/world tile lifecycle."
    }
    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "fe\rooms\Rooms.as"
        Note = "Original room XML definitions entry point."
    }
    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "fe\World.as"
        Note = "Original startup/form initialization reference."
    }
    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "..\TileMask.as"
        Note = "Original helper mask class."
    }
    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "..\TileMaskBare.as"
        Note = "Original helper mask class."
    }
    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "..\TileMaskDamaged.as"
        Note = "Original helper mask class."
    }
    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "..\BorderMask.as"
        Note = "Original helper mask class."
    }
    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "..\FloorMask.as"
        Note = "Original helper mask class."
    }
    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "..\SkolMask.as"
        Note = "Original helper mask class."
    }
    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "..\maskBare.as"
        Note = "Original wrapper mask class."
    }
    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "..\maskDamaged.as"
        Note = "Original wrapper mask class."
    }
    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "..\maskBorderBare.as"
        Note = "Original wrapper mask class."
    }
    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "..\maskDirtBorder.as"
        Note = "Original wrapper mask class."
    }
    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "..\maskFloor.as"
        Note = "Original wrapper mask class."
    }
    @{
        Group = "AS3"
        Path = Join-Path $externalScriptRoot "..\maskSkol.as"
        Note = "Original wrapper mask class."
    }
)

$sb = New-Object System.Text.StringBuilder

function Add-Line {
    param([string]$Text = "")
    [void]$sb.AppendLine($Text)
}

Add-Line "# Kontur Context Bundle"
Add-Line "Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Add-Line "Repository root: $repoRoot"
Add-Line "External AS3 root: $externalScriptRoot"
Add-Line ""

foreach ($file in $files) {
    Add-Line ("=" * 100)
    Add-Line "GROUP: $($file.Group)"
    Add-Line "PATH : $($file.Path)"
    Add-Line "NOTE : $($file.Note)"
    Add-Line ("-" * 100)

    if (Test-Path -LiteralPath $file.Path) {
        try {
            $content = Get-Content -LiteralPath $file.Path -Raw -ErrorAction Stop
            Add-Line $content
        }
        catch {
            Add-Line "[ERROR] Failed to read file: $($_.Exception.Message)"
        }
    }
    else {
        Add-Line "[MISSING] File not found."
    }

    Add-Line ""
}

$outputDir = Split-Path $OutputPath -Parent
if (-not [string]::IsNullOrWhiteSpace($outputDir) -and -not (Test-Path -LiteralPath $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
}

Set-Content -LiteralPath $OutputPath -Value $sb.ToString() -Encoding UTF8
Write-Host "Kontur context bundle written to: $OutputPath"
