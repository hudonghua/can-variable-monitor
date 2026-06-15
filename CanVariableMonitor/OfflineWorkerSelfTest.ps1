param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$workerProject = Join-Path $repoRoot "CanVariableMonitor.OfflineCWorker\CanVariableMonitor.OfflineCWorker.csproj"
$workerDll = Join-Path $repoRoot "CanVariableMonitor.OfflineCWorker\bin\$Configuration\net9.0\CanVariableMonitor.OfflineCWorker.dll"

dotnet build $workerProject -c $Configuration | Out-Host
if (-not (Test-Path -LiteralPath $workerDll)) {
    throw "Worker dll not found: $workerDll"
}

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = "dotnet"
$psi.Arguments = '"' + $workerDll + '"'
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true

$process = [System.Diagnostics.Process]::Start($psi)
$script:requestId = 0

function Send-WorkerCommand {
    param(
        [string]$Command,
        [object]$Payload
    )

    $script:requestId++
    $request = [ordered]@{
        id = $script:requestId
        command = $Command
        payload = $Payload
    }
    $json = $request | ConvertTo-Json -Depth 30 -Compress
    $process.StandardInput.WriteLine($json)
    $process.StandardInput.Flush()

    $line = $process.StandardOutput.ReadLine()
    if ([string]::IsNullOrWhiteSpace($line)) {
        $err = $process.StandardError.ReadToEnd()
        throw "No worker output for $Command. $err"
    }

    return $line | ConvertFrom-Json
}

function Assert-Equal {
    param(
        [object]$Actual,
        [object]$Expected,
        [string]$Message
    )
    if ("$Actual" -ne "$Expected") {
        throw "$Message. Expected=$Expected Actual=$Actual"
    }
}

try {
    $project = [ordered]@{
        workDirectory = "C:\Temp\canmon_offline_selftest"
        signature = "offline-worker-selftest-app-chain"
        rootFunctions = @("App_Tick10ms", "DisplaySupplement")
        sources = @(
            [ordered]@{
                functionName = "App_Tick10ms"
                filePath = "App\selftest.c"
                startLine = 1
                lines = @(
                    "void App_Tick10ms(void)",
                    "{",
                    "  DI_Scan();",
                    "  App_BusinessStep();",
                    "  TaskHook();",
                    "  main();",
                    "  Sys_Write_BD();",
                    "  CAN_SendFrame(0, 0);",
                    "}"
                )
            },
            [ordered]@{
                functionName = "App_BusinessStep"
                filePath = "App\selftest.c"
                startLine = 10
                lines = @(
                    "void App_BusinessStep(void)",
                    "{",
                    "  App_OutputLatch(1, 1);",
                    "}"
                )
            },
            [ordered]@{
                functionName = "DI_Scan"
                filePath = "App\input.c"
                startLine = 30
                lines = @(
                    "void DI_Scan(void)",
                    "{",
                    "  InputReady = 1;",
                    "}"
                )
            },
            [ordered]@{
                functionName = "DisplaySupplement"
                filePath = "App\display.c"
                startLine = 35
                lines = @(
                    "void DisplaySupplement(void)",
                    "{",
                    "  DisplayCount = OutputCount;",
                    "}"
                )
            },
            [ordered]@{
                functionName = "Sys_Write_BD"
                filePath = "App\storage.c"
                startLine = 38
                lines = @(
                    "void Sys_Write_BD(void)",
                    "{",
                    "  BadStorage = 9;",
                    "}"
                )
            },
            [ordered]@{
                functionName = "App_OutputLatch"
                filePath = "App\selftest.c"
                startLine = 40
                lines = @(
                    "void App_OutputLatch(unsigned int vMS, unsigned int vNo)",
                    "{",
                    "  if (InputReady == 1 && ModeAuto == 1)",
                    "  {",
                    "    OutputCount++;",
                    "    if (OutputCount > 3)",
                    "    {",
                    "      OutputLatch = 1;",
                    "    }",
                    "  }",
                    "  else",
                    "  {",
                    "    OutputCount = 0;",
                    "  }",
                    "}"
                )
            }
        )
        variables = @(
            [ordered]@{ key = "InputReady"; name = "InputReady"; address = 1; size = 1; typeName = "uint8"; rawValue = 0; forceActive = $false; aliases = @("InputReady") },
            [ordered]@{ key = "ModeAuto"; name = "ModeAuto"; address = 2; size = 1; typeName = "uint8"; rawValue = 0; forceActive = $false; aliases = @("ModeAuto") },
            [ordered]@{ key = "OutputCount"; name = "OutputCount"; address = 3; size = 2; typeName = "uint16"; rawValue = 0; forceActive = $false; aliases = @("OutputCount") },
            [ordered]@{ key = "OutputLatch"; name = "OutputLatch"; address = 4; size = 1; typeName = "uint8"; rawValue = 0; forceActive = $false; aliases = @("OutputLatch") },
            [ordered]@{ key = "TaskHook"; name = "TaskHook"; address = 5; size = 4; typeName = "uint32"; rawValue = 0; forceActive = $false; aliases = @("TaskHook") },
            [ordered]@{ key = "DisplayCount"; name = "DisplayCount"; address = 6; size = 2; typeName = "uint16"; rawValue = 0; forceActive = $false; aliases = @("DisplayCount") },
            [ordered]@{ key = "BadStorage"; name = "BadStorage"; address = 7; size = 1; typeName = "uint8"; rawValue = 0; forceActive = $false; aliases = @("BadStorage") }
        )
    }

    $result = Send-WorkerCommand "InitProject" $project
    Assert-Equal $result.ok $true "InitProject failed"
    Assert-Equal $result.engineAvailable $true "TinyCC engine unavailable"

    $result = Send-WorkerCommand "ForceVariable" ([ordered]@{ key = "ModeAuto"; name = "ModeAuto"; rawValue = 1; size = 1 })
    Assert-Equal $result.ok $true "Force ModeAuto failed"

    for ($i = 1; $i -le 5; $i++) {
        $result = Send-WorkerCommand "RunTick" $null
        Assert-Equal $result.ok $true "RunTick $i failed"
        Assert-Equal $result.values.OutputCount $i "OutputCount did not advance on tick $i"
        Assert-Equal $result.values.DisplayCount $i "Display supplement root did not observe control output on tick $i"
        Assert-Equal $result.values.BadStorage 0 "Storage boundary Sys_Write_BD executed real body on tick $i"
        $stubWarnings = @($result.coverage | Where-Object { $_ -like "*stub*" })
        $businessStubWarnings = @($stubWarnings | Where-Object { $_ -like "*业务调用被 stub*" -and $_ -notlike "*TaskHook*" -and $_ -notlike "*Sys_Write_BD*" })
        if ($businessStubWarnings.Count -gt 0) {
            throw "Business helper was stubbed during self-test: $($businessStubWarnings -join '; ')"
        }
    }
    Assert-Equal $result.values.OutputLatch 1 "OutputLatch was not set after repeated ticks"

    $latestTick = Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA "CanVariableMonitor\offline_c_worker") -Recurse -Filter "canmon_tick.c" -File |
        Where-Object { $_.FullName -like "*offline-worker-selftest-app-chain*" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $latestTick) {
        throw "Generated canmon_tick.c was not found for self-test."
    }
    $generated = Get-Content -Raw -LiteralPath $latestTick.FullName
    $mainIndex = $generated.IndexOf("int main()", [StringComparison]::Ordinal)
    if ($mainIndex -lt 0) {
        throw "Generated canmon_tick.c has no main()."
    }
    $mainText = $generated.Substring($mainIndex)
    if ($mainText -match "(?m)^\s+App_BusinessStep\(\);") {
        throw "App_BusinessStep was scheduled as a root. Helpers must be reached through App_Tick10ms only."
    }
    if ($mainText -match "(?m)^\s+App_OutputLatch\(\);") {
        throw "App_OutputLatch was scheduled as a root. Helpers must be reached through App_Tick10ms -> App_BusinessStep only."
    }
    if ($mainText -notmatch "(?m)^\s+DisplaySupplement\(\);") {
        throw "DisplaySupplement was not scheduled as an additional root. Control and display roots must be able to run in one tick."
    }

    if ($generated -notmatch "__canmon_record_output\(""CAN_SendFrame""\)") {
        throw "Output boundary CAN_SendFrame was not converted to output recording stub."
    }
    if ($generated -match "(?m)^#define\s+DI_Scan\(\.\.\.\)\s+__canmon_stub_DI_Scan\(\)\s*$") {
        throw "Application DI_Scan() source was incorrectly converted to a stub macro."
    }
    if ($generated -notmatch "void\s+DI_Scan\s*\(\s*void\s*\)") {
        throw "Application DI_Scan() source was not compiled into the offline worker."
    }
    if ($generated -match "(?m)^#define\s+TaskHook\s+__cm_v\d+\s*$") {
        throw "Function-like variable alias TaskHook was emitted as a storage macro and can conflict with the TaskHook() stub."
    }
    if ($generated -notmatch "(?m)^#define\s+TaskHook\(\.\.\.\)\s+__canmon_stub_TaskHook\(\)\s*$") {
        throw "Function-like TaskHook() call was not converted to a callable stub macro."
    }
    if ($generated -match "void\s+Sys_Write_BD\s*\(\s*void\s*\)") {
        throw "Storage boundary Sys_Write_BD() source was compiled. Storage/EEPROM boundaries must stay stubbed."
    }
    if ($generated -notmatch "(?m)^#define\s+Sys_Write_BD\(\.\.\.\)\s+__canmon_stub_Sys_Write_BD\(\)\s*$") {
        throw "Storage boundary Sys_Write_BD() call was not converted to a stub macro."
    }
    if ($generated -notmatch "(?m)^#define\s+main\(\.\.\.\)\s+__canmon_stub_main\(\)\s*$") {
        throw "Customer main() reference was not converted to a stub macro."
    }
    if ($generated -notmatch "(?s)#undef\s+main\s+int\s+main\(\)") {
        throw "Worker harness main() was not protected from the customer main() stub macro."
    }

    Write-Host "Offline worker self-test passed: generic app chain advanced 5 ticks; output stubs and function-like variable aliases are handled."
}
finally {
    try {
        [void](Send-WorkerCommand "Shutdown" $null)
    }
    catch {
    }
    if ($process -and -not $process.HasExited) {
        $process.Kill()
    }
}
