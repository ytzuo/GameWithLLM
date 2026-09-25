[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$UnityExe,
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\unity-NPC-agent-client'),
    [int]$EditorTimeoutSeconds = 1800,
    [int]$PlayerTimeoutSeconds = 180,
    [string]$LicensingIpc = ''
)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path -LiteralPath $ProjectPath).Path
$repository = (Resolve-Path -LiteralPath (Join-Path $project '..')).Path
$logs = Join-Path $project 'Logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null

function Invoke-UnityMethod {
    param([string]$Method, [string]$LogName)
    $log = Join-Path $logs $LogName
    $arguments = @('-batchmode', '-nographics', '-acceptSoftwareTermsForThisRunOnly',
        '-projectPath', $project, '-logFile', $log, '-executeMethod', $Method)
    if ($LicensingIpc) {
        $arguments = @('-useHub', '-hubIPC', '-cloudEnvironment', 'production',
            '-licensingIpc', $LicensingIpc) + $arguments
    }
    $process = Start-Process -FilePath $UnityExe -ArgumentList $arguments -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds($EditorTimeoutSeconds)
    while (-not $process.HasExited -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
    }
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        throw "Unity method '$Method' timed out. See '$log'."
    }
    if ($process.ExitCode -ne 0) {
        throw "Unity method '$Method' failed with exit code $($process.ExitCode). See '$log'."
    }
}

Invoke-UnityMethod 'HybridClrH7ReleasePipeline.PrepareCandidateAndSmokePlayerFromCommandLine' 'h7-prepare.log'

$manifestPath = Join-Path $project 'Assets\Content\HotUpdate\release-manifest.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$releaseDirectory = Join-Path $repository ("Artifacts\H7\{0}" -f $manifest.releaseId)
New-Item -ItemType Directory -Force -Path $releaseDirectory | Out-Null
$serverLog = Join-Path $logs 'h7-content-server.log'
$serverErrorLog = Join-Path $logs 'h7-content-server.error.log'
$server = Start-Process -FilePath 'python' -ArgumentList @('-m', 'http.server', '8081',
    '--bind', '127.0.0.1', '--directory', $project) -RedirectStandardOutput $serverLog `
    -RedirectStandardError $serverErrorLog -PassThru -WindowStyle Hidden
try {
    $player = Join-Path $project 'Builds\HybridClrH7Smoke\GameWithLLM.exe'
    $playerLog = Join-Path $logs 'h7-player-smoke.log'
    $process = Start-Process -FilePath $player -ArgumentList @('-batchmode', '-nographics',
        '-logFile', $playerLog, '-gameWithLlmH7Smoke') -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds($PlayerTimeoutSeconds)
    while (-not $process.HasExited -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
    }
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        throw "H7 Player smoke timed out. See '$playerLog'."
    }
    $logText = Get-Content -Raw -LiteralPath $playerLog
    if ($process.ExitCode -ne 0 -or $logText -notmatch 'H7_PLAYER_SMOKE_SUCCESS') {
        throw "H7 Player smoke failed. See '$playerLog'."
    }
    $manifestHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash.ToLowerInvariant()
    $logHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $playerLog).Hash.ToLowerInvariant()
    [ordered]@{
        schemaVersion = 1
        releaseId = $manifest.releaseId
        toolSetVersion = $manifest.toolSetVersion
        manifestSha256 = $manifestHash
        playerLogSha256 = $logHash
        successMarker = 'H7_PLAYER_SMOKE_SUCCESS'
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $releaseDirectory 'h7-player-smoke.passed.json') -Encoding utf8
}
finally {
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
    }
}

Invoke-UnityMethod 'HybridClrH7ReleasePipeline.FinalizeProductionCandidateFromCommandLine' 'h7-finalize.log'
Write-Output "H7 candidate '$($manifest.releaseId)' passed all gates. Artifacts: '$releaseDirectory'."
