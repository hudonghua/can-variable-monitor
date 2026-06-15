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

function Test-ZeroParameterList {
    param([string]$Parameters)
    if ($null -eq $Parameters) {
        $Parameters = ""
    }
    $text = [regex]::Replace($Parameters, '\s+', '')
    return $text.Length -eq 0 -or $text.Equals("void", [System.StringComparison]::OrdinalIgnoreCase)
}

$functionDefs = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::OrdinalIgnoreCase)
foreach ($file in Get-ChildItem -LiteralPath $ProjectSrc -Recurse -File -Include *.c,*.h | Where-Object { Test-AppSourceFile $_.FullName }) {
    $text = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::Default).Replace("`r`n", "`n").Replace("`r", "`n")
    $code = Remove-CodeCommentsPreserveLength $text
    foreach ($match in [regex]::Matches($code, '\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<params>[^;{}]*)\)\s*\{')) {
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
            canCallWithoutArgs = Test-ZeroParameterList $match.Groups["params"].Value
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

function Test-Identifier {
    param([string]$Name)
    return -not [string]::IsNullOrWhiteSpace($Name) -and
        [regex]::IsMatch($Name, '^[A-Za-z_][A-Za-z0-9_]*$') -and
        -not $cKeywords.Contains($Name)
}

function Test-FunctionDeclaresVariable {
    param([string]$Text, [string]$Name)
    $escaped = [regex]::Escape($Name)
    $typePattern = '(?:unsigned\s+|signed\s+|short\s+|long\s+)*(?:char|int|float|double|bool|uint|u8|u16|u32|s8|s16|s32|uint8_t|uint16_t|uint32_t|int8_t|int16_t|int32_t|BYTE|WORD|DWORD)'
    return [regex]::IsMatch(
        $Text,
        "(?m)^\s*(?:static\s+|const\s+|volatile\s+|register\s+)*$typePattern\s+[^;]*\b$escaped\b")
}

function Find-BranchForceCandidates {
    param([object[]]$Sources)
    $result = [System.Collections.Generic.List[object]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($source in $Sources) {
        $lines = @($source.lines)
        $text = [string]::Join("`n", $lines)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            $line = [regex]::Replace([string]$lines[$i], '//.*$', '')
            $condition = [regex]::Match(
                $line,
                '^\s*if\s*\(\s*(?<cond>[A-Za-z_][A-Za-z0-9_]*)\s*(?:(?:!=\s*0)|(?:==\s*1)|(?:>\s*0))?\s*\)')
            if (-not $condition.Success) {
                continue
            }
            $conditionName = $condition.Groups['cond'].Value
            if (-not (Test-Identifier $conditionName) -or
                $functionDefs.ContainsKey($conditionName) -or
                (Test-FunctionDeclaresVariable $text $conditionName)) {
                continue
            }

            $max = [Math]::Min($lines.Count - 1, $i + 14)
            for ($j = $i + 1; $j -le $max; $j++) {
                $bodyLine = [regex]::Replace([string]$lines[$j], '//.*$', '')
                if ($bodyLine -match '^\s*else\b') {
                    break
                }
                $assign = [regex]::Match(
                    $bodyLine,
                    '^\s*(?<target>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>[1-9][0-9]{0,5})\s*;')
                if (-not $assign.Success) {
                    continue
                }
                $targetName = $assign.Groups['target'].Value
                if (-not (Test-Identifier $targetName) -or
                    $targetName.Equals($conditionName, [System.StringComparison]::OrdinalIgnoreCase) -or
                    $functionDefs.ContainsKey($targetName) -or
                    (Test-FunctionDeclaresVariable $text $targetName)) {
                    continue
                }
                $key = "$($source.functionName)|$conditionName|$targetName"
                if (-not $seen.Add($key)) {
                    continue
                }
                $result.Add([ordered]@{
                    functionName = $source.functionName
                    filePath = $source.filePath
                    line = [int]$source.startLine + $i
                    condition = $conditionName
                    target = $targetName
                    forceValue = 1
                    expectedValue = [uint32]$assign.Groups['value'].Value
                })
                break
            }
        }
    }
    return $result
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

function New-WorkerWritePayload {
    param([string]$Name, [uint32]$Value)
    return [ordered]@{
        key = $Name
        name = $Name
        rawValue = $Value
        size = 4
    }
}

function Get-WorkerValue {
    param([object]$Response, [string]$Key)
    if ($null -eq $Response -or $null -eq $Response.values) {
        return $null
    }
    foreach ($property in $Response.values.PSObject.Properties) {
        if ($property.Name.Equals($Key, [System.StringComparison]::OrdinalIgnoreCase)) {
            return [uint32]$property.Value
        }
    }
    return $null
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
    Where-Object { $_.Score -ge 20 -and [bool]$_.Def.canCallWithoutArgs } |
    Sort-Object -Property @{ Expression = "Score"; Descending = $true }, @{ Expression = { $_.Def.functionName }; Descending = $false } |
    Select-Object -First 3 |
    ForEach-Object { $_.Def.functionName })

if ($roots.Count -eq 0) {
    $roots = @($functionDefs.Values | Where-Object { [bool]$_.canCallWithoutArgs } | Sort-Object functionName | Select-Object -First 1 | ForEach-Object { $_.functionName })
}

$sources = Build-ReachableSources $roots 500
if ($sources.Count -eq 0) {
    throw "No reachable application sources from roots: $($roots -join ', ')"
}

$branchCandidates = @(Find-BranchForceCandidates $sources | Select-Object -First 24)
if ($branchCandidates.Count -eq 0) {
    throw "No forceable branch candidate was discovered in reachable application sources."
}

$branchPassed = $false
$branchSummary = ""
$lastBranchError = ""
foreach ($branch in $branchCandidates) {
    $variables = @(
        (New-SmokeVariable "__offline_smoke_value" 1),
        (New-SmokeVariable $branch.condition 2),
        (New-SmokeVariable $branch.target 3)
    )
    $project = [ordered]@{
        workDirectory = $ProjectSrc
        signature = "real-project-auto-app-smoke"
        rootFunctions = $roots
        sources = $sources
        variables = $variables
    }
    $process = Start-Worker
    try {
        $init = Send-WorkerCommand $process "InitProject" $project
        if (-not [bool]$init.ok) {
            throw "InitProject failed: $($init.status)"
        }
        $baselineRun = Send-WorkerCommand $process "RunTick" $null
        if (-not [bool]$baselineRun.ok) {
            throw "RunTick failed: $($baselineRun.status)"
        }
        $baselineTarget = Get-WorkerValue $baselineRun $branch.target
        if ($null -eq $baselineTarget) {
            throw "Baseline snapshot did not include $($branch.target)."
        }
        if ([uint32]$baselineTarget -ne 0) {
            $lastBranchError = "Candidate $($branch.condition) -> $($branch.target) skipped: baseline target was $baselineTarget."
            continue
        }

        $force = Send-WorkerCommand $process "ForceVariable" (New-WorkerWritePayload $branch.condition ([uint32]$branch.forceValue))
        if (-not [bool]$force.ok) {
            throw "ForceVariable failed: $($force.status)"
        }
        $forcedRun = Send-WorkerCommand $process "RunTick" $null
        if (-not [bool]$forcedRun.ok) {
            throw "Forced RunTick failed: $($forcedRun.status)"
        }
        $forcedTarget = Get-WorkerValue $forcedRun $branch.target
        if ($null -eq $forcedTarget) {
            throw "Forced snapshot did not include $($branch.target)."
        }
        $expected = [uint32]$branch.expectedValue
        $forced = [uint32]$forcedTarget
        $branchExecuted =
            $forced -eq $expected -or
            ($expected -gt 1 -and $forced -gt 0 -and $forced -le $expected)
        if (-not $branchExecuted) {
            $lastBranchError = "Candidate $($branch.condition) -> $($branch.target) expected $($branch.expectedValue), got $forcedTarget."
            continue
        }

        $coverage = [string]::Join("`n", @($init.coverage) + @($baselineRun.coverage) + @($forcedRun.coverage))
        if ($coverage -match "业务调用被 stub") {
            Write-Warning "Some unresolved business calls remain:`n$coverage"
        }
        $branchSummary = "$($branch.condition)=$($branch.forceValue) -> $($branch.target)=$forcedTarget at $($branch.functionName):$($branch.line)"
        $branchPassed = $true
        break
    }
    catch {
        $lastBranchError = $_.Exception.Message
    }
    finally {
        try { [void](Send-WorkerCommand $process "Shutdown" $null 5000) } catch {}
        if ($process -and -not $process.HasExited) {
            try { $process.Kill() } catch {}
        }
    }
}

if (-not $branchPassed) {
    throw "Force branch check failed after $($branchCandidates.Count) candidates. Last: $lastBranchError"
}

Write-Host "Real project offline smoke passed."
Write-Host "Roots: $($roots -join ', ')"
Write-Host "Application sources: $($sources.Count); smoke variables: 3"
Write-Host "Force branch check passed: $branchSummary"
