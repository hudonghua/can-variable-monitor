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
        rootFunctions = @("App_Tick10ms")
        sources = @(
            [ordered]@{
                functionName = "App_Tick10ms"
                filePath = "App\selftest.c"
                startLine = 1
                lines = @(
                    "void App_Tick10ms(void)",
                    "{",
                    "  App_BusinessStep();",
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
                functionName = "App_OutputLatch"
                filePath = "App\selftest.c"
                startLine = 20
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
            [ordered]@{ key = "OutputLatch"; name = "OutputLatch"; address = 4; size = 1; typeName = "uint8"; rawValue = 0; forceActive = $false; aliases = @("OutputLatch") }
        )
    }

    $result = Send-WorkerCommand "InitProject" $project
    Assert-Equal $result.ok $true "InitProject failed"
    Assert-Equal $result.engineAvailable $true "TinyCC engine unavailable"

    $result = Send-WorkerCommand "ForceVariable" ([ordered]@{ key = "InputReady"; name = "InputReady"; rawValue = 1; size = 1 })
    Assert-Equal $result.ok $true "Force InputReady failed"
    $result = Send-WorkerCommand "ForceVariable" ([ordered]@{ key = "ModeAuto"; name = "ModeAuto"; rawValue = 1; size = 1 })
    Assert-Equal $result.ok $true "Force ModeAuto failed"

    for ($i = 1; $i -le 5; $i++) {
        $result = Send-WorkerCommand "RunTick" $null
        Assert-Equal $result.ok $true "RunTick $i failed"
        Assert-Equal $result.values.OutputCount $i "OutputCount did not advance on tick $i"
        $stubWarnings = @($result.coverage | Where-Object { $_ -like "*stub*" })
        $businessStubWarnings = @($stubWarnings | Where-Object { $_ -like "*业务调用被 stub*" })
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

    if ($generated -notmatch "__canmon_record_output\(""CAN_SendFrame""\)") {
        throw "Output boundary CAN_SendFrame was not converted to output recording stub."
    }

    Write-Host "Offline worker self-test passed: generic app chain advanced 5 ticks; bottom CAN_SendFrame was recorded, not compiled as driver code."
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
