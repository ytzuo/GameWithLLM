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
    param([string]$ReleaseId, [string]$PayloadText, [string]$NpcPayload = "")
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
    if ($NpcPayload) {
        $npcDirectory = Join-Path $repository 'NpcContent/npc/merchant_001/1'
        New-Item -ItemType Directory -Force -Path $npcDirectory | Out-Null
        Set-Content -LiteralPath (Join-Path $npcDirectory 'npc.json') -Value $NpcPayload -NoNewline -Encoding utf8
        $indexPath = Join-Path $repository 'NpcContent/npc/index.json'
        Set-Content -LiteralPath $indexPath -Value $ReleaseId -NoNewline -Encoding utf8
        $manifest.npcContentRoot = 'NpcContent'
        foreach ($npcFile in @((Join-Path $npcDirectory 'npc.json'), $indexPath)) {
            $manifest.files += [ordered]@{
                path = [IO.Path]::GetRelativePath($repository, $npcFile).Replace('\', '/')
                length = (Get-Item -LiteralPath $npcFile).Length
                sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $npcFile).Hash.ToLowerInvariant()
            }
        }
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
    $npcV1 = New-Candidate 'npc-test-v1' 'base' 'immutable-npc-v1'
    & $promote -CandidateDirectory $npcV1.Candidate -PublishRoot $publish -Confirm:$false | Out-Null
    if ((Get-Content -Raw (Join-Path $publish 'npc/index.json')) -ne 'npc-test-v1') { throw 'NPC index was not switched.' }
    $npcV2 = New-Candidate 'npc-test-v2' 'base' 'immutable-npc-v1'
    & $promote -CandidateDirectory $npcV2.Candidate -PublishRoot $publish -Confirm:$false | Out-Null
    $npcBad = New-Candidate 'npc-test-conflict' 'base' 'modified-same-version'
    $rejected = $false
    try { & $promote -CandidateDirectory $npcBad.Candidate -PublishRoot $publish -Confirm:$false | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected -or (Get-Content -Raw (Join-Path $publish 'npc/index.json')) -ne 'npc-test-v2') {
        throw 'An immutable NPC conflict changed the live index.'
    }
    if ((Get-Content -Raw (Join-Path $publish 'current.json') | ConvertFrom-Json).releaseId -ne 'npc-test-v2') {
        throw 'An immutable NPC conflict changed the release pointer.'
    }
    & $promote -CandidateDirectory (Join-Path $publish 'releases/npc-test-v1') -PublishRoot $publish -Rollback -Confirm:$false | Out-Null
    if ((Get-Content -Raw (Join-Path $publish 'npc/index.json')) -ne 'npc-test-v1') { throw 'NPC index rollback failed.' }
    Write-Output 'Content release transaction tests passed: publish, interrupted retry, tamper rejection, pointer preservation, v2, rollback, NPC immutable content and index rollback.'
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
