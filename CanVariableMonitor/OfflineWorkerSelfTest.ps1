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
        signature = "offline-worker-selftest-force-chain"
        rootFunctions = @("MyLogic_10ms")
        sources = @(
            [ordered]@{
                functionName = "MyLogic_10ms"
                filePath = "selftest.c"
                startLine = 1
                lines = @(
                    "void MyLogic_10ms(void)",
                    "{",
                    "  ZY_logic();",
                    "}"
                )
            },
            [ordered]@{
                functionName = "ZY_logic"
                filePath = "selftest.c"
                startLine = 10
                lines = @(
                    "void ZY_logic(void)",
                    "{",
                    "  KM_NO(1, 1);",
                    "}"
                )
            },
            [ordered]@{
                functionName = "KM_NO"
                filePath = "selftest.c"
                startLine = 20
                lines = @(
                    "void KM_NO(unsigned int vMS, unsigned int vNo)",
                    "{",
                    "  if (B134 == 1 && hand_auto == 1)",
                    "  {",
                    "    NO2_SW_count++;",
                    "    if (NO2_SW_count > 3)",
                    "    {",
                    "      Auto_work_flags_KM1_1 = 1;",
                    "    }",
                    "  }",
                    "  else",
                    "  {",
                    "    NO2_SW_count = 0;",
                    "  }",
                    "}"
                )
            }
        )
        variables = @(
            [ordered]@{ key = "B134"; name = "B134"; address = 1; size = 1; typeName = "uint8"; rawValue = 0; forceActive = $false; aliases = @("B134") },
            [ordered]@{ key = "hand_auto"; name = "hand_auto"; address = 2; size = 1; typeName = "uint8"; rawValue = 0; forceActive = $false; aliases = @("hand_auto") },
            [ordered]@{ key = "NO2_SW_count"; name = "NO2_SW_count"; address = 3; size = 2; typeName = "uint16"; rawValue = 0; forceActive = $false; aliases = @("NO2_SW_count") },
            [ordered]@{ key = "Auto_work_flags_KM1_1"; name = "Auto_work_flags_KM1_1"; address = 4; size = 1; typeName = "uint8"; rawValue = 0; forceActive = $false; aliases = @("Auto_work_flags_KM1_1") }
        )
    }

    $result = Send-WorkerCommand "InitProject" $project
    Assert-Equal $result.ok $true "InitProject failed"
    Assert-Equal $result.engineAvailable $true "TinyCC engine unavailable"

    $result = Send-WorkerCommand "ForceVariable" ([ordered]@{ key = "B134"; name = "B134"; rawValue = 1; size = 1 })
    Assert-Equal $result.ok $true "Force B134 failed"
    $result = Send-WorkerCommand "ForceVariable" ([ordered]@{ key = "hand_auto"; name = "hand_auto"; rawValue = 1; size = 1 })
    Assert-Equal $result.ok $true "Force hand_auto failed"

    for ($i = 1; $i -le 5; $i++) {
        $result = Send-WorkerCommand "RunTick" $null
        Assert-Equal $result.ok $true "RunTick $i failed"
        Assert-Equal $result.values.NO2_SW_count $i "NO2_SW_count did not advance on tick $i"
        $stubWarnings = @($result.coverage | Where-Object { $_ -like "*stub*" })
        if ($stubWarnings.Count -gt 0) {
            throw "Business helper was stubbed during self-test: $($stubWarnings -join '; ')"
        }
    }
    Assert-Equal $result.values.Auto_work_flags_KM1_1 1 "Auto_work_flags_KM1_1 was not set after repeated ticks"

    $latestTick = Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA "CanVariableMonitor\offline_c_worker") -Recurse -Filter "canmon_tick.c" -File |
        Where-Object { $_.FullName -like "*offline-worker-selftest-force-chain*" } |
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
    if ($mainText -match "(?m)^\s+ZY_logic\(\);") {
        throw "ZY_logic was scheduled as a root. Helpers must be reached through MyLogic_10ms only."
    }
    if ($mainText -match "(?m)^\s+KM_NO\(\);") {
        throw "KM_NO was scheduled as a root. Helpers must be reached through MyLogic_10ms -> ZY_logic only."
    }

    Write-Host "Offline worker self-test passed: MyLogic_10ms -> ZY_logic -> KM_NO advanced 5 ticks under forced inputs."
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
