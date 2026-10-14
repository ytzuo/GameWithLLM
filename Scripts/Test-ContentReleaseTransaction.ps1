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
        $npcDirectory = Join-Path $repository 'NpcContent/npc/Merchant'
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
    if ($NpcPayload) {
        [ordered]@{
            schemaVersion = 1
            releaseId = $ReleaseId
            candidateManifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash.ToLowerInvariant()
            successMarker = 'NPC_RUNTIME_SMOKE_SUCCESS'
            scenarios = @('dynamic-download','animation-driver','save-restore','resource-integrity',
                'cache-repair','catalog-address-missing','scene-cancel','manifest-reregister') |
                ForEach-Object { [ordered]@{ name = $_; passed = $true } }
        } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath `
            (Join-Path $candidate 'npc-runtime-smoke.passed.json') -Encoding utf8
    }
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
    $npcV1 = New-Candidate 'npc-test-v1' 'base' 'current-npc'
    & $promote -CandidateDirectory $npcV1.Candidate -PublishRoot $publish -Confirm:$false | Out-Null
    if ((Get-Content -Raw (Join-Path $publish 'npc/index.json')) -ne 'npc-test-v1') { throw 'NPC index was not switched.' }
    $missingNpcEvidence = New-Candidate 'npc-test-missing-evidence' 'base' 'current-npc'
    Remove-Item -LiteralPath (Join-Path $missingNpcEvidence.Candidate 'npc-runtime-smoke.passed.json')
    $rejected = $false
    try { & $promote -CandidateDirectory $missingNpcEvidence.Candidate -PublishRoot $publish -Confirm:$false | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'NPC content was promoted without runtime smoke evidence.' }
    $legacyNpcEvidence = New-Candidate 'npc-test-legacy-evidence' 'base' 'current-npc'
    $legacyEvidencePath = Join-Path $legacyNpcEvidence.Candidate 'npc-runtime-smoke.passed.json'
    $legacyEvidence = (Get-Content -Raw -LiteralPath $legacyEvidencePath).Replace('resource-integrity','version-conflict').Replace('catalog-address-missing','catalog-restart-required')
    Set-Content -LiteralPath $legacyEvidencePath -Value $legacyEvidence -Encoding utf8
    $rejected = $false
    try { & $promote -CandidateDirectory $legacyNpcEvidence.Candidate -PublishRoot $publish -Confirm:$false | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Old version-based NPC smoke evidence was accepted.' }
    $npcV2 = New-Candidate 'npc-test-v2' 'base' 'current-npc'
    & $promote -CandidateDirectory $npcV2.Candidate -PublishRoot $publish -Confirm:$false | Out-Null
    $npcUpdated = New-Candidate 'npc-test-update' 'base' 'updated-current-npc'
    $beforeWhatIf = (Get-FileHash -LiteralPath (Join-Path $publish 'npc/Merchant/npc.json')).Hash
    & $promote -CandidateDirectory $npcUpdated.Candidate -PublishRoot $publish -WhatIf | Out-Null
    if ((Test-Path -LiteralPath (Join-Path $publish 'releases/npc-test-update')) -or
        (Get-FileHash -LiteralPath (Join-Path $publish 'npc/Merchant/npc.json')).Hash -ne $beforeWhatIf) {
        throw 'WhatIf mutated published content.'
    }
    # Fail after NPC payload and current.json changed, at the final index copy.
    $faultControl = @{ FailIndexCopy = $true }
    function Copy-Item {
        param($LiteralPath, $Destination, [switch]$Recurse, [switch]$Force)
        if ($faultControl.FailIndexCopy -and $Destination.EndsWith('next-index.json')) {
            $faultControl.FailIndexCopy = $false
            throw 'Injected final index copy failure.'
        }
        Microsoft.PowerShell.Management\Copy-Item @PSBoundParameters
    }
    $rejected = $false
    try { & $promote -CandidateDirectory $npcUpdated.Candidate -PublishRoot $publish -Confirm:$false | Out-Null }
    catch { $rejected = $true }
    finally { Remove-Item Function:\Copy-Item }
    if (-not $rejected -or
        (Get-Content -Raw (Join-Path $publish 'npc/index.json')) -ne 'npc-test-v2' -or
        (Get-Content -Raw (Join-Path $publish 'npc/Merchant/npc.json')) -ne 'current-npc' -or
        (Get-Content -Raw (Join-Path $publish 'current.json') | ConvertFrom-Json).releaseId -ne 'npc-test-v2') {
        throw 'Failed NPC promotion did not restore payload, index and pointer.'
    }
    $obsolete = Join-Path $publish 'npc/DeletedNpc'
    New-Item -ItemType Directory -Path $obsolete | Out-Null
    Set-Content -LiteralPath (Join-Path $obsolete 'npc.json') -Value 'obsolete'
    & $promote -CandidateDirectory $npcUpdated.Candidate -PublishRoot $publish -Confirm:$false | Out-Null
    if (Test-Path -LiteralPath $obsolete) { throw 'Deleted NPC remained in the current generated tree.' }
    if ((Get-Content -Raw (Join-Path $publish 'npc/Merchant/npc.json')) -ne 'updated-current-npc' -or
        (Get-Content -Raw (Join-Path $publish 'npc/index.json')) -ne 'npc-test-update') {
        throw 'Current NPC update did not publish payload before switching index.'
    }
    # Simulate process termination after payload/pointer changes with a durable journal.
    $unfinished = Join-Path $publish '.transaction-interrupted'
    New-Item -ItemType Directory -Path $unfinished | Out-Null
    Copy-Item -LiteralPath (Join-Path $publish 'npc') -Destination (Join-Path $unfinished 'npc') -Recurse
    Copy-Item -LiteralPath (Join-Path $publish 'current.json') -Destination (Join-Path $unfinished 'current.json')
    @{ hadNpc = $true; hadCurrent = $true; touchesNpc = $true; committed = $false } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $unfinished 'journal.json') -Encoding utf8
    Set-Content -LiteralPath (Join-Path $publish 'npc/index.json') -Value 'interrupted'
    @{ releaseId = 'interrupted' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $publish 'current.json')
    & $promote -CandidateDirectory (Join-Path $publish 'releases/npc-test-v1') -PublishRoot $publish -Rollback -Confirm:$false | Out-Null
    if ((Get-Content -Raw (Join-Path $publish 'npc/index.json')) -ne 'npc-test-v1') { throw 'NPC index rollback failed.' }
    if ((Get-Content -Raw (Join-Path $publish 'current.json') | ConvertFrom-Json).previousReleaseId -ne 'npc-test-update' -or
        (Test-Path -LiteralPath $unfinished)) { throw 'Interrupted promotion journal was not recovered.' }
    Write-Output 'Content release transaction tests passed: publish/retry, tamper and old evidence rejection, WhatIf, failed NPC promotion rollback, deleted NPC cleanup, interrupted journal recovery and release rollback.'
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
