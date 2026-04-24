# PowerShell Script to Create Context File for Coordinate/Dimension Fix
# This script combines specified CS and AS files into a single context file

param(
    [string]$OutputPath = "context_output.md",
    [string]$UnityProjectPath = "",
    [string]$AS3ScriptsPath = ""
)

if ([string]::IsNullOrWhiteSpace($UnityProjectPath)) {
    $UnityProjectPath = $PSScriptRoot
}

if ([string]::IsNullOrWhiteSpace($AS3ScriptsPath) -and $env:PFE_IMPORT_ROOT) {
    $AS3ScriptsPath = Join-Path $env:PFE_IMPORT_ROOT "pfe/scripts"
}

# Define the files to include
# Must have (for the coordinate/dimension fix)
$mustHaveCSFiles = @(
    "Assets/_PFE/Systems/Map/WorldConstants.cs",
    "Assets/_PFE/Systems/Map/WorldCoordinates.cs",
    "Assets/_PFE/Systems/Map/Rendering/RoomVisualController.cs",
    "Assets/_PFE/Systems/Map/Rendering/TileRenderer.cs",
    "Assets/_PFE/Systems/Map/MapBridge.cs",
    "Assets/_PFE/Systems/Map/Doorcarver.cs",
    "Assets/_PFE/Systems/Map/RoomSetup.cs"
)

$mustHaveAS3Files = @(
    "fe/World.as",
    "fe/loc/Location.as",
    "fe/loc/Tile.as"
)

# Good to have (for broader context)
$goodToHaveCSFiles = @(
    "Assets/_PFE/Systems/Map/TileData.cs",
    "Assets/_PFE/Systems/Map/RoomInstance.cs",
    "Assets/_PFE/Systems/Map/RoomTemplate.cs",
    "Assets/_PFE/Systems/Map/Rendering/TileVisualManager.cs",
    "Assets/_PFE/Systems/Map/Rendering/TileCompositor.cs",
    "Assets/_PFE/Systems/Map/Rendering/KonturCalculator.cs"
)

function Add-FileContent {
    param(
        [string]$FilePath,
        [string]$Header,
        [System.Text.StringBuilder]$StringBuilder
    )
    
    if (Test-Path $FilePath) {
        $codeBlock = '```'
        [void]$StringBuilder.AppendLine("## $Header")
        [void]$StringBuilder.AppendLine("")
        [void]$StringBuilder.AppendLine("**File:** ``$FilePath``")
        [void]$StringBuilder.AppendLine("")
        [void]$StringBuilder.AppendLine($codeBlock)
        $content = Get-Content $FilePath -Raw -Encoding UTF8
        [void]$StringBuilder.AppendLine($content)
        [void]$StringBuilder.AppendLine($codeBlock)
        [void]$StringBuilder.AppendLine("")
        [void]$StringBuilder.AppendLine("---")
        [void]$StringBuilder.AppendLine("")
        Write-Host "Added: $Header" -ForegroundColor Green
    }
    else {
        Write-Host "File not found: $FilePath" -ForegroundColor Red
    }
}

# Main script
Write-Host "Creating context file..." -ForegroundColor Cyan
Write-Host "Output: $OutputPath" -ForegroundColor Cyan
Write-Host ""

$sb = [System.Text.StringBuilder]::new()

# Add header
[void]$sb.AppendLine("# Context File for Coordinate/Dimension Fix")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("---")
[void]$sb.AppendLine("")

# Must have CS files
[void]$sb.AppendLine("# Must Have Files (CS)")
[void]$sb.AppendLine("")
foreach ($file in $mustHaveCSFiles) {
    $fullPath = Join-Path $UnityProjectPath $file
    $fileName = [System.IO.Path]::GetFileName($file)
    Add-FileContent -FilePath $fullPath -Header $fileName -StringBuilder $sb
}

# Must have AS3 files
[void]$sb.AppendLine("# Must Have Files (AS3)")
[void]$sb.AppendLine("")
foreach ($file in $mustHaveAS3Files) {
    $fullPath = Join-Path $AS3ScriptsPath $file
    $fileName = [System.IO.Path]::GetFileName($file)
    Add-FileContent -FilePath $fullPath -Header $fileName -StringBuilder $sb
}

# Good to have CS files
[void]$sb.AppendLine("# Good to Have Files (CS)")
[void]$sb.AppendLine("")
foreach ($file in $goodToHaveCSFiles) {
    $fullPath = Join-Path $UnityProjectPath $file
    $fileName = [System.IO.Path]::GetFileName($file)
    Add-FileContent -FilePath $fullPath -Header $fileName -StringBuilder $sb
}

# Write output
$sb.ToString() | Out-File -FilePath $OutputPath -Encoding UTF8

Write-Host ""
Write-Host "Context file created successfully: $OutputPath" -ForegroundColor Green
Write-Host "Total size: $((Get-Item $OutputPath).Length) bytes" -ForegroundColor Cyan
