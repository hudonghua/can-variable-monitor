param(
    [Parameter(Mandatory = $true)]
    [string]$ProjectSrc,

    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ProjectSrc)) {
    throw "Project source directory not found: $ProjectSrc"
}

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$workerProject = Join-Path $repoRoot "CanVariableMonitor.OfflineCWorker\CanVariableMonitor.OfflineCWorker.csproj"
$workerDll = Join-Path $repoRoot "CanVariableMonitor.OfflineCWorker\bin\$Configuration\net9.0\CanVariableMonitor.OfflineCWorker.dll"

dotnet build $workerProject -c $Configuration | Out-Host
if (-not (Test-Path -LiteralPath $workerDll)) {
    throw "Worker dll not found: $workerDll"
}

$cKeywords = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($word in @(
    "if","else","for","while","do","switch","case","default","return","sizeof",
    "typedef","struct","union","enum","static","extern","const","volatile","break",
    "continue","goto","void","int","char","short","long","float","double","signed",
    "unsigned","true","false","NULL","printf","memset","memcpy","abs")) {
    [void]$cKeywords.Add($word)
}

function Test-AppSourceFile {
    param([string]$Path)
    $ext = [System.IO.Path]::GetExtension($Path)
    if ($ext -notin @(".c", ".h")) {
        return $false
    }
    $relative = Get-RelativePathCompat $ProjectSrc $Path
    $relative = $relative.Replace("\", "/")
    $segments = @($relative -split "/" | Where-Object { $_.Length -gt 0 })
    $blockedDirs = @("bsp","driver","drivers","cmsis","startup","system","hal","rte","core","periph","peripheral","uart","usart","adc","gpio","can","timer","tim","eeprom","flash","i2c","spi","pwm","usb","eth","objects","listings")
    foreach ($segment in $segments[0..([Math]::Max(0, $segments.Count - 2))]) {
        if ($blockedDirs -contains $segment.ToLowerInvariant()) {
            return $false
        }
    }
    $file = [System.IO.Path]::GetFileNameWithoutExtension($Path).ToLowerInvariant()
    foreach ($prefix in @("startup","system_lpc17","core_cm","lpc17xx","bsp","driver","gpio","uart","usart","adc","can","timer","tim","eeprom","flash","i2c","spi","pwm")) {
        if ($file.StartsWith($prefix)) {
            return $false
        }
    }
    return $true
}

function Get-RelativePathCompat {
    param([string]$Root, [string]$Path)
    try {
        $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
        $pathFull = [System.IO.Path]::GetFullPath($Path)
        if ($pathFull.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $pathFull.Substring($rootFull.Length)
        }
    }
    catch {
    }
    return $Path
}

function Remove-CodeCommentsPreserveLength {
    param([string]$Text)
    $masked = [regex]::Replace($Text, '/\*[\s\S]*?\*/', { param($m) ' ' * $m.Value.Length })
    return [regex]::Replace($masked, '//.*$', { param($m) ' ' * $m.Value.Length }, [System.Text.RegularExpressions.RegexOptions]::Multiline)
}

function Get-LineNumber {
    param([string]$Text, [int]$Index)
    $line = 1
    for ($i = 0; $i -lt $Index; $i++) {
        if ($Text[$i] -eq "`n") {
            $line++
        }
    }
    return $line
}

function Find-MatchingBrace {
    param([string]$Text, [int]$OpenBrace)
    $depth = 0
    for ($i = $OpenBrace; $i -lt $Text.Length; $i++) {
        if ($Text[$i] -eq '{') {
            $depth++
        }
        elseif ($Text[$i] -eq '}') {
            $depth--
            if ($depth -eq 0) {
                return $i
            }
        }
    }
    return -1
}

$functionDefs = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($file in Get-ChildItem -LiteralPath $ProjectSrc -Recurse -File -Include *.c,*.h | Where-Object { Test-AppSourceFile $_.FullName }) {
    $text = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::Default).Replace("`r`n", "`n").Replace("`r", "`n")
    $code = Remove-CodeCommentsPreserveLength $text
    foreach ($match in [regex]::Matches($code, '\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\([^;{}]*\)\s*\{')) {
        $name = $match.Groups["name"].Value
        if ($cKeywords.Contains($name) -or $functionDefs.ContainsKey($name)) {
            continue
        }
        $openBrace = $code.IndexOf('{', $match.Index)
        $closeBrace = Find-MatchingBrace $code $openBrace
        if ($openBrace -lt 0 -or $closeBrace -lt 0) {
            continue
        }
        $lineStart = $text.LastIndexOf("`n", $match.Index)
        $lineStart = if ($lineStart -lt 0) { 0 } else { $lineStart + 1 }
        $sourceText = $text.Substring($lineStart, $closeBrace - $lineStart + 1)
        $functionDefs[$name] = [ordered]@{
            functionName = $name
            filePath = $file.FullName
            startLine = Get-LineNumber $text $lineStart
            lines = @($sourceText -split "`n")
            text = $sourceText
        }
    }
}

function Get-CalledFunctions {
    param([object]$FunctionDef)
    $result = [System.Collections.Generic.List[string]]::new()
    $code = Remove-CodeCommentsPreserveLength ([string]$FunctionDef.text)
    foreach ($match in [regex]::Matches($code, '\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(')) {
        $name = $match.Groups["name"].Value
        if ($cKeywords.Contains($name) -or $name.Equals([string]$FunctionDef.functionName, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        if (-not $result.Contains($name)) {
            $result.Add($name)
        }
    }
    return $result
}

function Get-RootScore {
    param([object]$FunctionDef)
    $name = [string]$FunctionDef.functionName
    $text = "$($FunctionDef.filePath) $name"
    $score = 0
    if ($name -match '(?i)(^|_)(loop|task|tick|logic|ctrl|control|work|process|scan|cycle)(_|$)') { $score += 35 }
    if ($name.Equals("main", [System.StringComparison]::OrdinalIgnoreCase)) { $score -= 40 }
    if ($name -match '(?i)(^|_)\d+ms(_|$)|\d+\s*ms') { $score += 25 }
    if ($text -match '(?i)app|usr|user|business|logic|control|ctrl') { $score += 18 }
    if ($text -match '(?i)display|disp|lcd|screen|page') { $score += 12 }
    if ($name -match '(?i)(init|send|write|read|recv|receive|get|set|delay|isr|irq|handler)$') { $score -= 25 }
    $score += [Math]::Min(25, (@(Get-CalledFunctions $FunctionDef).Count * 3))
    return $score
}

function Build-ReachableSources {
    param([string[]]$RootFunctions, [int]$Limit = 500)
    $queue = [System.Collections.Generic.Queue[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $sources = [System.Collections.Generic.List[object]]::new()
    foreach ($root in $RootFunctions) {
        $queue.Enqueue($root)
    }
    while ($queue.Count -gt 0 -and $sources.Count -lt $Limit) {
        $name = $queue.Dequeue()
        if (-not $seen.Add($name) -or -not $functionDefs.ContainsKey($name)) {
            continue
        }
        $def = $functionDefs[$name]
        $sources.Add([ordered]@{
            functionName = $def.functionName
            filePath = $def.filePath
            startLine = $def.startLine
            lines = $def.lines
        })
        foreach ($call in Get-CalledFunctions $def) {
            if (-not $seen.Contains($call)) {
                $queue.Enqueue($call)
            }
        }
    }
    return $sources
}

function New-SmokeVariable {
    param([string]$Name, [uint32]$Address)
    return [ordered]@{
        key = $Name
        name = $Name
        address = $Address
        size = 4
        typeName = "uint32"
        rawValue = 0
        forceActive = $false
        aliases = @($Name)
    }
}

function Start-Worker {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = "dotnet"
    $psi.Arguments = '"' + $workerDll + '"'
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    return [System.Diagnostics.Process]::Start($psi)
}

$script:requestId = 0
function Send-WorkerCommand {
    param([System.Diagnostics.Process]$Process, [string]$Command, [object]$Payload, [int]$TimeoutMs = 90000)
    $script:requestId++
    $request = [ordered]@{ id = $script:requestId; command = $Command; payload = $Payload }
    $Process.StandardInput.WriteLine(($request | ConvertTo-Json -Depth 80 -Compress))
    $Process.StandardInput.Flush()
    $readTask = $Process.StandardOutput.ReadLineAsync()
    if (-not $readTask.Wait($TimeoutMs)) {
        throw "Worker command timed out: $Command"
    }
    return $readTask.Result | ConvertFrom-Json
}

if ($functionDefs.Count -eq 0) {
    throw "No application-layer C functions were discovered after excluding BSP/Driver/CMSIS/Startup sources."
}

$roots = @($functionDefs.Values |
    ForEach-Object { [pscustomobject]@{ Def = $_; Score = Get-RootScore $_ } } |
    Where-Object { $_.Score -ge 20 } |
    Sort-Object -Property @{ Expression = "Score"; Descending = $true }, @{ Expression = { $_.Def.functionName }; Descending = $false } |
    Select-Object -First 3 |
    ForEach-Object { $_.Def.functionName })

if ($roots.Count -eq 0) {
    $roots = @($functionDefs.Values | Sort-Object functionName | Select-Object -First 1 | ForEach-Object { $_.functionName })
}

$sources = Build-ReachableSources $roots 500
if ($sources.Count -eq 0) {
    throw "No reachable application sources from roots: $($roots -join ', ')"
}

$variables = @((New-SmokeVariable "__offline_smoke_value" 1))

$process = Start-Worker
try {
    $project = [ordered]@{
        workDirectory = $ProjectSrc
        signature = "real-project-auto-app-smoke"
        rootFunctions = $roots
        sources = $sources
        variables = $variables
    }
    $init = Send-WorkerCommand $process "InitProject" $project
    if (-not [bool]$init.ok) {
        throw "InitProject failed: $($init.status)"
    }
    $run = Send-WorkerCommand $process "RunTick" $null
    if (-not [bool]$run.ok) {
        throw "RunTick failed: $($run.status)"
    }
    $coverage = [string]::Join("`n", @($init.coverage) + @($run.coverage))
    if ($coverage -match "业务调用被 stub") {
        Write-Warning "Some unresolved business calls remain:`n$coverage"
    }
    Write-Host "Real project offline smoke passed."
    Write-Host "Roots: $($roots -join ', ')"
    Write-Host "Application sources: $($sources.Count); smoke variables: $($variables.Count)"
}
finally {
    try { [void](Send-WorkerCommand $process "Shutdown" $null 5000) } catch {}
    if ($process -and -not $process.HasExited) {
        try { $process.Kill() } catch {}
    }
}
