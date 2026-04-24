# UnityCSharpScanner.ps1
# Scans a Unity C# project and builds dependency analysis
# Usage: .\UnityCSharpScanner.ps1 -SourcePath "C:\UnityProject\Assets\Scripts"

param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,
    
    [Parameter(Mandatory = $false)]
    [string]$OutputPath = ".\unity_analysis",

    [Parameter(Mandatory = $false)]
    [switch]$IncludeUnityNative,

    [Parameter(Mandatory = $false)]
    [switch]$IncludeTests
)

# ============================================================
# Setup
# ============================================================

if (-not (Test-Path $OutputPath)) {
    New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
}

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

# Unity/System types to optionally exclude
$unityNativeTypes = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase
)
@(
    # Unity core
    'MonoBehaviour', 'ScriptableObject', 'GameObject', 'Transform', 'Component',
    'Rigidbody', 'Rigidbody2D', 'Collider', 'Collider2D', 'Camera',
    'Vector2', 'Vector3', 'Vector4', 'Quaternion', 'Matrix4x4',
    'Color', 'Color32', 'Rect', 'Bounds',
    'Mathf', 'Debug', 'Time', 'Input', 'Screen', 'Application',
    'Resources', 'Addressables', 'AssetBundle',
    'Sprite', 'Texture2D', 'Material', 'Shader', 'Mesh',
    'AudioSource', 'AudioClip', 'AudioMixer',
    'Canvas', 'RectTransform', 'Image', 'Text', 'Button', 'TMP_Text',
    'TextMeshPro', 'TextMeshProUGUI',
    'Animator', 'Animation', 'AnimationClip', 'RuntimeAnimatorController',
    'NavMeshAgent', 'Tilemap', 'SpriteRenderer',
    'Physics', 'Physics2D', 'RaycastHit', 'RaycastHit2D',
    'Coroutine', 'WaitForSeconds', 'WaitForEndOfFrame',
    'SceneManager', 'Scene',
    'PlayerPrefs', 'JsonUtility',
    'SerializeField', 'Header', 'Tooltip', 'Range', 'Space',
    'RequireComponent', 'DisallowMultipleComponent', 'ExecuteAlways',
    'HideInInspector', 'CreateAssetMenu',
    'Editor', 'EditorWindow', 'CustomEditor', 'PropertyDrawer',
    # C# / .NET
    'String', 'Int32', 'Single', 'Double', 'Boolean', 'Byte',
    'List', 'Dictionary', 'HashSet', 'Queue', 'Stack', 'LinkedList',
    'Array', 'ArrayList', 'Hashtable',
    'IEnumerable', 'IEnumerator', 'ICollection', 'IList', 'IDictionary',
    'IDisposable', 'IComparable', 'IEquatable',
    'Task', 'CancellationToken', 'CancellationTokenSource',
    'Action', 'Func', 'Predicate', 'Delegate', 'EventHandler',
    'Exception', 'ArgumentException', 'NullReferenceException',
    'Math', 'Convert', 'Enum', 'Tuple', 'ValueTuple',
    'StringBuilder', 'Regex', 'Match',
    'Stream', 'StreamReader', 'StreamWriter', 'File', 'Path', 'Directory',
    'JsonSerializer', 'JsonConvert',
    'Guid', 'DateTime', 'TimeSpan', 'Random',
    'object', 'string', 'int', 'float', 'double', 'bool', 'byte', 'char',
    'void', 'var', 'dynamic',
    # Stack framework types (recognized but categorized separately)
    'UniTask', 'UniTaskVoid',
    'IContainerBuilder', 'LifetimeScope', 'ObjectResolver',
    'IPublisher', 'ISubscriber', 'IBufferedPublisher', 'IBufferedSubscriber',
    'Tween', 'Sequence'
) | ForEach-Object { $unityNativeTypes.Add($_) | Out-Null }

# Stack framework namespaces
$stackNamespaces = @(
    'VContainer', 'VContainer.Unity',
    'MessagePipe',
    'Cysharp.Threading.Tasks',
    'PrimeTween',
    'Sirenix.OdinInspector'
)

# ============================================================
# Phase 1: Parse all C# files
# ============================================================

Write-Host "`n=== Unity C# Dependency Scanner ===" -ForegroundColor Cyan
Write-Host "Source: $SourcePath"
Write-Host "Scanning..." -ForegroundColor Yellow

$fileFilter = if ($IncludeTests) { "*.cs" } else { "*.cs" }
$csFiles = Get-ChildItem -Path $SourcePath -Recurse -Filter $fileFilter -File

if (-not $IncludeTests) {
    $csFiles = $csFiles | Where-Object {
        $_.FullName -notmatch '[\\/]Tests?[\\/]' -and
        $_.FullName -notmatch '[\\/]Editor[\\/]' -and
        $_.FullName -notmatch '\.Tests?\.' -and
        $_.FullName -notmatch 'Test\.cs$' -and
        $_.FullName -notmatch '[\\/]Plugins[\\/]'
    }
}

$totalFiles = @($csFiles).Count
Write-Host "Found $totalFiles .cs files`n"

# Data structures
$classInfo = @{}
$dependencies = @{}
$reverseDeps = @{}
$allClassNames = [System.Collections.Generic.HashSet[string]]::new()
$namespaceMap = @{}
$interfaceImplementations = @{}  # interface -> [classes that implement it]
$inheritanceMap = @{}            # class -> base class

$fileCounter = 0

foreach ($file in $csFiles) {
    $fileCounter++
    if ($fileCounter % 50 -eq 0) {
        Write-Host "  Parsing $fileCounter / $totalFiles ..." -ForegroundColor DarkGray
    }

    $content = Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
    if (-not $content) { continue }

    $relativePath = $file.FullName.Replace($SourcePath, "").TrimStart('\', '/')

    # --- Extract namespace ---
    $namespace = ""
    if ($content -match 'namespace\s+([\w.]+)') {
        $namespace = $Matches[1]
    }

    # --- Extract all class/interface/struct/enum names in file ---
    $typeMatches = [regex]::Matches($content, 
        '(?:public|private|protected|internal)\s+(?:abstract\s+|sealed\s+|static\s+|partial\s+)*(?:class|interface|struct|enum|record)\s+(\w+)')
    
    $primaryClass = ""
    foreach ($tm in $typeMatches) {
        $typeName = $tm.Groups[1].Value
        $allClassNames.Add($typeName) | Out-Null
        if (-not $primaryClass) { $primaryClass = $typeName }
        if ($namespace) { $namespaceMap[$typeName] = $namespace }
    }

    if (-not $primaryClass) {
        $primaryClass = $file.BaseName
        $allClassNames.Add($primaryClass) | Out-Null
    }

    # --- Extract using statements ---
    $usings = [System.Collections.Generic.HashSet[string]]::new()
    $usingMatches = [regex]::Matches($content, 'using\s+([\w.]+)\s*;')
    $usingNamespaces = @()
    foreach ($um in $usingMatches) {
        $usingNamespaces += $um.Groups[1].Value
    }

    # --- Extract inheritance ---
    $extends = ""
    $implements = @()
    $inheritMatch = [regex]::Match($content, 
        '(?:class|struct|record)\s+' + [regex]::Escape($primaryClass) + '(?:<[^>]+>)?\s*:\s*([^\{]+)')
    if ($inheritMatch.Success) {
        $inheritList = $inheritMatch.Groups[1].Value -split '\s*,\s*'
        foreach ($inh in $inheritList) {
            $cleanName = ($inh.Trim() -replace '<.*>', '' -replace '\s+', '')
            if ($cleanName -match '^I[A-Z]') {
                $implements += $cleanName
                if (-not $interfaceImplementations.ContainsKey($cleanName)) {
                    $interfaceImplementations[$cleanName] = @()
                }
                $interfaceImplementations[$cleanName] += $primaryClass
            }
            elseif (-not $extends -and $cleanName) {
                $extends = $cleanName
                $inheritanceMap[$primaryClass] = $extends
            }
            if ($IncludeUnityNative -or -not $unityNativeTypes.Contains($cleanName)) {
                $usings.Add($cleanName) | Out-Null
            }
        }
    }

    # --- Extract type references ---
    # Constructor injection parameters
    $ctorParams = [regex]::Matches($content, 
        '(?:public|private|protected|internal)\s+' + [regex]::Escape($primaryClass) + '\s*\(([^)]*)\)')
    foreach ($cp in $ctorParams) {
        $paramTypes = [regex]::Matches($cp.Groups[1].Value, '(?:I[A-Z]\w+|[A-Z]\w+)(?=\s+\w+)')
        foreach ($pt in $paramTypes) {
            $typeName = $pt.Value -replace '<.*>', ''
            if ($IncludeUnityNative -or -not $unityNativeTypes.Contains($typeName)) {
                $usings.Add($typeName) | Out-Null
            }
        }
    }

    # Field/property type references
    $fieldTypes = [regex]::Matches($content, 
        '(?:private|protected|public|internal)\s+(?:readonly\s+|static\s+)*(?:I[A-Z]\w+|[A-Z]\w+)(?:<[^>]+>)?(?:\[\])?\s+_?\w+\s*[;=\{]')
    foreach ($ft in $fieldTypes) {
        $typeMatch = [regex]::Match($ft.Value, '(?:readonly\s+|static\s+)*(I?[A-Z]\w+)')
        if ($typeMatch.Success) {
            $typeName = $typeMatch.Groups[1].Value
            if ($IncludeUnityNative -or -not $unityNativeTypes.Contains($typeName)) {
                $usings.Add($typeName) | Out-Null
            }
        }
    }

    # new Foo() instantiations
    $newTypes = [regex]::Matches($content, 'new\s+([A-Z]\w+)\s*[\(<\[]')
    foreach ($nt in $newTypes) {
        $typeName = $nt.Groups[1].Value
        if ($IncludeUnityNative -or -not $unityNativeTypes.Contains($typeName)) {
            $usings.Add($typeName) | Out-Null
        }
    }

    # Generic type parameters
    $genericTypes = [regex]::Matches($content, '<\s*([A-Z]\w+)\s*>')
    foreach ($gt in $genericTypes) {
        $typeName = $gt.Groups[1].Value
        if ($IncludeUnityNative -or -not $unityNativeTypes.Contains($typeName)) {
            $usings.Add($typeName) | Out-Null
        }
    }

    # Static class references (Foo.Bar)
    $staticRefs = [regex]::Matches($content, '\b([A-Z]\w+)\s*\.\s*[A-Za-z]')
    foreach ($sr in $staticRefs) {
        $typeName = $sr.Groups[1].Value
        if ($IncludeUnityNative -or -not $unityNativeTypes.Contains($typeName)) {
            $usings.Add($typeName) | Out-Null
        }
    }

    # typeof(Foo)
    $typeofRefs = [regex]::Matches($content, 'typeof\s*\(\s*(\w+)\s*\)')
    foreach ($tr in $typeofRefs) {
        $typeName = $tr.Groups[1].Value
        if ($IncludeUnityNative -or -not $unityNativeTypes.Contains($typeName)) {
            $usings.Add($typeName) | Out-Null
        }
    }

    # Remove self-references
    $usings.Remove($primaryClass) | Out-Null

    # --- Detect patterns ---
    $lines = ($content -split "`n").Count

    $functionCount = ([regex]::Matches($content, 
        '(?:public|private|protected|internal)\s+(?:static\s+|virtual\s+|override\s+|abstract\s+|async\s+)*(?:\w+(?:<[^>]+>)?(?:\[\])?)\s+\w+\s*[\(<]')).Count

    # VContainer patterns
    $hasLifetimeScope = $content -match 'LifetimeScope|IContainerBuilder'
    $hasEntryPoint = $content -match 'IStartable|ITickable|IFixedTickable|ILateTickable|IAsyncStartable'
    $hasInject = $content -match '\[Inject\]'
    $hasConstructorInjection = ($ctorParams.Count -gt 0) -and ($content -match 'class\s+\w+.*:\s*.*(?:IStartable|ITickable|IDisposable)')

    # MessagePipe patterns
    $publisherCount = ([regex]::Matches($content, 'IPublisher<')).Count
    $subscriberCount = ([regex]::Matches($content, 'ISubscriber<')).Count
    $publishCalls = ([regex]::Matches($content, '\.Publish\s*\(')).Count
    $subscribeCalls = ([regex]::Matches($content, '\.Subscribe\s*\(')).Count

    # UniTask patterns
    $asyncMethods = ([regex]::Matches($content, 'async\s+UniTask')).Count
    $awaitCalls = ([regex]::Matches($content, 'await\s+')).Count
    $forgetCalls = ([regex]::Matches($content, '\.Forget\s*\(')).Count

    # PrimeTween patterns
    $tweenCalls = ([regex]::Matches($content, 'Tween\.\w+\s*\(')).Count

    # Odin patterns  
    $odinAttributes = ([regex]::Matches($content, '\[(Button|ShowIf|HideIf|BoxGroup|FoldoutGroup|TabGroup|TableList|ShowInInspector|ReadOnly|Required|ValidateInput|OnValueChanged|InfoBox|PropertyOrder|LabelText|Title|GUIColor|EnableIf|DisableIf)')).Count

    # Unity patterns
    $usesMonoBehaviour = $content -match 'class\s+\w+\s*:\s*MonoBehaviour'
    $usesScriptableObject = $content -match 'class\s+\w+\s*:\s*ScriptableObject'
    $serializeFieldCount = ([regex]::Matches($content, '\[SerializeField\]')).Count
    $getComponentCalls = ([regex]::Matches($content, 'GetComponent')).Count
    $findObjectCalls = ([regex]::Matches($content, 'Find(?:Object|GameObj|WithTag)')).Count
    $coroutineCount = ([regex]::Matches($content, 'StartCoroutine|IEnumerator')).Count

    # Categorize
    $category = if ($hasLifetimeScope) { "DI-Installer" }
        elseif ($usesScriptableObject) { "Data-SO" }
        elseif ($hasEntryPoint -or $hasConstructorInjection) { "Service-Pure" }
        elseif ($usesMonoBehaviour -and $hasInject) { "View-Injected" }
        elseif ($usesMonoBehaviour) { "View-Mono" }
        elseif ($content -match '^\s*public\s+(?:enum|struct)\s+') { "Data-Type" }
        elseif ($content -match 'interface\s+I[A-Z]') { "Interface" }
        elseif ($content -match 'static\s+class') { "Utility-Static" }
        else { "Service-Pure" }

    # Detect potential issues
    $issues = @()
    if ($findObjectCalls -gt 0) { $issues += "FindObject($findObjectCalls)" }
    if ($coroutineCount -gt 0) { $issues += "Coroutine($coroutineCount)" }
    if ($content -match 'static\s+(?:public|internal)\s+\w+\s+Instance') { $issues += "Singleton" }
    if ($content -match 'DontDestroyOnLoad') { $issues += "DontDestroy" }
    if ($getComponentCalls -gt 3) { $issues += "ExcessiveGetComponent($getComponentCalls)" }

    # Detect AS3 port remnants
    $portIndicators = @()
    if ($content -match '// ?(?:AS3|Flash|ActionScript|ported|original)') { $portIndicators += "HasPortComment" }
    if ($content -match 'TODO|HACK|FIXME|XXX') { 
        $todoCount = ([regex]::Matches($content, 'TODO|HACK|FIXME|XXX')).Count
        $portIndicators += "TODO($todoCount)" 
    }

    # Detect which stack packages are used
    $stackUsage = @()
    if ($usingNamespaces -match 'VContainer') { $stackUsage += "VContainer" }
    if ($usingNamespaces -match 'MessagePipe') { $stackUsage += "MessagePipe" }
    if ($usingNamespaces -match 'Cysharp') { $stackUsage += "UniTask" }
    if ($usingNamespaces -match 'PrimeTween') { $stackUsage += "PrimeTween" }
    if ($usingNamespaces -match 'Sirenix') { $stackUsage += "Odin" }

    $classInfo[$primaryClass] = @{
        File                    = $relativePath
        Namespace               = $namespace
        Category                = $category
        Lines                   = $lines
        Functions               = $functionCount
        Extends                 = $extends
        Implements              = ($implements -join ", ")
        UsingNamespaces         = ($usingNamespaces -join "; ")
        # Stack usage
        StackUsage              = ($stackUsage -join ", ")
        IsLifetimeScope         = $hasLifetimeScope
        IsEntryPoint            = $hasEntryPoint
        PublisherCount          = $publisherCount
        SubscriberCount         = $subscriberCount
        PublishCalls            = $publishCalls
        SubscribeCalls          = $subscribeCalls
        AsyncMethods            = $asyncMethods
        AwaitCalls              = $awaitCalls
        TweenCalls              = $tweenCalls
        OdinAttributes          = $odinAttributes
        # Unity patterns
        IsMonoBehaviour         = [bool]$usesMonoBehaviour
        IsScriptableObject      = [bool]$usesScriptableObject
        SerializeFields         = $serializeFieldCount
        GetComponentCalls       = $getComponentCalls
        FindObjectCalls         = $findObjectCalls
        CoroutineCalls          = $coroutineCount
        # Quality
        Issues                  = ($issues -join ", ")
        PortIndicators          = ($portIndicators -join ", ")
    }
    $dependencies[$primaryClass] = $usings
}

# ============================================================
# Phase 2: Build reverse dependencies (only for project types)
# ============================================================

Write-Host "`nBuilding dependency graph..." -ForegroundColor Yellow

foreach ($className in $dependencies.Keys) {
    foreach ($dep in $dependencies[$className]) {
        if ($allClassNames.Contains($dep)) {
            if (-not $reverseDeps.ContainsKey($dep)) {
                $reverseDeps[$dep] = [System.Collections.Generic.HashSet[string]]::new()
            }
            $reverseDeps[$dep].Add($className) | Out-Null
        }
    }
}

# ============================================================
# Phase 3: Detect clusters
# ============================================================

Write-Host "Detecting clusters..." -ForegroundColor Yellow

$visited = [System.Collections.Generic.HashSet[string]]::new()
$clusters = @()

foreach ($className in $allClassNames) {
    if ($visited.Contains($className)) { continue }
    $queue = [System.Collections.Queue]::new()
    $cluster = [System.Collections.Generic.List[string]]::new()
    $queue.Enqueue($className)
    $visited.Add($className) | Out-Null

    while ($queue.Count -gt 0) {
        $current = $queue.Dequeue()
        $cluster.Add($current)
        if ($dependencies.ContainsKey($current)) {
            foreach ($dep in $dependencies[$current]) {
                if ($allClassNames.Contains($dep) -and -not $visited.Contains($dep)) {
                    $visited.Add($dep) | Out-Null
                    $queue.Enqueue($dep)
                }
            }
        }
        if ($reverseDeps.ContainsKey($current)) {
            foreach ($rdep in $reverseDeps[$current]) {
                if (-not $visited.Contains($rdep)) {
                    $visited.Add($rdep) | Out-Null
                    $queue.Enqueue($rdep)
                }
            }
        }
    }
    if ($cluster.Count -gt 0) {
        $clusters += , @($cluster.ToArray())
    }
}
$clusters = $clusters | Sort-Object { $_.Count } -Descending

# ============================================================
# Phase 4: Output Reports
# ============================================================

Write-Host "`n=== RESULTS ===" -ForegroundColor Green

# --- 4A: Architecture overview ---
Write-Host "`nArchitecture Overview:" -ForegroundColor Cyan

$catSummary = $classInfo.GetEnumerator() | 
    ForEach-Object { [PSCustomObject]@{
        Class    = $_.Key
        Category = $_.Value.Category
        Lines    = $_.Value.Lines
    }} | Group-Object Category |
    ForEach-Object { [PSCustomObject]@{
        Category   = $_.Name
        Count      = $_.Count
        TotalLines = ($_.Group | Measure-Object Lines -Sum).Sum
    }} | Sort-Object Count -Descending

$catSummary | Format-Table -AutoSize

# --- 4B: Stack adoption ---
Write-Host "Stack Adoption:" -ForegroundColor Cyan

$stackCounts = @{
    VContainer  = 0
    MessagePipe = 0
    UniTask     = 0
    PrimeTween  = 0
    Odin        = 0
}
foreach ($info in $classInfo.Values) {
    $su = $info.StackUsage
    if ($su -match 'VContainer') { $stackCounts.VContainer++ }
    if ($su -match 'MessagePipe') { $stackCounts.MessagePipe++ }
    if ($su -match 'UniTask') { $stackCounts.UniTask++ }
    if ($su -match 'PrimeTween') { $stackCounts.PrimeTween++ }
    if ($su -match 'Odin') { $stackCounts.Odin++ }
}

$totalClasses = $classInfo.Count
foreach ($pkg in $stackCounts.Keys | Sort-Object) {
    $count = $stackCounts[$pkg]
    $pct = if ($totalClasses -gt 0) { [Math]::Round($count / $totalClasses * 100, 1) } else { 0 }
    $bar = "#" * [Math]::Min(40, [Math]::Floor($pct / 2.5))
    Write-Host ("  {0,-15} {1,4} files ({2,5}%)  {3}" -f $pkg, $count, $pct, $bar)
}

# --- 4C: DI / LifetimeScope analysis ---
Write-Host "`nLifetimeScopes (DI Installers):" -ForegroundColor Cyan
$classInfo.GetEnumerator() | Where-Object { $_.Value.IsLifetimeScope } | ForEach-Object {
    Write-Host "  $($_.Key) - $($_.Value.File)" -ForegroundColor White
    
    # Try to find what it registers
    $file = Join-Path $SourcePath $_.Value.File
    if (Test-Path $file) {
        $content = Get-Content $file -Raw
        $registers = [regex]::Matches($content, 'Register(?:EntryPoint|MessageBroker)?(?:<([^>]+)>)?\s*\(')
        foreach ($r in $registers) {
            Write-Host "    Register: $($r.Value)" -ForegroundColor DarkGray
        }
    }
}

# --- 4D: MessagePipe event flow ---
Write-Host "`nMessagePipe Event Flow:" -ForegroundColor Cyan

$eventTypes = @{}  # EventType -> { publishers: [], subscribers: [] }

foreach ($entry in $classInfo.GetEnumerator()) {
    $cls = $entry.Key
    $file = Join-Path $SourcePath $entry.Value.File
    if (-not (Test-Path $file)) { continue }
    
    $content = Get-Content $file -Raw -ErrorAction SilentlyContinue
    if (-not $content) { continue }

    $pubMatches = [regex]::Matches($content, 'IPublisher<(\w+)>')
    foreach ($pm in $pubMatches) {
        $evtType = $pm.Groups[1].Value
        if (-not $eventTypes.ContainsKey($evtType)) {
            $eventTypes[$evtType] = @{ Publishers = @(); Subscribers = @() }
        }
        if ($cls -notin $eventTypes[$evtType].Publishers) {
            $eventTypes[$evtType].Publishers += $cls
        }
    }

    $subMatches = [regex]::Matches($content, 'ISubscriber<(\w+)>')
    foreach ($sm in $subMatches) {
        $evtType = $sm.Groups[1].Value
        if (-not $eventTypes.ContainsKey($evtType)) {
            $eventTypes[$evtType] = @{ Publishers = @(); Subscribers = @() }
        }
        if ($cls -notin $eventTypes[$evtType].Subscribers) {
            $eventTypes[$evtType].Subscribers += $cls
        }
    }
}

if ($eventTypes.Count -gt 0) {
    foreach ($evt in ($eventTypes.GetEnumerator() | Sort-Object { $_.Value.Subscribers.Count } -Descending)) {
        Write-Host "  $($evt.Key)" -ForegroundColor White
        Write-Host "    Published by:  $($evt.Value.Publishers -join ', ')" -ForegroundColor DarkGray
        Write-Host "    Subscribed by: $($evt.Value.Subscribers -join ', ')" -ForegroundColor DarkGray
    }
}
else {
    Write-Host "  (No MessagePipe events detected)" -ForegroundColor DarkGray
}

# --- 4E: Most depended-on ---
Write-Host "`nTop 30 Most-Depended-On:" -ForegroundColor Cyan
$topDeps = $reverseDeps.GetEnumerator() | 
    ForEach-Object { [PSCustomObject]@{
        Class      = $_.Key
        Dependents = $_.Value.Count
        Category   = if ($classInfo.ContainsKey($_.Key)) { $classInfo[$_.Key].Category } else { "External" }
        Lines      = if ($classInfo.ContainsKey($_.Key)) { $classInfo[$_.Key].Lines } else { 0 }
    }} | Sort-Object Dependents -Descending

$topDeps | Select-Object -First 30 | Format-Table -AutoSize

# --- 4F: Issues / Anti-patterns ---
Write-Host "Potential Issues:" -ForegroundColor Cyan
$issueFiles = $classInfo.GetEnumerator() | 
    Where-Object { $_.Value.Issues } |
    ForEach-Object { [PSCustomObject]@{
        Class    = $_.Key
        Issues   = $_.Value.Issues
        Category = $_.Value.Category
        File     = $_.Value.File
    }} | Sort-Object Issues

if (@($issueFiles).Count -gt 0) {
    $issueFiles | Format-Table -AutoSize
}
else {
    Write-Host "  No major issues detected" -ForegroundColor Green
}

# --- 4G: Port progress indicators ---
Write-Host "`nPort Progress Indicators:" -ForegroundColor Cyan
$portFiles = $classInfo.GetEnumerator() | 
    Where-Object { $_.Value.PortIndicators } |
    ForEach-Object { [PSCustomObject]@{
        Class      = $_.Key
        Indicators = $_.Value.PortIndicators
    }}

if (@($portFiles).Count -gt 0) {
    $portFiles | Format-Table -AutoSize
}
else {
    Write-Host "  No TODO/HACK/port markers found" -ForegroundColor Green
}

# --- 4H: Interface coverage ---
Write-Host "`nInterface Implementations:" -ForegroundColor Cyan
foreach ($iface in ($interfaceImplementations.GetEnumerator() | Sort-Object { $_.Value.Count } -Descending | Select-Object -First 20)) {
    $implCount = $iface.Value.Count
    $impls = $iface.Value -join ", "
    Write-Host ("  {0,-35} -> {1}" -f $iface.Key, $impls)
}

# --- 4I: Clusters ---
Write-Host "`nClusters:" -ForegroundColor Cyan
Write-Host "  Total: $($clusters.Count)"
$ci = 0
foreach ($cluster in ($clusters | Select-Object -First 10)) {
    $clusterLines = 0
    foreach ($member in $cluster) {
        if ($classInfo.ContainsKey($member)) { $clusterLines += $classInfo[$member].Lines }
    }
    Write-Host ("  Cluster {0}: {1,4} classes, {2,6} lines" -f $ci, $cluster.Count, $clusterLines)
    $ci++
}

# --- 4J: Largest files ---
Write-Host "`nLargest Files:" -ForegroundColor Cyan
$classInfo.GetEnumerator() |
    ForEach-Object { [PSCustomObject]@{
        Class     = $_.Key
        Lines     = $_.Value.Lines
        Functions = $_.Value.Functions
        Category  = $_.Value.Category
        Stack     = $_.Value.StackUsage
    }} |
    Sort-Object Lines -Descending |
    Select-Object -First 20 |
    Format-Table -AutoSize

# ============================================================
# Phase 5: Exports
# ============================================================

# --- CSV: Dependencies ---
$csvPath = Join-Path $OutputPath "dependencies.csv"
$csvRows = foreach ($className in $dependencies.Keys) {
    foreach ($dep in $dependencies[$className]) {
        if ($allClassNames.Contains($dep)) {
            [PSCustomObject]@{
                Source         = $className
                Target         = $dep
                SourceCategory = if ($classInfo.ContainsKey($className)) { $classInfo[$className].Category } else { "" }
                TargetCategory = if ($classInfo.ContainsKey($dep)) { $classInfo[$dep].Category } else { "" }
            }
        }
    }
}
$csvRows | Export-Csv -Path $csvPath -NoTypeInformation
Write-Host "`nExported: $csvPath" -ForegroundColor Green

# --- CSV: Classes ---
$classPath = Join-Path $OutputPath "classes.csv"
$classInfo.GetEnumerator() | ForEach-Object {
    [PSCustomObject]@{
        Class              = $_.Key
        Namespace          = $_.Value.Namespace
        Category           = $_.Value.Category
        Lines              = $_.Value.Lines
        Functions          = $_.Value.Functions
        Extends            = $_.Value.Extends
        Implements         = $_.Value.Implements
        StackUsage         = $_.Value.StackUsage
        IsMonoBehaviour    = $_.Value.IsMonoBehaviour
        IsScriptableObject = $_.Value.IsScriptableObject
        SerializeFields    = $_.Value.SerializeFields
        PublisherCount     = $_.Value.PublisherCount
        SubscriberCount    = $_.Value.SubscriberCount
        AsyncMethods       = $_.Value.AsyncMethods
        TweenCalls         = $_.Value.TweenCalls
        OdinAttributes     = $_.Value.OdinAttributes
        Issues             = $_.Value.Issues
        PortIndicators     = $_.Value.PortIndicators
        Dependents         = if ($reverseDeps.ContainsKey($_.Key)) { $reverseDeps[$_.Key].Count } else { 0 }
        File               = $_.Value.File
    }
} | Export-Csv -Path $classPath -NoTypeInformation
Write-Host "Exported: $classPath" -ForegroundColor Green

# --- CSV: Clusters ---
$clusterPath = Join-Path $OutputPath "clusters.csv"
$clusterRows = for ($i = 0; $i -lt $clusters.Count; $i++) {
    foreach ($member in $clusters[$i]) {
        [PSCustomObject]@{
            ClusterID   = $i
            ClusterSize = $clusters[$i].Count
            Class       = $member
            Category    = if ($classInfo.ContainsKey($member)) { $classInfo[$member].Category } else { "" }
            Lines       = if ($classInfo.ContainsKey($member)) { $classInfo[$member].Lines } else { 0 }
        }
    }
}
$clusterRows | Export-Csv -Path $clusterPath -NoTypeInformation
Write-Host "Exported: $clusterPath" -ForegroundColor Green

# --- DOT graph ---
$dotPath = Join-Path $OutputPath "dependency_graph.dot"
$topClassCount = 60
$topClassNames = $topDeps | Select-Object -First $topClassCount | ForEach-Object { $_.Class }
$topSet = [System.Collections.Generic.HashSet[string]]::new([string[]]@($topClassNames))

$dotLines = [System.Collections.Generic.List[string]]::new()
$dotLines.Add("digraph UnityDependencies {")
$dotLines.Add("    rankdir=LR;")
$dotLines.Add("    node [shape=box, style=filled, fontsize=10];")
$dotLines.Add("")

foreach ($cn in $topSet) {
    $cat = if ($classInfo.ContainsKey($cn)) { $classInfo[$cn].Category } else { "" }
    $color = switch -Wildcard ($cat) {
        "DI-*"         { "#B19CD9" }  # purple
        "Service-*"    { "#90EE90" }  # green
        "View-*"       { "#FFD700" }  # yellow
        "Data-*"       { "#87CEEB" }  # blue
        "Interface"    { "#FFFFFF" }  # white
        "Utility-*"    { "#DDDDDD" }  # gray
        default        { "#FFCCCC" }  # light red
    }
    $depCount = if ($reverseDeps.ContainsKey($cn)) { $reverseDeps[$cn].Count } else { 0 }
    $label = "$cn\n($cat, ${depCount}dep)"
    $dotLines.Add("    `"$cn`" [fillcolor=`"$color`", label=`"$label`"];")
}

$dotLines.Add("")
foreach ($cn in $topSet) {
    if ($dependencies.ContainsKey($cn)) {
        foreach ($dep in $dependencies[$cn]) {
            if ($topSet.Contains($dep)) {
                $dotLines.Add("    `"$cn`" -> `"$dep`";")
            }
        }
    }
}
$dotLines.Add("}")
$dotLines -join "`n" | Set-Content -Path $dotPath -Encoding UTF8
Write-Host "Exported: $dotPath" -ForegroundColor Green

# --- Full JSON ---
$jsonPath = Join-Path $OutputPath "full_analysis.json"
$jsonData = @{
    meta = @{
        totalFiles   = $totalFiles
        totalClasses = $allClassNames.Count
        scanDate     = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
        sourcePath   = $SourcePath
    }
    classes          = @{}
    eventFlow        = $eventTypes
    interfaceImpls   = $interfaceImplementations
    inheritance      = $inheritanceMap
    clusters         = @()
}

foreach ($cn in $allClassNames) {
    $info = if ($classInfo.ContainsKey($cn)) { $classInfo[$cn] } else { @{} }
    $deps = if ($dependencies.ContainsKey($cn)) { @($dependencies[$cn]) } else { @() }
    $rdeps = if ($reverseDeps.ContainsKey($cn)) { @($reverseDeps[$cn]) } else { @() }
    $jsonData.classes[$cn] = @{
        info         = $info
        dependsOn    = $deps
        dependedOnBy = $rdeps
    }
}

for ($i = 0; $i -lt [Math]::Min($clusters.Count, 50); $i++) {
    $jsonData.clusters += , @{
        id      = $i
        size    = $clusters[$i].Count
        members = $clusters[$i]
    }
}

$jsonData | ConvertTo-Json -Depth 5 -Compress | Set-Content -Path $jsonPath -Encoding UTF8
Write-Host "Exported: $jsonPath" -ForegroundColor Green

# --- Obsidian Canvas ---
$canvasPath = Join-Path $OutputPath "unity_project.canvas"

$canvasNodes = [System.Collections.Generic.List[object]]::new()
$canvasEdges = [System.Collections.Generic.List[object]]::new()

$categories = @("DI-Installer", "Service-Pure", "Interface", "View-Injected", "View-Mono", "Data-SO", "Data-Type", "Utility-Static")
$catColors = @{
    "DI-Installer"   = "3"  # purple
    "Service-Pure"   = "4"  # green
    "Interface"      = "0"  # default
    "View-Injected"  = "5"  # yellow
    "View-Mono"      = "6"  # orange
    "Data-SO"        = "2"  # blue  
    "Data-Type"      = "2"  # blue
    "Utility-Static" = "0"  # default
}

$nodeWidth = 280
$nodeHeight = 80
$catIndex = 0

foreach ($cat in $categories) {
    $catClasses = $classInfo.GetEnumerator() | 
        Where-Object { $_.Value.Category -eq $cat } |
        Sort-Object { 
            $depCount = if ($reverseDeps.ContainsKey($_.Key)) { $reverseDeps[$_.Key].Count } else { 0 }
            $depCount
        } -Descending

    if (@($catClasses).Count -eq 0) { continue }

    $xOffset = $catIndex * ($nodeWidth + 150)

    $canvasNodes.Add(@{
        id     = "header_$cat"
        type   = "text"
        text   = "## $cat`n$(@($catClasses).Count) classes"
        x      = $xOffset
        y      = -120
        width  = $nodeWidth + 20
        height = 70
        color  = if ($catColors.ContainsKey($cat)) { $catColors[$cat] } else { "0" }
    })

    $rowIndex = 0
    foreach ($entry in $catClasses) {
        $cls = $entry.Key
        $info = $entry.Value
        $depCount = if ($reverseDeps.ContainsKey($cls)) { $reverseDeps[$cls].Count } else { 0 }
        $stackBadge = if ($info.StackUsage) { " [$($info.StackUsage)]" } else { "" }
        $issueBadge = if ($info.Issues) { " !!$($info.Issues)" } else { "" }

        $label = "**$cls**`nL:$($info.Lines) F:$($info.Functions) D:$depCount$stackBadge$issueBadge"

        $canvasNodes.Add(@{
            id     = $cls
            type   = "text"
            text   = $label
            x      = $xOffset
            y      = $rowIndex * ($nodeHeight + 20)
            width  = $nodeWidth
            height = $nodeHeight
            color  = if ($catColors.ContainsKey($cat)) { $catColors[$cat] } else { "0" }
        })
        $rowIndex++
    }
    $catIndex++
}

# Add edges (only for top N most connected to keep it readable)
$topForCanvas = $topDeps | Select-Object -First 40 | ForEach-Object { $_.Class }
$canvasTopSet = [System.Collections.Generic.HashSet[string]]::new([string[]]@($topForCanvas))

foreach ($dep in $csvRows) {
    if ($null -eq $dep) { continue }
    if ($canvasTopSet.Contains($dep.Source) -and $canvasTopSet.Contains($dep.Target)) {
        $canvasEdges.Add(@{
            id       = "$($dep.Source)_$($dep.Target)"
            fromNode = $dep.Source
            toNode   = $dep.Target
            fromSide = "right"
            toSide   = "left"
        })
    }
}

$canvas = @{
    nodes = $canvasNodes.ToArray()
    edges = $canvasEdges.ToArray()
}
$canvas | ConvertTo-Json -Depth 4 | Set-Content -Path $canvasPath -Encoding UTF8
Write-Host "Exported: $canvasPath" -ForegroundColor Green

# ============================================================
# Summary
# ============================================================

$stopwatch.Stop()
$totalLines = 0
foreach ($v in $classInfo.Values) { $totalLines += $v.Lines }

Write-Host "`n=== SCAN COMPLETE ===" -ForegroundColor Green
Write-Host "  Classes:        $($allClassNames.Count)"
Write-Host "  Total lines:    $totalLines"
Write-Host "  Clusters:       $($clusters.Count)"
Write-Host "  Time:           $($stopwatch.Elapsed.TotalSeconds.ToString('0.0'))s"
Write-Host ""
Write-Host "  Output: $OutputPath" -ForegroundColor Yellow
Write-Host "    - dependencies.csv"
Write-Host "    - classes.csv"
Write-Host "    - clusters.csv"
Write-Host "    - dependency_graph.dot"
Write-Host "    - full_analysis.json"
Write-Host "    - unity_project.canvas"
Write-Host ""
Write-Host "  Share the console output + classes.csv for analysis" -ForegroundColor Cyan