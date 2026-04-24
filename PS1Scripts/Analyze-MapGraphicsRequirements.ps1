param(
    [string]$ProjectRoot = (Get-Location).Path,
    [string]$As3Root = "",
    [string]$ClassInventoryCsv = "",
    [string]$OutputDir = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-OptionalContent {
    param(
        [string[]]$Candidates
    )

    foreach ($candidate in $Candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate)) {
            continue
        }

        if (Test-Path -LiteralPath $candidate) {
            return @{
                Path = (Resolve-Path -LiteralPath $candidate).Path
                Content = Get-Content -LiteralPath $candidate -Raw -Encoding UTF8
            }
        }
    }

    return $null
}

function Get-UniqueMatches {
    param(
        [string]$Content,
        [string]$Pattern
    )

    if ([string]::IsNullOrWhiteSpace($Content)) {
        return @()
    }

    $matches = [regex]::Matches($Content, $Pattern)
    $values = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($match in $matches) {
        if ($match.Groups.Count -gt 1) {
            $value = $match.Groups[1].Value.Trim()
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                [void]$values.Add($value)
            }
        }
    }

    return @($values | Sort-Object)
}

function Get-ClassInventoryMap {
    param(
        [string]$CsvPath
    )

    $lookup = @{}
    if ([string]::IsNullOrWhiteSpace($CsvPath) -or -not (Test-Path -LiteralPath $CsvPath)) {
        return $lookup
    }

    foreach ($row in (Import-Csv -LiteralPath $CsvPath)) {
        if ([string]::IsNullOrWhiteSpace($row.Class)) {
            continue
        }

        $lookup[$row.Class] = [PSCustomObject]@{
            Class = $row.Class
            File = $row.File
            Package = $row.Package
            Category = $row.Category
        }
    }

    return $lookup
}

function Compare-WithInventory {
    param(
        [string[]]$Names,
        [hashtable]$InventoryLookup
    )

    $results = foreach ($name in ($Names | Sort-Object -Unique)) {
        [PSCustomObject]@{
            Name = $name
            InInventory = $InventoryLookup.ContainsKey($name)
            File = if ($InventoryLookup.ContainsKey($name)) { $InventoryLookup[$name].File } else { "" }
            Category = if ($InventoryLookup.ContainsKey($name)) { $InventoryLookup[$name].Category } else { "" }
        }
    }

    return @($results)
}

function Get-RoomTemplateBackgroundIds {
    param(
        [string]$Root
    )

    $roomAssets = Get-ChildItem -Path (Join-Path $Root "Assets/_PFE/Data/Resources/Rooms") -Recurse -Filter *.asset -ErrorAction SilentlyContinue
    $ids = New-Object 'System.Collections.Generic.HashSet[string]'

    foreach ($asset in $roomAssets) {
        $content = Get-Content -LiteralPath $asset.FullName -Raw -Encoding UTF8
        foreach ($id in (Get-UniqueMatches -Content $content -Pattern 'backgroundRoomId:\s*([^\r\n]+)')) {
            $normalized = $id.Trim()
            if (
                -not [string]::IsNullOrWhiteSpace($normalized) -and
                $normalized -notmatch ':' -and
                $normalized -match '^[A-Za-z0-9_\-\.]+$'
            ) {
                [void]$ids.Add($normalized)
            }
        }
    }

    return @($ids | Where-Object { $_ -ne "" } | Sort-Object)
}

function Get-TileAssetOverlayIds {
    param(
        [string]$Root
    )

    $dbPath = Join-Path $Root "Assets/_PFE/Data/Map/TileAssetDatabase.asset"
    if (-not (Test-Path -LiteralPath $dbPath)) {
        return @()
    }

    $content = Get-Content -LiteralPath $dbPath -Raw -Encoding UTF8
    return Get-UniqueMatches -Content $content -Pattern 'tileId:\s*([^\r\n]+)'
}

function Get-RoomTemplateDecorationIds {
    param(
        [string]$Root
    )

    $roomAssets = Get-ChildItem -Path (Join-Path $Root "Assets/_PFE/Data/Resources/Rooms") -Recurse -Filter *.asset -ErrorAction SilentlyContinue
    $ids = New-Object 'System.Collections.Generic.HashSet[string]'

    foreach ($asset in $roomAssets) {
        $content = Get-Content -LiteralPath $asset.FullName -Raw -Encoding UTF8
        foreach ($id in (Get-UniqueMatches -Content $content -Pattern '(?m)^\s+id:\s*([^\r\n]+)')) {
            $normalized = $id.Trim()
            if (
                -not [string]::IsNullOrWhiteSpace($normalized) -and
                $normalized -notmatch ':' -and
                $normalized -match '^[A-Za-z0-9_\-\.]+$'
            ) {
                [void]$ids.Add($normalized)
            }
        }
    }

    return @($ids | Where-Object { $_ -ne "" } | Sort-Object)
}

function Get-ExistingTileTextureLookupNames {
    param(
        [string]$Root
    )

    $lookupPath = Join-Path $Root "Assets/_PFE/Data/TileTextureLookup.asset"
    if (-not (Test-Path -LiteralPath $lookupPath)) {
        return @()
    }

    $content = Get-Content -LiteralPath $lookupPath -Raw -Encoding UTF8
    return Get-UniqueMatches -Content $content -Pattern '(?m)^\s+textureName:\s*([^\r\n]+)'
}

function New-ManifestEntry {
    param(
        [string]$Category,
        [string]$Name,
        [string]$SourceType,
        [string]$SourceRef,
        [bool]$PresentInUnity = $false,
        [string]$InventoryFile = "",
        [string]$Notes = ""
    )

    return [PSCustomObject]@{
        category = $Category
        name = $Name
        sourceType = $SourceType
        sourceRef = $SourceRef
        presentInUnity = $PresentInUnity
        inventoryFile = $InventoryFile
        notes = $Notes
    }
}

function Write-ImportManifestCsv {
    param(
        [string]$Path,
        [object[]]$Entries
    )

    $Entries |
        Sort-Object category, name |
        Export-Csv -LiteralPath $Path -NoTypeInformation -Encoding UTF8
}

function Write-MarkdownReport {
    param(
        [string]$Path,
        $Report
    )

    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add("# Map Graphics Requirements Report")
    $lines.Add("")
    $lines.Add("## Why this report exists")
    $lines.Add("")
    $lines.Add("This narrows the graphics migration work to the smallest AS3 source set needed for room and tile rendering parity.")
    $lines.Add("")
    $lines.Add("## Required data contract")
    $lines.Add("")

    foreach ($section in $Report["DataContract"].GetEnumerator()) {
        $lines.Add("### $($section.Key)")
        foreach ($item in $section.Value) {
            $lines.Add("- $item")
        }
        $lines.Add("")
    }

    $lines.Add("## Source files inspected")
    $lines.Add("")
    foreach ($prop in $Report["SourceFiles"].GetEnumerator()) {
        $lines.Add("- $($prop.Key): $($prop.Value)")
    }
    $lines.Add("")

    $lines.Add("## Unity-side usage summary")
    $lines.Add("")
    $lines.Add("- Background room IDs used by room templates: $(@($Report["UnityUsage"]["BackgroundRoomIds"]).Count)")
    $lines.Add("- Background decoration IDs used by room templates: $(@($Report["UnityUsage"]["BackgroundDecorationIds"]).Count)")
    $lines.Add("- Tile overlay sprite IDs currently present in TileAssetDatabase: $(@($Report["UnityUsage"]["TileOverlayIds"]).Count)")
    $lines.Add("- Tile textures currently present in TileTextureLookup: $(@($Report["UnityUsage"]["TileTextureNames"]).Count)")
    $lines.Add("")

    $lines.Add("## AS3 discovery summary")
    $lines.Add("")
    $lines.Add("- Room option keys referenced in Location.as: $(@($Report["As3Discovery"]["LocationOptionKeys"]).Count)")
    $lines.Add("- Symbols instantiated in Grafon.as: $(@($Report["As3Discovery"]["GrafonInstantiatedSymbols"]).Count)")
    $lines.Add("- Material IDs found: $(@($Report["As3Discovery"]["MaterialIds"]).Count)")
    $lines.Add("- Material textures found: $(@($Report["As3Discovery"]["MaterialTextures"]).Count)")
    $lines.Add("- Material masks found: $(@($Report["As3Discovery"]["MaterialMasks"]).Count)")
    $lines.Add("- Material filters found: $(@($Report["As3Discovery"]["MaterialFilters"]).Count)")
    $lines.Add("")

    $lines.Add("## Priority extraction targets")
    $lines.Add("")
    foreach ($item in $Report["PriorityTargets"]) {
        $lines.Add("- $item")
    }
    $lines.Add("")

    $lines.Add("## Unity Pipeline Entry Points")
    $lines.Add("")
    foreach ($item in $Report["UnityPipelineFiles"]) {
        $lines.Add("- $item")
    }
    $lines.Add("")

    $lines.Add("## External Agent Workflow")
    $lines.Add("")
    foreach ($item in $Report["AgentWorkflow"]) {
        $lines.Add("- $item")
    }
    $lines.Add("")

    if (@($Report["InventoryComparisons"]["BackgroundSymbols"]).Count -gt 0) {
        $lines.Add("## Background symbol coverage")
        $lines.Add("")
        foreach ($item in $Report["InventoryComparisons"]["BackgroundSymbols"]) {
            $status = if ($item.InInventory) { "found" } else { "missing" }
            $suffix = if ($item.File) { " -> $($item.File)" } else { "" }
            $lines.Add("- $($item.Name): $status$suffix")
        }
        $lines.Add("")
    }

    if (@($Report["InventoryComparisons"]["MaskSymbols"]).Count -gt 0) {
        $lines.Add("## Mask symbol coverage")
        $lines.Add("")
        foreach ($item in $Report["InventoryComparisons"]["MaskSymbols"]) {
            $status = if ($item.InInventory) { "found" } else { "missing" }
            $suffix = if ($item.File) { " -> $($item.File)" } else { "" }
            $lines.Add("- $($item.Name): $status$suffix")
        }
        $lines.Add("")
    }

    if (@($Report["InventoryComparisons"]["GrafonSymbols"]).Count -gt 0) {
        $lines.Add("## Grafon symbol coverage")
        $lines.Add("")
        foreach ($item in $Report["InventoryComparisons"]["GrafonSymbols"]) {
            $status = if ($item.InInventory) { "found" } else { "missing" }
            $suffix = if ($item.File) { " -> $($item.File)" } else { "" }
            $lines.Add("- $($item.Name): $status$suffix")
        }
        $lines.Add("")
    }

    Set-Content -LiteralPath $Path -Value $lines -Encoding UTF8
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $ProjectRoot "unity_analysis/graphics_requirements"
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$locationCandidates = @()
if (-not [string]::IsNullOrWhiteSpace($As3Root)) {
    $locationCandidates += (Join-Path $As3Root "fe/loc/Location.as")
}
$locationCandidates += (Join-Path $ProjectRoot "Location.as")
$locationFile = Get-OptionalContent -Candidates $locationCandidates

$grafonCandidates = @()
if (-not [string]::IsNullOrWhiteSpace($As3Root)) {
    $grafonCandidates += (Join-Path $As3Root "fe/graph/Grafon.as")
}
$grafonFile = Get-OptionalContent -Candidates $grafonCandidates

$allDataCandidates = @()
if (-not [string]::IsNullOrWhiteSpace($As3Root)) {
    $allDataCandidates += (Join-Path $As3Root "fe/AllData.as")
}
$allDataCandidates += (Join-Path $ProjectRoot "AllData.as")
$allDataFile = Get-OptionalContent -Candidates $allDataCandidates

$inventoryLookup = Get-ClassInventoryMap -CsvPath $ClassInventoryCsv

$locationOptionKeys = if ($locationFile) {
    Get-UniqueMatches -Content $locationFile.Content -Pattern 'nroom\.options\.@([A-Za-z0-9_]+)'
} else {
    @()
}

$grafonNewSymbolsRaw = if ($grafonFile) {
    Get-UniqueMatches -Content $grafonFile.Content -Pattern 'new\s+([A-Za-z_][A-Za-z0-9_]*)\s*\('
} else {
    @()
}

$excludedGrafonSymbols = @(
    "Array", "Bitmap", "BitmapData", "BlurFilter", "ColorTransform", "Dictionary",
    "GlowFilter", "Matrix", "MovieClip", "Point", "Rectangle", "Shape", "Sprite", "TextField"
)
$grafonInstantiatedSymbols = @($grafonNewSymbolsRaw | Where-Object { $excludedGrafonSymbols -notcontains $_ } | Sort-Object -Unique)

$materialIds = @()
$materialTextures = @()
$materialMasks = @()
$materialFilters = @()

if ($allDataFile) {
    $materialIds = Get-UniqueMatches -Content $allDataFile.Content -Pattern "<mat\b[^>]*\bid=['""]([^'""]+)['""]"
    $materialTextures = @(
        (Get-UniqueMatches -Content $allDataFile.Content -Pattern "<(?:main|border|floor)\b[^>]*\bt=['""]([^'""]+)['""]") +
        (Get-UniqueMatches -Content $allDataFile.Content -Pattern "<(?:main|border|floor)\b[^>]*\btex=['""]([^'""]+)['""]")
    ) | Sort-Object -Unique
    $materialMasks = @(
        (Get-UniqueMatches -Content $allDataFile.Content -Pattern "<(?:main|border|floor)\b[^>]*\bmask=['""]([^'""]+)['""]")
    ) | Sort-Object -Unique
    $materialFilters = @(
        (Get-UniqueMatches -Content $allDataFile.Content -Pattern "<mat\b[^>]*\bfilter=['""]([^'""]+)['""]") +
        (Get-UniqueMatches -Content $allDataFile.Content -Pattern "<filter\b[^>]*\bid=['""]([^'""]+)['""]")
    ) | Sort-Object -Unique
}

$unityBackgroundRoomIds = Get-RoomTemplateBackgroundIds -Root $ProjectRoot
$unityBackgroundDecorationIds = Get-RoomTemplateDecorationIds -Root $ProjectRoot
$unityOverlayIds = Get-TileAssetOverlayIds -Root $ProjectRoot
$unityTileTextureNames = Get-ExistingTileTextureLookupNames -Root $ProjectRoot
$allDataSourceRef = if ($allDataFile) { $allDataFile.Path } else { "AllData.as" }
$grafonSourceRef = if ($grafonFile) { $grafonFile.Path } else { "Grafon.as" }
$classInventorySourceRef = if ([string]::IsNullOrWhiteSpace([string]$ClassInventoryCsv)) { "" } else { [string]$ClassInventoryCsv }
$locationSourceRef = if ($locationFile) { $locationFile.Path } else { "" }
$grafonFileSourceRef = if ($grafonFile) { $grafonFile.Path } else { "" }
$allDataFileSourceRef = if ($allDataFile) { $allDataFile.Path } else { "" }

$backgroundCoverage = Compare-WithInventory -Names $unityBackgroundRoomIds -InventoryLookup $inventoryLookup
$backgroundCoverageLookup = @{}
foreach ($item in $backgroundCoverage) {
    $backgroundCoverageLookup[$item.Name] = $item
}

$decorationCoverage = Compare-WithInventory -Names $unityBackgroundDecorationIds -InventoryLookup $inventoryLookup
$decorationCoverageLookup = @{}
foreach ($item in $decorationCoverage) {
    $decorationCoverageLookup[$item.Name] = $item
}

$maskCoverage = Compare-WithInventory -Names $materialMasks -InventoryLookup $inventoryLookup
$grafonCoverage = Compare-WithInventory -Names $grafonInstantiatedSymbols -InventoryLookup $inventoryLookup

$importManifest = New-Object System.Collections.Generic.List[object]

foreach ($name in $unityBackgroundRoomIds) {
    $inventory = $backgroundCoverageLookup[$name]
    $importManifest.Add((New-ManifestEntry -Category "BackgroundRoomSymbol" -Name $name -SourceType "RoomTemplate.backgroundRoomId" -SourceRef "Assets/_PFE/Data/Resources/Rooms/**/*.asset" -PresentInUnity $false -InventoryFile $inventory.File -Notes "Needed for room-level backdrop / background renderer"))
}

foreach ($name in $unityBackgroundDecorationIds) {
    $inventory = $decorationCoverageLookup[$name]
    $importManifest.Add((New-ManifestEntry -Category "BackgroundDecorationSymbol" -Name $name -SourceType "RoomTemplate.backgroundDecorations[].id" -SourceRef "Assets/_PFE/Data/Resources/Rooms/**/*.asset" -PresentInUnity $false -InventoryFile $inventory.File -Notes "Needed for <back> decoration placements"))
}

foreach ($name in $materialTextures) {
    $present = $unityTileTextureNames -contains $name
    $importManifest.Add((New-ManifestEntry -Category "MaterialTexture" -Name $name -SourceType "AllData.mat.<main|border|floor>.tex" -SourceRef $allDataSourceRef -PresentInUnity $present -Notes "Texture should map into TileTextureLookup"))
}

foreach ($item in $maskCoverage) {
    $importManifest.Add((New-ManifestEntry -Category "MaterialMaskSymbol" -Name $item.Name -SourceType "AllData.mat.<main|border|floor>.mask" -SourceRef $allDataSourceRef -PresentInUnity $false -InventoryFile $item.File -Notes "Likely exported as DefineSprite_*_$($item.Name) or class named $($item.Name)"))
}

foreach ($item in $grafonCoverage) {
    $importManifest.Add((New-ManifestEntry -Category "GrafonSymbol" -Name $item.Name -SourceType "new Symbol() in Grafon.as" -SourceRef $grafonSourceRef -PresentInUnity $false -InventoryFile $item.File -Notes "Check if this is room/tile related before importing"))
}

$importManifest.Add((New-ManifestEntry -Category "OverlayContainer" -Name "tileFront" -SourceType "Grafon overlay container / vid frames" -SourceRef "Grafon.as + tile visual ids" -PresentInUnity (@($unityOverlayIds).Count -gt 0) -Notes "Need actual frame extraction for vid/vid2 overlays"))
$importManifest.Add((New-ManifestEntry -Category "WaterSymbol" -Name "tileVoda" -SourceType "Grafon water layer" -SourceRef "Grafon.as" -PresentInUnity $false -Notes "Need water sprite/animation import or fallback renderer"))

$dataContract = [ordered]@{}
$dataContract["RoomEnvironment"] = @(
    "backwall", "backform", "transpfon", "color", "colorfon", "ramka", "vis", "lon",
    "dark", "retdark", "sky", "music", "wlevel", "wtip", "wopac", "wdam", "wtipdam", "rad"
)
$dataContract["TileForms"] = @(
    "front", "back", "vid", "rear", "phis", "shelf", "diagon", "stair", "mat", "hp", "thre", "indestruct", "lurk", "mirror"
)
$dataContract["MaterialRendering"] = @(
    "material id", "ed / rear", "main texture", "alt texture", "border texture", "floor texture",
    "main mask", "border mask", "floor mask", "filter type"
)
$dataContract["SymbolSets"] = @(
    "tileFront frames / visual overlay ids",
    "tileVoda water symbol",
    "room background symbols from backgroundRoomId",
    "background decoration symbols from <back>",
    "mask classes used by materials",
    "symbols instantiated directly by Grafon"
)

$sourceFiles = [ordered]@{}
$sourceFiles["LocationAs"] = $locationSourceRef
$sourceFiles["GrafonAs"] = $grafonFileSourceRef
$sourceFiles["AllDataAs"] = $allDataFileSourceRef
$sourceFiles["ClassInventoryCsv"] = $classInventorySourceRef
$sourceFiles["RoomTemplates"] = (Join-Path $ProjectRoot "Assets/_PFE/Data/Resources/Rooms")
$sourceFiles["TileAssetDatabase"] = (Join-Path $ProjectRoot "Assets/_PFE/Data/Map/TileAssetDatabase.asset")

$unityUsage = [ordered]@{}
$unityUsage["BackgroundRoomIds"] = @($unityBackgroundRoomIds)
$unityUsage["BackgroundDecorationIds"] = @($unityBackgroundDecorationIds)
$unityUsage["TileOverlayIds"] = @($unityOverlayIds)
$unityUsage["TileTextureNames"] = @($unityTileTextureNames)

$as3Discovery = [ordered]@{}
$as3Discovery["LocationOptionKeys"] = @($locationOptionKeys)
$as3Discovery["GrafonInstantiatedSymbols"] = @($grafonInstantiatedSymbols)
$as3Discovery["MaterialIds"] = @($materialIds)
$as3Discovery["MaterialTextures"] = @($materialTextures)
$as3Discovery["MaterialMasks"] = @($materialMasks)
$as3Discovery["MaterialFilters"] = @($materialFilters)

$inventoryComparisons = [ordered]@{}
$inventoryComparisons["BackgroundSymbols"] = @($backgroundCoverage)
$inventoryComparisons["DecorationSymbols"] = @($decorationCoverage)
$inventoryComparisons["MaskSymbols"] = @($maskCoverage)
$inventoryComparisons["GrafonSymbols"] = @($grafonCoverage)

$report = [ordered]@{}
$report["GeneratedAt"] = (Get-Date).ToString("s")
$report["DataContract"] = $dataContract
$report["SourceFiles"] = $sourceFiles
$report["UnityUsage"] = $unityUsage
$report["As3Discovery"] = $as3Discovery
$report["InventoryComparisons"] = $inventoryComparisons
$report["PriorityTargets"] = @(
    "Room background symbols referenced by Unity room templates",
    "Background decoration symbols referenced by room <back> data",
    "Material textures, masks, and filters from AllData / Material data",
    "tileFront overlay frames used by vid / vid2",
    "Water symbol and water rendering dependencies",
    "Direct Grafon-instantiated symbols not yet represented in Unity import data"
)
$report["UnityPipelineFiles"] = @(
    "Assets/_PFE/Systems/Map/DataMigration/AS3RoomParser.cs",
    "Assets/_PFE/Systems/Map/DataMigration/AS3RoomData.cs",
    "Assets/_PFE/Systems/Map/DataMigration/AS3ToUnityConverter.cs",
    "Assets/_PFE/Systems/Map/DataMigration/AllDataParser.cs",
    "Assets/_PFE/Editor/RoomTemplateImporterWindow.cs",
    "Assets/_PFE/Editor/Importers/MaterialDataImporter.cs",
    "Assets/_PFE/Editor/Importers/TileTextureImporter.cs",
    "Assets/_PFE/Systems/Map/Rendering/TileAssetDatabase.cs",
    "Assets/_PFE/Systems/Map/Rendering/TileTextureLookup.cs",
    "Assets/_PFE/Systems/Map/Rendering/MaterialRenderDatabase.cs",
    "Assets/_PFE/Systems/Map/Rendering/RoomVisualController.cs"
)
$report["AgentWorkflow"] = @(
    "Use this report to build a first-pass import manifest, not to browse all AS3 files manually.",
    "Parse only Location.as, Grafon.as, AllData.as, room XML/Rooms*.as, and optional class inventory CSV.",
    "For exported sprites, resolve by explicit class suffix first (example: DefineSprite_4829_BorderMask -> BorderMask).",
    "Emit deterministic outputs: JSON manifest, CSV manifest, copy/extract logs, and a missing-items report.",
    "Do not import all 1655 exported sprites; import only manifest-backed symbols first."
)
$report["ImportManifest"] = [object[]]$importManifest.ToArray()

$jsonPath = Join-Path $OutputDir "map_graphics_requirements.json"
$mdPath = Join-Path $OutputDir "map_graphics_requirements.md"
$manifestJsonPath = Join-Path $OutputDir "graphics_import_manifest.json"
$manifestCsvPath = Join-Path $OutputDir "graphics_import_manifest.csv"

$report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
Write-MarkdownReport -Path $mdPath -Report $report
($report["ImportManifest"]) | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestJsonPath -Encoding UTF8
Write-ImportManifestCsv -Path $manifestCsvPath -Entries $report["ImportManifest"]

Write-Host "Map graphics requirements report written to:" -ForegroundColor Green
Write-Host "  $jsonPath"
Write-Host "  $mdPath"
Write-Host "  $manifestJsonPath"
Write-Host "  $manifestCsvPath"
