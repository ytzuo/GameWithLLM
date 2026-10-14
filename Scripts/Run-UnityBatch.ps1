# Generic Windows Unity batch runner; never starts while an Editor already exists.
[CmdletBinding()]
param(
    [string]$ProjectPath = '',
    [string]$UnityExe = '',
    [string]$LicensingIpc = '',
    [ValidateRange(1, 86400)] [int]$TimeoutSeconds = 240,
    [string]$LogFile = '',
    [string[]]$ExtraArgs = @()
)
$ErrorActionPreference = 'Stop'

function Assert-NoRunningUnityEditor {
    $editors = @(Get-Process -Name Unity -ErrorAction SilentlyContinue)
    if ($editors.Count -gt 0) {
        $editorIds = ($editors | ForEach-Object { $_.Id }) -join ', '
        throw "UNITY_EDITOR_ALREADY_RUNNING: PID $editorIds. Close all running Unity Editors before continuing. No Editor was started or stopped."
    }
}

function Get-LicensingIpcChannel {
    if ([string]::IsNullOrWhiteSpace($env:APPDATA)) { return $null }
    $logDir = Join-Path $env:APPDATA 'UnityHub/logs'
    foreach ($name in @('info-log.json', 'info-log.json.1')) {
        $path = Join-Path $logDir $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
        $candidate = $null
        foreach ($line in Get-Content -LiteralPath $path) {
            try { $entry = $line | ConvertFrom-Json } catch { continue }
            if ($entry.moduleName -eq 'LaunchProcess' -and
                $entry.msg -match "-licensingIpc',\s*'([^']+)'") { $candidate = $Matches[1] }
        }
        if ($candidate) { return $candidate }
    }
    return $null
}

function ConvertTo-WindowsArgument([string]$Argument) {
    if ($null -eq $Argument) { $Argument = '' }
    $quoted = [regex]::Replace($Argument, '(\\*)"', '$1$1\"')
    $quoted = [regex]::Replace($quoted, '(\\+)$', '$1$1')
    return '"' + $quoted + '"'
}

Assert-NoRunningUnityEditor
if (-not $ProjectPath) { throw 'ProjectPath must explicitly name a Unity project.' }
$ProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
foreach ($directory in @('Assets', 'Packages', 'ProjectSettings')) {
    if (-not (Test-Path -LiteralPath (Join-Path $ProjectPath $directory) -PathType Container)) {
        throw "Not a Unity project: missing $directory."
    }
}
$versionFile = Join-Path $ProjectPath 'ProjectSettings/ProjectVersion.txt'
$versionMatch = [regex]::Match((Get-Content -LiteralPath $versionFile -Raw), '(?m)^m_EditorVersion:\s*(\S+)')
if (-not $versionMatch.Success) { throw 'Cannot read m_EditorVersion from ProjectVersion.txt.' }
$editorVersion = $versionMatch.Groups[1].Value
if (-not $UnityExe) {
    $roots = @('D:/Application/Unity')
    if ($env:ProgramFiles) { $roots += (Join-Path $env:ProgramFiles 'Unity/Hub/Editor') }
    if ($env:LOCALAPPDATA) { $roots += (Join-Path $env:LOCALAPPDATA 'Programs/Unity/Hub/Editor') }
    foreach ($root in $roots) {
        $candidate = Join-Path $root "$editorVersion/Editor/Unity.exe"
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { $UnityExe = $candidate; break }
    }
    if (-not $UnityExe) { throw "Unity $editorVersion not found. Supply its exact -UnityExe path." }
}
$UnityExe = (Resolve-Path -LiteralPath $UnityExe).Path
$productVersion = (Get-Item -LiteralPath $UnityExe).VersionInfo.ProductVersion
if (-not $productVersion -or $productVersion -notmatch ('^' + [regex]::Escape($editorVersion) + '(?:\b|_)')) {
    throw "Editor version mismatch: project requires $editorVersion; executable reports $productVersion."
}
foreach ($argument in $ExtraArgs) {
    if ($argument -match '^(?i)-(projectPath|logFile|licensingIpc|accessToken|hubSessionId)(?:=|$)') {
        throw 'ExtraArgs cannot override project/log/licensing options or pass access tokens.'
    }
}
if (-not $LicensingIpc) {
    $LicensingIpc = Get-LicensingIpcChannel
    if (-not $LicensingIpc) { $LicensingIpc = "LicenseClient-$env:USERNAME" }
}
if (-not $LogFile) { $LogFile = Join-Path $ProjectPath 'Logs/unity-batch-run.log' }
$LogFile = [IO.Path]::GetFullPath($LogFile)
$unityArgs = @('-batchmode', '-acceptSoftwareTermsForThisRunOnly', '-useHub', '-hubIPC',
    '-cloudEnvironment', 'production', '-licensingIpc', $LicensingIpc,
    '-projectPath', $ProjectPath, '-logFile', $LogFile) + $ExtraArgs
$argumentLine = ($unityArgs | ForEach-Object { ConvertTo-WindowsArgument $_ }) -join ' '
Assert-NoRunningUnityEditor
$logDir = Split-Path -Parent $LogFile
if (-not (Test-Path -LiteralPath $logDir -PathType Container)) {
    New-Item -ItemType Directory -Path $logDir -Force | Out-Null
}
$process = $null
$timedOut = $false
try {
    $process = Start-Process -FilePath $UnityExe -ArgumentList $argumentLine -PassThru -WindowStyle Hidden
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while (-not $process.HasExited -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
    }
    if (-not $process.HasExited) { $timedOut = $true; $process.Kill() }
    $process.WaitForExit()
    [pscustomobject]@{
        ExitCode = $process.ExitCode
        TimedOut = $timedOut
        EditorVersion = $editorVersion
        ProjectPath = $ProjectPath
        LogFile = $LogFile
    }
} finally {
    if ($null -ne $process) {
        if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
        $process.Dispose()
    }
}
