[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$testParent = Join-Path $workspace '.tmp\content-release-transaction-tests'
$testRoot = Join-Path $testParent ([Guid]::NewGuid().ToString('N'))
$repository = Join-Path $testRoot 'repository'
$publish = Join-Path $testRoot 'publish'
$promote = Join-Path $PSScriptRoot 'Publish-ContentRelease.ps1'

function New-Candidate {
    param([string]$ReleaseId, [string]$PayloadText)
    $candidate = Join-Path $repository ("Artifacts\Content\{0}" -f $ReleaseId)
    $payload = Join-Path $repository ("payload\{0}.bin" -f $ReleaseId)
    New-Item -ItemType Directory -Force -Path $candidate,(Split-Path $payload -Parent) | Out-Null
    Set-Content -LiteralPath $payload -Value $PayloadText -NoNewline -Encoding utf8
    $relative = [IO.Path]::GetRelativePath($repository, $payload).Replace('\', '/')
    $manifest = [ordered]@{
        schemaVersion = 1
        releaseId = $ReleaseId
        releaseKind = 'full'
        toolSetVersion = 'test'
        playerBuildId = 'test'
        toolPackageSmokeEvidence = 'tool-package-smoke.passed.json'
        toolPackageSmokeEvidenceSha256 = ''
        files = @([ordered]@{
            path = $relative
            length = (Get-Item -LiteralPath $payload).Length
            sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $payload).Hash.ToLowerInvariant()
        })
    }
    $toolEvidencePath = Join-Path $candidate 'tool-package-smoke.passed.json'
    Set-Content -LiteralPath $toolEvidencePath -Value 'tool-package-smoke' -NoNewline -Encoding utf8
    $manifest.toolPackageSmokeEvidenceSha256 =
        (Get-FileHash -Algorithm SHA256 -LiteralPath $toolEvidencePath).Hash.ToLowerInvariant()
    $manifestPath = Join-Path $candidate 'candidate-manifest.json'
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8
    $evidence = [ordered]@{
        schemaVersion = 1
        releaseId = $ReleaseId
        candidateManifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash.ToLowerInvariant()
        successMarker = 'CONTENT_RELEASE_SMOKE_SUCCESS'
        scenarios = @('fresh-install','offline','cache-hit','low-disk','interrupted-retry') |
            ForEach-Object { [ordered]@{ name = $_; passed = $true } }
    }
    $evidence | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $candidate 'content-release-smoke.passed.json') -Encoding utf8
    return [pscustomobject]@{ Candidate = $candidate; Payload = $payload }
}

try {
    $v1 = New-Candidate 'script-test-v1' 'version-one'
    & $promote -CandidateDirectory $v1.Candidate -PublishRoot $publish -Confirm:$false | Out-Null
    Remove-Item -LiteralPath (Join-Path $publish 'current.json')
    & $promote -CandidateDirectory $v1.Candidate -PublishRoot $publish -Confirm:$false | Out-Null

    $v2 = New-Candidate 'script-test-v2' 'version-two'
    Set-Content -LiteralPath $v2.Payload -Value 'tampered' -NoNewline -Encoding utf8
    $rejected = $false
    try { & $promote -CandidateDirectory $v2.Candidate -PublishRoot $publish -Confirm:$false | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'A tampered candidate was promoted.' }
    if ((Get-Content -Raw -LiteralPath (Join-Path $publish 'current.json') | ConvertFrom-Json).releaseId -ne
        'script-test-v1') { throw 'A rejected candidate changed current.json.' }

    $v2 = New-Candidate 'script-test-v2' 'version-two'
    & $promote -CandidateDirectory $v2.Candidate -PublishRoot $publish -Confirm:$false | Out-Null
    & $promote -CandidateDirectory (Join-Path $publish 'releases\script-test-v1') `
        -PublishRoot $publish -Rollback -Confirm:$false | Out-Null
    $current = Get-Content -Raw -LiteralPath (Join-Path $publish 'current.json') | ConvertFrom-Json
    if ($current.releaseId -ne 'script-test-v1' -or $current.previousReleaseId -ne 'script-test-v2' -or
        -not $current.rollback) { throw 'Rollback did not atomically point to the retained v1 release.' }
    Write-Output 'Content release transaction tests passed: publish, interrupted retry, tamper rejection, pointer preservation, v2, rollback.'
}
finally {
    $resolvedParent = [IO.Path]::GetFullPath($testParent).TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    $resolvedTarget = [IO.Path]::GetFullPath($testRoot)
    if (-not $resolvedTarget.StartsWith($resolvedParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean unexpected content release test directory '$resolvedTarget'."
    }
    if (Test-Path -LiteralPath $resolvedTarget) {
        Remove-Item -LiteralPath $resolvedTarget -Recurse -Force
    }
}
