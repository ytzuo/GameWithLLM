# Isolated behavioral checks. Start-Process is always mocked; no Unity is launched.
$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot 'Run-UnityBatch.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('UnityLaunchSkillTest-' + [guid]::NewGuid())
$project = Join-Path $testRoot 'Project With Spaces'
$log = Join-Path $project 'Logs With Spaces/run.log'
$global:unityLaunchTest = [pscustomobject]@{ Mode = 'blocked'; Polls = 0; Starts = 0; ArgumentLine = ''; Owned = $null }
$global:unityLaunchTest.Mode = 'blocked'
$global:unityLaunchTest.Polls = 0
$global:unityLaunchTest.Starts = 0
$global:unityLaunchTest.ArgumentLine = ''
$global:unityLaunchTest.Owned = $null

function Get-Process {
    param($Name, $ErrorAction)
    $global:unityLaunchTest.Polls++
    if ($global:unityLaunchTest.Mode -eq 'blocked' -or ($global:unityLaunchTest.Mode -eq 'race' -and $global:unityLaunchTest.Polls -ge 2)) {
        [pscustomobject]@{ Id = 900001 }
    }
}
function Get-Item {
    param($LiteralPath)
    [pscustomobject]@{ VersionInfo = [pscustomobject]@{ ProductVersion = '6000.3.19f1' } }
}
function Start-Process {
    param($FilePath, $ArgumentList, [switch]$PassThru, $WindowStyle)
    $global:unityLaunchTest.Starts++
    if ($WindowStyle -ne 'Hidden') { throw 'Expected hidden startup.' }
    $global:unityLaunchTest.ArgumentLine = $ArgumentList
    $global:unityLaunchTest.Owned = [pscustomobject]@{
        HasExited = ($global:unityLaunchTest.Mode -eq 'success')
        ExitCode = 0
        KillCount = 0
        Disposed = $false
        FailRefresh = ($global:unityLaunchTest.Mode -eq 'interrupted')
    }
    $global:unityLaunchTest.Owned | Add-Member ScriptMethod Refresh { if ($this.FailRefresh) { throw 'SIMULATED_INTERRUPTION' } }
    $global:unityLaunchTest.Owned | Add-Member ScriptMethod Kill { $this.KillCount++; $this.HasExited = $true; $this.ExitCode = -1 }
    $global:unityLaunchTest.Owned | Add-Member ScriptMethod WaitForExit { }
    $global:unityLaunchTest.Owned | Add-Member ScriptMethod Dispose { $this.Disposed = $true }
    return $global:unityLaunchTest.Owned
}
function Require($condition, $message) { if (-not $condition) { throw $message } }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class UnityLaunchArgumentTest {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CommandLineToArgvW(string commandLine, out int argc);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    public static string[] Parse(string line) {
        int count; IntPtr ptr = CommandLineToArgvW("dummy.exe " + line, out count);
        if (ptr == IntPtr.Zero) throw new Exception("Argument parsing failed");
        try {
            string[] args = new string[count - 1];
            for (int i = 1; i < count; i++)
                args[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(ptr, i * IntPtr.Size));
            return args;
        } finally { LocalFree(ptr); }
    }
}
'@

try {
    foreach ($dir in @('Assets', 'Packages', 'ProjectSettings')) {
        New-Item -ItemType Directory -Path (Join-Path $project $dir) -Force | Out-Null
    }
    Set-Content -LiteralPath (Join-Path $project 'ProjectSettings/ProjectVersion.txt') -Value 'm_EditorVersion: 6000.3.19f1'
    $parameters = @{ ProjectPath = $project; UnityExe = (Join-Path $PSHOME 'pwsh.exe');
        LogFile = $log; LicensingIpc = 'TestChannel'; TimeoutSeconds = 1 }
    foreach ($mode in @('blocked', 'race')) {
        $global:unityLaunchTest.Mode = $mode; $global:unityLaunchTest.Polls = 0; $global:unityLaunchTest.Starts = 0
        try { & $runner @parameters | Out-Null; throw 'Expected Editor stop condition.' }
        catch { Require ($_.Exception.Message -like 'UNITY_EDITOR_ALREADY_RUNNING:*') ("Unexpected blocking error: " + $_.Exception.Message) }
        Require ($global:unityLaunchTest.Starts -eq 0) 'Blocked run attempted to start a process.'
        Require (-not (Test-Path -LiteralPath (Split-Path $log -Parent))) 'Blocked run wrote a log directory.'
        Write-Output ("PASS " + $mode + ": no launch or writes")
    }
    $global:unityLaunchTest.Mode = 'success'; $global:unityLaunchTest.Polls = 0; $global:unityLaunchTest.Starts = 0
    $extra = @('-executeMethod', 'Example.Run', 'text with spaces', 'embedded"quote', 'trailing\', '')
    $result = & $runner @parameters -ExtraArgs $extra
    Require ($global:unityLaunchTest.Starts -eq 1 -and $result.ExitCode -eq 0 -and -not $result.TimedOut) 'Successful run failed.'
    Require ($global:unityLaunchTest.Owned.KillCount -eq 0 -and $global:unityLaunchTest.Owned.Disposed) 'Successful cleanup killed a process.'
    $arguments = [UnityLaunchArgumentTest]::Parse($global:unityLaunchTest.ArgumentLine)
    $projectIndex = [array]::IndexOf($arguments, '-projectPath')
    Require ($arguments[$projectIndex + 1] -eq $project) 'Project path quoting failed.'
    $actualExtra = @($arguments[($arguments.Length - $extra.Length)..($arguments.Length - 1)])
    for ($i = 0; $i -lt $extra.Length; $i++) { Require ($actualExtra[$i] -ceq $extra[$i]) "Argument round-trip failed at $i." }
    Write-Output 'PASS success: spaces, quotes, trailing backslash and empty arguments round-trip'
    $global:unityLaunchTest.Mode = 'timeout'; $global:unityLaunchTest.Polls = 0
    $result = & $runner @parameters
    Require ($result.TimedOut -and $global:unityLaunchTest.Owned.KillCount -eq 1 -and $global:unityLaunchTest.Owned.Disposed) 'Timeout cleanup failed.'
    Write-Output 'PASS timeout: only owned process stopped'
    $global:unityLaunchTest.Mode = 'interrupted'; $global:unityLaunchTest.Polls = 0
    try { & $runner @parameters | Out-Null; throw 'Expected interruption.' }
    catch { Require ($_.Exception.Message -like '*SIMULATED_INTERRUPTION*') 'Unexpected interruption error.' }
    Require ($global:unityLaunchTest.Owned.KillCount -eq 1 -and $global:unityLaunchTest.Owned.Disposed) 'Interrupted cleanup failed.'
    Write-Output 'PASS interruption: only owned process stopped'
} finally {
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $allowedRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedTestRoot.StartsWith($allowedRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolvedTestRoot)).StartsWith('UnityLaunchSkillTest-')) {
        throw 'Refusing cleanup outside the verified test workspace.'
    }
    if (Test-Path -LiteralPath $resolvedTestRoot) { Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force }
    Remove-Variable -Name unityLaunchTest -Scope Global
}
