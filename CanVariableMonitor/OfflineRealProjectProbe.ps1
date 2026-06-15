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

$functionDefs = [System.Collections.Generic.Dictionary[string, object]]::new([System.StringComparer]::OrdinalIgnoreCase)

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

function Load-FunctionDefinitions {
    param([string]$Directory)

    foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File -Include *.c,*.h) {
        $text = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::Default).Replace("`r`n", "`n").Replace("`r", "`n")
        $code = Remove-CodeCommentsPreserveLength $text
        foreach ($match in [regex]::Matches($code, '\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\([^;{}]*\)\s*\{')) {
            $name = $match.Groups["name"].Value
            if ($name -in @("if", "while", "switch", "for")) {
                continue
            }
            $key = $name.ToLowerInvariant()
            if ($functionDefs.ContainsKey($key)) {
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
            $functionDefs[$key] = [ordered]@{
                functionName = $name
                filePath = $file.FullName
                startLine = Get-LineNumber $text $lineStart
                lines = @($sourceText -split "`n")
                text = $sourceText
            }
        }
    }
}

function Get-CalledFunctions {
    param([object]$FunctionDef)

    $skip = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($word in @("if", "while", "switch", "for", "return", "sizeof", "abs", "printf", "memset", "memcpy")) {
        [void]$skip.Add($word)
    }

    $result = [System.Collections.Generic.List[string]]::new()
    $code = Remove-CodeCommentsPreserveLength ([string]$FunctionDef.text)
    foreach ($match in [regex]::Matches($code, '\b(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(')) {
        $name = $match.Groups["name"].Value
        if ($skip.Contains($name) -or $name.Equals([string]$FunctionDef.functionName, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        if (-not $result.Contains($name)) {
            $result.Add($name)
        }
    }
    return $result
}

function Build-ReachableSources {
    param([string[]]$RootFunctions, [int]$Limit = 800)

    $queue = [System.Collections.Generic.Queue[string]]::new()
    $seen = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    $sources = [System.Collections.Generic.List[object]]::new()
    foreach ($root in $RootFunctions) {
        $queue.Enqueue($root)
    }

    while ($queue.Count -gt 0 -and $sources.Count -lt $Limit) {
        $name = $queue.Dequeue()
        if (-not $seen.Add($name)) {
            continue
        }
        $key = $name.ToLowerInvariant()
        if (-not $functionDefs.ContainsKey($key)) {
            continue
        }
        $def = $functionDefs[$key]
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

function New-Variable {
    param(
        [string]$Name,
        [uint32]$Address,
        [uint32]$RawValue,
        [bool]$ForceActive = $false
    )
    return [ordered]@{
        key = $Name
        name = $Name
        address = $Address
        size = 4
        typeName = "uint32"
        rawValue = $RawValue
        forceActive = $ForceActive
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
    param(
        [System.Diagnostics.Process]$Process,
        [string]$Command,
        [object]$Payload,
        [int]$TimeoutMs = 60000
    )

    $script:requestId++
    $request = [ordered]@{
        id = $script:requestId
        command = $Command
        payload = $Payload
    }
    $json = $request | ConvertTo-Json -Depth 80 -Compress
    $Process.StandardInput.WriteLine($json)
    $Process.StandardInput.Flush()

    $readTask = $Process.StandardOutput.ReadLineAsync()
    if (-not $readTask.Wait($TimeoutMs)) {
        throw "Worker command timed out: $Command after $TimeoutMs ms. ProcessId=$($Process.Id)"
    }
    $line = $readTask.Result
    if ([string]::IsNullOrWhiteSpace($line)) {
        $err = ""
        if ($Process.HasExited) {
            $err = $Process.StandardError.ReadToEnd()
        }
        throw "No worker output for $Command. $err"
    }
    return $line | ConvertFrom-Json
}

function Assert-True {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) {
        throw $Message
    }
}

function Run-Scenario {
    param(
        [string]$Name,
        [uint32]$HandAuto,
        [uint32]$ExpectedAutoWorkFlag
    )

    $variables = @(
        (New-Variable "NO2_SW" 1 1 $true),
        (New-Variable "NO2_Up_Down" 2 63 $true),
        (New-Variable "NO2_Left_Right" 3 63 $true),
        (New-Variable "NO2_SW_count" 4 0),
        (New-Variable "Auto_work_flags_KM1_1" 5 0),
        (New-Variable "B132" 6 0 $true),
        (New-Variable "kognda_press_set" 7 100 $true),
        (New-Variable "Auto_work_flags_KM1" 8 0),
        (New-Variable "kongda_begin_flag" 9 1 $true),
        (New-Variable "hand_auto" 10 $HandAuto $true),
        (New-Variable "Drill_Lash_PWM_KM1" 11 1 $true),
        (New-Variable "Lash_force" 12 0 $true),
        (New-Variable "kognda_dly_com1" 13 12 $true),
        (New-Variable "Kongda_flag" 14 0),
        (New-Variable "zy_flag" 15 14),
        (New-Variable "B188" 16 0 $true),
        (New-Variable "Gas_alarm_low_SET" 17 0 $true),
        (New-Variable "gDJ_OK" 18 1 $true),
        (New-Variable "B188_dly_count" 19 200)
    )

    $process = Start-Worker
    try {
        $project = [ordered]@{
            workDirectory = $ProjectSrc
            signature = "real-project-$Name"
            rootFunctions = @("MyLogic_10ms")
            sources = $script:reachableSources
            variables = $variables
        }

        $init = Send-WorkerCommand $process "InitProject" $project 90000
        Assert-True ([bool]$init.ok) "$Name InitProject failed: $($init.status)"

        $run = $null
        for ($i = 1; $i -le 51; $i++) {
            $run = Send-WorkerCommand $process "RunTick" $null 30000
            Assert-True ([bool]$run.ok) "$Name RunTick $i failed: $($run.status)"
            if ($i % 10 -eq 0 -or $i -eq 51) {
                Write-Host "$Name tick ${i}: NO2_SW_count=$($run.values.NO2_SW_count), Auto_work_flags_KM1_1=$($run.values.Auto_work_flags_KM1_1), Kongda_flag=$($run.values.Kongda_flag), zy_flag=$($run.values.zy_flag)"
            }
        }

        $coverageText = [string]::Join("`n", @($init.coverage) + @($run.coverage))
        Assert-True ($coverageText -match "MyLogic_10ms") "$Name did not report MyLogic_10ms coverage."
        Assert-True ($coverageText -notmatch "KM_NO") "$Name unexpectedly stubbed KM_NO: $coverageText"
        Assert-True ($coverageText -notmatch "ZY_protect1") "$Name unexpectedly stubbed ZY_protect1: $coverageText"
        Assert-True ([uint32]$run.values.NO2_SW_count -eq 201) "$Name NO2_SW_count did not reach 201. Actual=$($run.values.NO2_SW_count)"
        Assert-True ([uint32]$run.values.Auto_work_flags_KM1_1 -eq $ExpectedAutoWorkFlag) "$Name Auto_work_flags_KM1_1 mismatch. Expected=$ExpectedAutoWorkFlag Actual=$($run.values.Auto_work_flags_KM1_1)"
        Assert-True ([uint32]$run.values.Kongda_flag -eq 1) "$Name Kongda_flag was not set by ZY_protect1. Actual=$($run.values.Kongda_flag)"
        Assert-True ([uint32]$run.values.zy_flag -eq 7) "$Name zy_flag was not set by ZY_protect1. Actual=$($run.values.zy_flag)"
        Assert-True ([uint32]$run.values.B188_dly_count -eq 201) "$Name B188_dly_count did not saturate. Actual=$($run.values.B188_dly_count)"

        Write-Host "$Name passed: NO2_SW_count=$($run.values.NO2_SW_count), Auto_work_flags_KM1_1=$($run.values.Auto_work_flags_KM1_1), Kongda_flag=$($run.values.Kongda_flag), zy_flag=$($run.values.zy_flag), B188_dly_count=$($run.values.B188_dly_count)"
    }
    finally {
        try {
            [void](Send-WorkerCommand $process "Shutdown" $null 5000)
        }
        catch {
        }
        if ($process -and -not $process.HasExited) {
            try {
                $process.Kill()
            }
            catch {
            }
        }
    }
}

Load-FunctionDefinitions $ProjectSrc
$script:reachableSources = Build-ReachableSources @("MyLogic_10ms") 800

$names = @($script:reachableSources | ForEach-Object { $_.functionName })
Assert-True ($names -contains "MyLogic_10ms") "Reachable sources missing MyLogic_10ms."
Assert-True ($names -contains "KM_NO") "Reachable sources missing KM_NO."
Assert-True ($names -contains "ZY_protect1") "Reachable sources missing ZY_protect1."

Write-Host "Reachable functions: $($script:reachableSources.Count). Includes KM_NO and ZY_protect1."
Run-Scenario "hand_auto_0" 0 1
Run-Scenario "hand_auto_1" 1 0
Write-Host "Real project offline probe passed."
