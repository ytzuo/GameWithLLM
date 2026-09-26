[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$UnityExe,
    [ValidateSet('Full', 'ContentUpdate')]
    [string]$Mode = 'Full',
    [string]$BaselineContentState = '',
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\unity-NPC-agent-client'),
    [int]$EditorTimeoutSeconds = 3600,
    [int]$PlayerTimeoutSeconds = 180,
    [string]$LicensingIpc = ''
)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path -LiteralPath $ProjectPath).Path
$repository = (Resolve-Path -LiteralPath (Join-Path $project '..')).Path
$logs = Join-Path $project 'Logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null

function Invoke-UnityMethod {
    param([string]$Method, [string]$LogName, [string[]]$AdditionalArguments = @())
    $log = Join-Path $logs $LogName
    $arguments = @('-batchmode', '-nographics', '-acceptSoftwareTermsForThisRunOnly',
        '-projectPath', $project, '-logFile', $log, '-executeMethod', $Method) + $AdditionalArguments
    if ($LicensingIpc) {
        $arguments = @('-useHub', '-hubIPC', '-cloudEnvironment', 'production',
            '-licensingIpc', $LicensingIpc) + $arguments
    }
    $process = Start-Process -FilePath $UnityExe -ArgumentList $arguments -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds($EditorTimeoutSeconds)
    while (-not $process.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 5 }
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        throw "Unity method '$Method' timed out. See '$log'."
    }
    if ($process.ExitCode -ne 0) {
        throw "Unity method '$Method' failed with exit code $($process.ExitCode). See '$log'."
    }
}

if ($Mode -eq 'ContentUpdate') {
    if (-not (Test-Path -LiteralPath $BaselineContentState -PathType Leaf)) {
        throw 'ContentUpdate requires -BaselineContentState from the matching archived Player release.'
    }
    $env:CONTENT_BASELINE_STATE_PATH = (Resolve-Path -LiteralPath $BaselineContentState).Path
}

# This step only creates LocalDevelopment smoke output. Production output has one owner below.
Invoke-UnityMethod 'ContentSmokeRunner.RunFromCommandLine' 'content-smoke-player-build.log' `
    @('-contentSmokeLayer', 'tool-package-player')

$manifestPath = Join-Path $project 'Assets\Content\HotUpdate\release-manifest.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$candidate = Join-Path $repository ("Artifacts\Content\{0}" -f $manifest.releaseId)
New-Item -ItemType Directory -Force -Path $candidate | Out-Null
$serverLog = Join-Path $logs 'content-smoke-server.log'
$serverErrorLog = Join-Path $logs 'content-smoke-server.error.log'
$server = Start-Process -FilePath 'python' -ArgumentList @('-m', 'http.server', '8081',
    '--bind', '127.0.0.1', '--directory', $project) -RedirectStandardOutput $serverLog `
    -RedirectStandardError $serverErrorLog -PassThru -WindowStyle Hidden
try {
    $player = Join-Path $project 'Builds\ContentLocalSmoke\GameWithLLM.exe'
    $playerLog = Join-Path $logs 'tool-package-player-smoke.log'
    $process = Start-Process -FilePath $player -ArgumentList @('-batchmode', '-nographics',
        '-logFile', $playerLog, '-gameWithLlmToolPackageSmoke') -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds($PlayerTimeoutSeconds)
    while (-not $process.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        throw "Tool package Player smoke timed out. See '$playerLog'."
    }
    $logText = Get-Content -Raw -LiteralPath $playerLog
    if ($process.ExitCode -ne 0 -or $logText -notmatch 'TOOL_PACKAGE_SMOKE_SUCCESS') {
        throw "Tool package Player smoke failed. See '$playerLog'."
    }
    [ordered]@{
        schemaVersion = 1
        releaseId = $manifest.releaseId
        toolSetVersion = $manifest.toolSetVersion
        manifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash.ToLowerInvariant()
        playerLogSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $playerLog).Hash.ToLowerInvariant()
        successMarker = 'TOOL_PACKAGE_SMOKE_SUCCESS'
    } | ConvertTo-Json | Set-Content -LiteralPath `
        (Join-Path $candidate 'tool-package-smoke.passed.json') -Encoding utf8
}
finally {
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
}

$method = if ($Mode -eq 'Full') {
    'ContentReleasePipeline.BuildFullFromCommandLine'
} else {
    'ContentReleasePipeline.BuildUpdateFromCommandLine'
}
Invoke-UnityMethod $method ("content-{0}-candidate.log" -f $Mode.ToLowerInvariant())
Write-Output "Content $Mode candidate '$($manifest.releaseId)' built at '$candidate'. Publishing remains blocked until content-release-smoke.passed.json is attached."
