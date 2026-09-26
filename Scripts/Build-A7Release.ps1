[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$UnityExe,
    [ValidateSet('Full', 'ContentUpdate')]
    [string]$Mode = 'Full',
    [string]$BaselineContentState = '',
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\unity-NPC-agent-client'),
    [int]$EditorTimeoutSeconds = 3600,
    [string]$LicensingIpc = ''
)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path -LiteralPath $ProjectPath).Path
$logs = Join-Path $project 'Logs'
New-Item -ItemType Directory -Force -Path $logs | Out-Null

if ($Mode -eq 'ContentUpdate') {
    if (-not (Test-Path -LiteralPath $BaselineContentState -PathType Leaf)) {
        throw 'ContentUpdate requires -BaselineContentState from the matching archived Player release.'
    }
    $env:A7_CONTENT_STATE_PATH = (Resolve-Path -LiteralPath $BaselineContentState).Path
}

$method = if ($Mode -eq 'Full') {
    'AddressablesA7ReleasePipeline.BuildFullReleaseCandidateFromCommandLine'
} else {
    'AddressablesA7ReleasePipeline.BuildContentUpdateCandidateFromCommandLine'
}
$log = Join-Path $logs ("a7-{0}.log" -f $Mode.ToLowerInvariant())
$arguments = @('-batchmode', '-nographics', '-acceptSoftwareTermsForThisRunOnly',
    '-projectPath', $project, '-logFile', $log, '-executeMethod', $method)
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
    throw "A7 $Mode build timed out. See '$log'."
}
if ($process.ExitCode -ne 0) {
    throw "A7 $Mode build failed with exit code $($process.ExitCode). See '$log'."
}

$release = Get-Content -Raw -LiteralPath (Join-Path $project 'Assets\Content\HotUpdate\release-manifest.json') |
    ConvertFrom-Json
$candidate = Join-Path (Resolve-Path (Join-Path $project '..')).Path ("Artifacts\A7\{0}" -f $release.releaseId)
Write-Output "A7 $Mode candidate '$($release.releaseId)' built at '$candidate'. Promotion remains blocked until a7-smoke.passed.json is attached."
