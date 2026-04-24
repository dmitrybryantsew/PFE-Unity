# PowerShell Script to Create Simplified Project Structure Context
# Generates a compact project structure for LLM context with minimal tokens
# Automatically discovers ALL .cs files in Assets/_PFE (excluding Tests)

param(
    [string]$OutputPath = "project_structure_context.md",
    [string]$UnityProjectPath = ""
)

if ([string]::IsNullOrWhiteSpace($UnityProjectPath)) {
    $UnityProjectPath = Split-Path $PSScriptRoot -Parent
}

# Directories to exclude
$excludeDirs = @("Tests", "Test", "~")

# Function to extract class/interface/struct signatures
function Get-TypeSignatures {
    param([string]$Content)
    
    $signatures = @()
    $lines = $Content -split "`n"
    $inClass = $false
    $braceCount = 0
    $classContent = @()
    
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $trimmed = $line.Trim()
        
        # Skip empty lines and comments at file level
        if (-not $inClass) {
            if ($trimmed -match '^(//|/\*|\*)') { continue }
            if ($trimmed -eq '') { continue }
        }
        
        # Detect class/interface/struct/enum start
        if ($trimmed -match '^(public|internal|private|protected)?\s*(sealed|abstract|static|partial)?\s*(class|interface|struct|enum)\s+(\w+)') {
            $inClass = $true
            $classContent = @($trimmed)
            $braceCount = ($trimmed.ToCharArray() | Where-Object { $_ -eq '{' }).Count
            $braceCount -= ($trimmed.ToCharArray() | Where-Object { $_ -eq '}' }).Count
            continue
        }
        
        if ($inClass) {
            $classContent += $trimmed
            $braceCount += ($trimmed.ToCharArray() | Where-Object { $_ -eq '{' }).Count
            $braceCount -= ($trimmed.ToCharArray() | Where-Object { $_ -eq '}' }).Count
            
            if ($braceCount -le 0) {
                $signature = Extract-Signature -Content $classContent
                $signatures += $signature
                $inClass = $false
                $classContent = @()
            }
        }
    }
    
    return $signatures
}

function Extract-Signature {
    param([string[]]$Content)
    
    $result = @()
    $header = $Content[0]
    
    # Clean up header
    $header = $header -replace '\s+', ' ' -replace '\{$', ''
    $result += $header
    
    # Extract public/protected methods and properties (signatures only)
    foreach ($line in $Content[1..($Content.Count-1)]) {
        # Skip comments
        if ($line -match '^\s*(//|/\*|\*)') { continue }
        
        # Properties
        if ($line -match '^\s*(public|protected)\s+[\w<>\[\],\s]+\s+(\w+)\s*\{\s*(get|set)') {
            $prop = $line -replace '\s+', ' ' -replace '\{$', '' -replace '^\s+', '  '
            $result += $prop.Trim()
        }
        
        # Methods (signature only)
        if ($line -match '^\s*(public|protected)\s+[\w<>\[\],\s]+\s+(\w+)\s*\(') {
            $method = $line -replace '\s+', ' ' -replace '\{$', '' -replace '^\s+', '  '
            $result += $method.Trim()
        }
    }
    
    return $result -join "`n"
}

# Main script
Write-Host "Discovering all .cs files in Assets/_PFE..." -ForegroundColor Cyan

$sb = [System.Text.StringBuilder]::new()
$codeBlock = '```'

[void]$sb.AppendLine("# PFE Unity Project Structure")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
[void]$sb.AppendLine("")

# Find all directories containing .cs files in Assets/_PFE
$pfePath = Join-Path $UnityProjectPath "Assets/_PFE"
$allCsFiles = Get-ChildItem -Path $pfePath -Filter "*.cs" -Recurse | Where-Object {
    $dir = $_.DirectoryName
    $shouldExclude = $false
    foreach ($excludeDir in $excludeDirs) {
        if ($dir -like "*\$excludeDir\*" -or $dir -like "*\$excludeDir") {
            $shouldExclude = $true
            break
        }
    }
    -not $shouldExclude
}

# Group files by directory
$filesByDir = $allCsFiles | Group-Object { $_.DirectoryName } | Sort-Object Name

$totalFiles = 0
foreach ($group in $filesByDir) {
    $dirPath = $group.Name
    $dirName = $dirPath -replace [regex]::Escape($pfePath), "" -replace "^\\", ""
    
    if ($dirName -eq "") { $dirName = "_PFE root" }
    
    [void]$sb.AppendLine("## $dirName/")
    [void]$sb.AppendLine("")
    
    foreach ($file in ($group.Group | Sort-Object Name)) {
        $fileName = $file.Name
        $filePath = $file.FullName
        
        Write-Host "Processing: $dirName/$fileName" -ForegroundColor Green
        
        $content = Get-Content $filePath -Raw -Encoding UTF8
        
        # Extract just the signatures
        $signatures = Get-TypeSignatures -Content $content
        
        [void]$sb.AppendLine("### $fileName")
        [void]$sb.AppendLine("$codeBlock csharp")
        [void]$sb.AppendLine($signatures)
        [void]$sb.AppendLine($codeBlock)
        [void]$sb.AppendLine("")
        $totalFiles++
    }
    
    [void]$sb.AppendLine("---")
    [void]$sb.AppendLine("")
}

# Add Data directory structure (just file listing, no content)
$dataSection = @"
## Data/ (Assets)

$codeBlock
Data/
+-- TileFormDatabase.asset
+-- MaterialRenderDatabase.asset
+-- TileTextureLookup.asset
+-- Resources/Rooms/ (RoomTemplate assets)
$codeBlock

## Art/ (Imported Assets)

$codeBlock
Art/
+-- TileTextures/ (imported tile textures)
+-- Imported/TileMasks/ (border masks)
+-- Imported/RoomBackgrounds/ (room backdrop sprites)
$codeBlock
"@

[void]$sb.AppendLine($dataSection)

# Write output
$sb.ToString() | Out-File -FilePath $OutputPath -Encoding UTF8

Write-Host ""
Write-Host "Project structure context created: $OutputPath" -ForegroundColor Green
Write-Host "Total files processed: $totalFiles" -ForegroundColor Cyan
Write-Host "Total size: $((Get-Item $OutputPath).Length) bytes" -ForegroundColor Cyan
