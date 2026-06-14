$ErrorActionPreference = 'Stop'

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectDir 'CanVariableMonitor.csproj'
$workerProject = [System.IO.Path]::GetFullPath((Join-Path $projectDir '..\CanVariableMonitor.OfflineCWorker\CanVariableMonitor.OfflineCWorker.csproj'))
$appName = [string]::Concat([char]0x4E0A, [char]0x4F4D, [char]0x673A, [char]0x76D1, [char]0x63A7)
$upperComputerName = [string]::Concat([char]0x4E0A, [char]0x4F4D, [char]0x673A)
$monitorName = [string]::Concat([char]0x76D1, [char]0x63A7)
$workName = [string]::Concat([char]0x5DE5, [char]0x4F5C)
$modelName = [string]::Concat('AI', [char]0x6A21, [char]0x578B)
$publishDir = Join-Path $projectDir ('dist\' + $appName)
$workerPublishDir = Join-Path $publishDir 'offline_c_worker'
$releaseDir = Join-Path $projectDir 'release'
$legacyDeployRoot = [System.IO.Path]::Combine('F:\', $workName, $modelName, ('s' + $upperComputerName), ($monitorName + $upperComputerName), $upperComputerName)
$legacyDeployDir = Join-Path $legacyDeployRoot ($upperComputerName + $monitorName + '_V1.2_20260612_120554')
$processNames = @($appName, 'CanVariableMonitor', 'CanVariableMonitor.OfflineCWorker')

function Sync-PublishedFiles {
    param(
        [Parameter(Mandatory = $true)][string]$SourceDir,
        [Parameter(Mandatory = $true)][string]$DestinationDir
    )

    $sourceFull = [System.IO.Path]::GetFullPath($SourceDir)
    $destinationFull = [System.IO.Path]::GetFullPath($DestinationDir)
    $allowedRoot = [System.IO.Path]::GetFullPath($legacyDeployRoot + '\')

    if (-not $destinationFull.StartsWith($allowedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refuse to sync outside deploy root: $destinationFull"
    }

    if ($destinationFull.TrimEnd('\') -eq $allowedRoot.TrimEnd('\')) {
        throw "Refuse to sync directly into deploy root: $destinationFull"
    }

    if (-not (Test-Path -LiteralPath $DestinationDir)) {
        New-Item -ItemType Directory -Path $DestinationDir | Out-Null
    }

    Get-ChildItem -LiteralPath $DestinationDir -Force | Remove-Item -Recurse -Force
    Copy-Item -LiteralPath (Get-ChildItem -LiteralPath $SourceDir -Force).FullName -Destination $DestinationDir -Recurse -Force
}

foreach ($processName in $processNames) {
    Get-Process $processName -ErrorAction SilentlyContinue | ForEach-Object {
        $_.CloseMainWindow() | Out-Null
    }
}

Start-Sleep -Milliseconds 1000

foreach ($processName in $processNames) {
    Get-Process $processName -ErrorAction SilentlyContinue | ForEach-Object {
        if (-not $_.HasExited) {
            Stop-Process -Id $_.Id -Force
        }
    }
}

Start-Sleep -Milliseconds 300

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

dotnet publish $projectFile -c Release -r win-x86 --self-contained false -o $publishDir

if (Test-Path -LiteralPath $workerProject) {
    dotnet publish $workerProject -c Release -r win-x86 --self-contained false -o $workerPublishDir
}

$tinyCcSource = Join-Path $projectDir 'tools\tinycc'
if (Test-Path -LiteralPath $tinyCcSource) {
    $tinyCcTarget = Join-Path $workerPublishDir 'tinycc'
    if (-not (Test-Path -LiteralPath $tinyCcTarget)) {
        New-Item -ItemType Directory -Path $tinyCcTarget | Out-Null
    }
    Copy-Item -LiteralPath (Get-ChildItem -LiteralPath $tinyCcSource -Force).FullName -Destination $tinyCcTarget -Recurse -Force
}

$codePagesCandidates = @(
    (Join-Path $env:USERPROFILE '.nuget\packages\system.text.encoding.codepages\9.0.6\runtimes\win\lib\net9.0\System.Text.Encoding.CodePages.dll'),
    (Join-Path $env:USERPROFILE '.nuget\packages\system.text.encoding.codepages\9.0.6\lib\net9.0\System.Text.Encoding.CodePages.dll'),
    (Join-Path $env:USERPROFILE '.nuget\packages\system.text.encoding.codepages\9.0.6\lib\net8.0\System.Text.Encoding.CodePages.dll')
)

foreach ($candidate in $codePagesCandidates) {
    if (Test-Path -LiteralPath $candidate) {
        Copy-Item -LiteralPath $candidate -Destination (Join-Path $publishDir 'System.Text.Encoding.CodePages.dll') -Force
        break
    }
}

Get-ChildItem -LiteralPath $publishDir -Filter '*.pdb' -File -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force

$sourceLeaks = Get-ChildItem -LiteralPath $publishDir -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in '.cs', '.c', '.h', '.pdb' }

if ($sourceLeaks.Count -gt 0) {
    throw "Customer package contains source/debug files: $($sourceLeaks.Name -join ', ')"
}

Sync-PublishedFiles -SourceDir $publishDir -DestinationDir $legacyDeployDir

$versionLine = Select-String -LiteralPath (Join-Path $projectDir 'MainForm.cs') -Pattern 'UpperComputerVersion\s*=\s*"([^"]+)"' | Select-Object -First 1
$version = if ($versionLine -and $versionLine.Matches.Count -gt 0) { $versionLine.Matches[0].Groups[1].Value } else { 'VUnknown' }
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'

if (Test-Path -LiteralPath $releaseDir) {
    Get-ChildItem -LiteralPath $releaseDir -Force | Remove-Item -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $releaseDir | Out-Null
}

$zipPath = Join-Path $releaseDir ($appName + '_' + $version + '_' + $stamp + '.zip')
Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $publishDir -Force).FullName -DestinationPath $zipPath -Force
$zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$manifest = [ordered]@{
    version = $version
    channel = 'stable'
    packageUrl = [System.IO.Path]::GetFileName($zipPath)
    sha256 = $zipHash
    releaseNotes = ''
    force = $false
}
$manifestPath = Join-Path $releaseDir 'update_manifest.json'
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host ''
Write-Host "Customer package ready:"
Write-Host $publishDir
Write-Host "Legacy deploy synced:"
Write-Host $legacyDeployDir
Write-Host "Release zip:"
Write-Host $zipPath
Write-Host "Update manifest:"
Write-Host $manifestPath
