# QuickGameCodeSummary.ps1
# Summarizes your actual game code for sharing

param(
    [string]$SourcePath = ""
)

if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    $SourcePath = Join-Path (Split-Path $PSScriptRoot -Parent) "Assets\_PFE"
}

Write-Host "`n=== GAME CODE SUMMARY ===" -ForegroundColor Cyan

# Folder structure
Write-Host "`nFolder Structure:" -ForegroundColor Yellow
Get-ChildItem $SourcePath -Directory -Recurse | ForEach-Object {
    $depth = ($_.FullName.Replace($SourcePath, "").Split('\/', [System.StringSplitOptions]::RemoveEmptyEntries)).Count
    $indent = "  " * $depth
    $csCount = (Get-ChildItem $_.FullName -Filter "*.cs" -File -ErrorAction SilentlyContinue).Count
    if ($csCount -gt 0) {
        Write-Host "$indent$($_.Name)/ ($csCount files)" -ForegroundColor White
    }
}

# All files with sizes
Write-Host "`nAll Game Scripts:" -ForegroundColor Yellow
$allFiles = Get-ChildItem $SourcePath -Recurse -Filter "*.cs" -File | Sort-Object FullName
$totalLines = 0

foreach ($file in $allFiles) {
    $content = Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
    $lines = if ($content) { ($content -split "`n").Count } else { 0 }
    $totalLines += $lines
    $relativePath = $file.FullName.Replace($SourcePath, "").TrimStart('\', '/')
    
    # Quick classification
    $markers = @()
    if ($content -match 'VContainer|LifetimeScope|IContainerBuilder') { $markers += "VC" }
    if ($content -match 'MessagePipe|IPublisher|ISubscriber') { $markers += "MP" }
    if ($content -match 'UniTask|async\s+UniTask') { $markers += "UT" }
    if ($content -match 'PrimeTween|Tween\.') { $markers += "PT" }
    if ($content -match 'MonoBehaviour') { $markers += "MB" }
    if ($content -match 'ScriptableObject') { $markers += "SO" }
    if ($content -match 'interface\s+I') { $markers += "IF" }
    if ($content -match 'static\s+class') { $markers += "ST" }
    if ($content -match 'TODO|HACK|FIXME') { $markers += "!!" }
    
    $markerStr = if ($markers.Count -gt 0) { " [" + ($markers -join ",") + "]" } else { "" }
    
    Write-Host ("{0,5} lines  {1}{2}" -f $lines, $relativePath, $markerStr)
}

Write-Host "`n  Total: $($allFiles.Count) files, $totalLines lines" -ForegroundColor Green

# Extract class signatures for sharing
Write-Host "`n`nClass Signatures (for AI analysis):" -ForegroundColor Yellow
Write-Host "=" * 60

foreach ($file in $allFiles) {
    $content = Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
    if (-not $content) { continue }
    
    $relativePath = $file.FullName.Replace($SourcePath, "").TrimStart('\', '/')
    
    # Extract class declaration, fields, and method signatures
    $classMatch = [regex]::Match($content, 
        '(?:public|internal)\s+(?:abstract\s+|sealed\s+|static\s+|partial\s+)*(?:class|interface|struct|enum)\s+\w+[^{]*')
    
    if ($classMatch.Success) {
        Write-Host "`n// --- $relativePath ---" -ForegroundColor DarkGray
        Write-Host $classMatch.Value.Trim()
        Write-Host "{"
        
        # Fields
        $fields = [regex]::Matches($content, 
            '^\s+(?:(?:\[[\w\(\)]+\]\s*)*)((?:public|private|protected|internal)\s+(?:readonly\s+|static\s+|const\s+)*[\w<>\[\],\s]+\s+\w+)\s*[;={]') 
        foreach ($f in ($fields | Select-Object -First 15)) {
            $fieldLine = $f.Groups[1].Value.Trim()
            if ($fieldLine.Length -lt 120) {
                Write-Host "    $fieldLine;"
            }
        }
        
        # Method signatures
        $methods = [regex]::Matches($content,
            '(?:public|private|protected|internal|override)\s+(?:static\s+|virtual\s+|abstract\s+|async\s+)*(?:[\w<>\[\]]+\s+)+(\w+)\s*\([^)]*\)')
        foreach ($m in $methods) {
            $sig = $m.Value.Trim()
            if ($sig.Length -lt 150) {
                Write-Host "    $sig { ... }"
            }
        }
        
        Write-Host "}"
    }
}

Write-Host "`n=== END SUMMARY ===" -ForegroundColor Green
