[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $true)]
    [string]$CandidateDirectory,
    [Parameter(Mandatory = $true)]
    [string]$PublishRoot,
    [switch]$Rollback
)

$ErrorActionPreference = 'Stop'
function Resolve-ContainedPath {
    param([string]$Root, [string]$RelativePath)
    if ([IO.Path]::IsPathRooted($RelativePath)) { throw "Artifact path must be relative: '$RelativePath'." }
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath((Join-Path $rootPath $RelativePath))
    if (-not $resolved.StartsWith($rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Artifact path escapes its release root: '$RelativePath'."
    }
    return $resolved
}
function Write-PublishJournal {
    param($Journal, [string]$Path)
    $pending = $Path + '.tmp'
    $Journal | ConvertTo-Json | Set-Content -LiteralPath $pending -Encoding utf8
    Move-Item -LiteralPath $pending -Destination $Path -Force
}

$candidate = (Resolve-Path -LiteralPath $CandidateDirectory).Path
$manifestPath = Join-Path $candidate 'candidate-manifest.json'
$evidencePath = Join-Path $candidate 'content-release-smoke.passed.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) {
    throw 'Content candidate manifest and environment smoke evidence are both required.'
}
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$evidence = Get-Content -Raw -LiteralPath $evidencePath | ConvertFrom-Json
$manifestHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash.ToLowerInvariant()
$toolEvidencePath = Join-Path $candidate $manifest.toolPackageSmokeEvidence
if (-not (Test-Path -LiteralPath $toolEvidencePath -PathType Leaf) -or
    (Get-FileHash -Algorithm SHA256 -LiteralPath $toolEvidencePath).Hash.ToLowerInvariant() -ne
        $manifest.toolPackageSmokeEvidenceSha256) {
    throw 'Tool package smoke evidence is missing or changed after candidate creation.'
}
$requiredScenarios = @('offline', 'cache-hit', 'low-disk', 'interrupted-retry')
if ($manifest.releaseKind -eq 'full') {
    $requiredScenarios += 'fresh-install'
} elseif ($manifest.releaseKind -eq 'content-update') {
    $requiredScenarios += 'existing-install-upgrade'
} else {
    throw "Unknown content release kind '$($manifest.releaseKind)'."
}
if ($evidence.schemaVersion -ne 1 -or $evidence.releaseId -ne $manifest.releaseId -or
    $evidence.candidateManifestSha256 -ne $manifestHash -or $evidence.successMarker -ne 'CONTENT_RELEASE_SMOKE_SUCCESS') {
    throw 'Content release smoke evidence is stale or does not describe this successful candidate.'
}
$passed = @($evidence.scenarios | Where-Object { $_.passed } | ForEach-Object { $_.name })
foreach ($scenario in $requiredScenarios) {
    if ($passed -notcontains $scenario) { throw "Content release smoke scenario '$scenario' did not pass." }
}

$npcRuntimeEvidencePath = $null
if ($manifest.npcContentRoot) {
    $npcRuntimeEvidencePath = Join-Path $candidate 'npc-runtime-smoke.passed.json'
    if (-not (Test-Path -LiteralPath $npcRuntimeEvidencePath -PathType Leaf)) {
        throw 'NPC runtime smoke evidence is required for a candidate containing NPC content.'
    }
    $npcRuntimeEvidence = Get-Content -Raw -LiteralPath $npcRuntimeEvidencePath | ConvertFrom-Json
    if ($npcRuntimeEvidence.schemaVersion -ne 1 -or
        $npcRuntimeEvidence.releaseId -ne $manifest.releaseId -or
        $npcRuntimeEvidence.candidateManifestSha256 -ne $manifestHash -or
        $npcRuntimeEvidence.successMarker -ne 'NPC_RUNTIME_SMOKE_SUCCESS') {
        throw 'NPC runtime smoke evidence is stale or does not describe this successful candidate.'
    }
    $requiredNpcScenarios = @('dynamic-download','animation-driver','save-restore',
        'resource-integrity','cache-repair','catalog-address-missing','scene-cancel','manifest-reregister')
    $passedNpcScenarios = @($npcRuntimeEvidence.scenarios | Where-Object { $_.passed } |
        ForEach-Object { $_.name })
    foreach ($scenario in $requiredNpcScenarios) {
        if ($passedNpcScenarios -notcontains $scenario) {
            throw "NPC runtime smoke scenario '$scenario' did not pass."
        }
    }
}

$publish = [IO.Path]::GetFullPath($PublishRoot)
$releases = Join-Path $publish 'releases'
if ($manifest.releaseId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$') { throw 'Invalid releaseId.' }
$releaseDirectory = Resolve-ContainedPath $releases $manifest.releaseId
$repository = (Resolve-Path (Join-Path $candidate '..\..\..')).Path
$sourceRoot = if ($Rollback) { $candidate } else { $repository }
if ($Rollback -and [IO.Path]::GetFullPath($candidate) -ne [IO.Path]::GetFullPath($releaseDirectory)) {
    throw "Rollback candidate must be the retained release directory '$releaseDirectory'."
}
foreach ($file in $manifest.files) {
    $source = Resolve-ContainedPath $sourceRoot $file.path
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Candidate artifact is missing: '$($file.path)'."
    }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash.ToLowerInvariant()
    if ($actual -ne $file.sha256 -or (Get-Item -LiteralPath $source).Length -ne $file.length) {
        throw "Candidate artifact hash or length mismatch: '$($file.path)'."
    }
}

# WhatIf performs all read-only gates but must not create releases or overwrite NPC files.
if ($manifest.npcContentRoot) {
    $indexRelative = $manifest.npcContentRoot.TrimEnd('/') + '/npc/index.json'
    if (@($manifest.files | Where-Object { $_.path.Replace('\', '/') -eq $indexRelative }).Count -ne 1) {
        throw 'NPC index must appear exactly once in the candidate file manifest.'
    }
}
if (-not $PSCmdlet.ShouldProcess($publish, "publish content release '$($manifest.releaseId)'")) { return }
New-Item -ItemType Directory -Force -Path $publish,$releases | Out-Null
$publishLock = [IO.File]::Open((Join-Path $publish '.publish.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
try {
# Recover an interrupted mutable promotion before accepting another one. Journal
# paths are fixed descendants of the publish root; no external paths are read.
foreach ($unfinished in Get-ChildItem -LiteralPath $publish -Directory -Filter '.transaction-*') {
    $journalPath = Resolve-ContainedPath $unfinished.FullName 'journal.json'
    if (Test-Path -LiteralPath $journalPath) {
        $journal = Get-Content -Raw -LiteralPath $journalPath | ConvertFrom-Json
        if (-not $journal.committed) {
            $oldNpc = Resolve-ContainedPath $publish 'npc'
            if ($journal.touchesNpc) {
                if (Test-Path -LiteralPath $oldNpc) { Remove-Item -LiteralPath $oldNpc -Recurse -Force }
                if ($journal.hadNpc) { Copy-Item -LiteralPath (Resolve-ContainedPath $unfinished.FullName 'npc') -Destination $oldNpc -Recurse }
            }
            $oldCurrent = Resolve-ContainedPath $publish 'current.json'
            if ($journal.hadCurrent) {
                Copy-Item -LiteralPath (Resolve-ContainedPath $unfinished.FullName 'current.json') -Destination $oldCurrent -Force
            } elseif (Test-Path -LiteralPath $oldCurrent) { Remove-Item -LiteralPath $oldCurrent }
        }
    }
    $cleanup = Resolve-ContainedPath $publish $unfinished.Name
    Remove-Item -LiteralPath $cleanup -Recurse -Force
}
$releaseAlreadyPresent = -not $Rollback -and (Test-Path -LiteralPath $releaseDirectory)
if ($releaseAlreadyPresent) {
    foreach ($file in $manifest.files) {
        $published = Resolve-ContainedPath $releaseDirectory $file.path
        if (-not (Test-Path -LiteralPath $published -PathType Leaf) -or
            (Get-Item -LiteralPath $published).Length -ne $file.length -or
            (Get-FileHash -Algorithm SHA256 -LiteralPath $published).Hash.ToLowerInvariant() -ne $file.sha256) {
            throw "Existing immutable release '$($manifest.releaseId)' differs at '$($file.path)'."
        }
    }
    $publishedManifest = Join-Path $releaseDirectory 'candidate-manifest.json'
    if (-not (Test-Path -LiteralPath $publishedManifest -PathType Leaf) -or
        (Get-FileHash -Algorithm SHA256 -LiteralPath $publishedManifest).Hash.ToLowerInvariant() -ne $manifestHash) {
        throw "Existing immutable release '$($manifest.releaseId)' has a different candidate manifest."
    }
}
if (-not $Rollback -and -not $releaseAlreadyPresent) {
    $staging = Join-Path $releases (".{0}.{1}.staging" -f $manifest.releaseId, [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    try {
        # Immutable payload is copied first. current.json is never touched until all hashes re-verify.
        foreach ($file in $manifest.files) {
            $source = Resolve-ContainedPath $repository $file.path
            $destination = Resolve-ContainedPath $staging $file.path
            New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination
            if ((Get-Item -LiteralPath $destination).Length -ne $file.length -or
                (Get-FileHash -Algorithm SHA256 -LiteralPath $destination).Hash.ToLowerInvariant() -ne $file.sha256) {
                throw "Copied candidate artifact changed: '$($file.path)'."
            }
        }
        Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $staging 'candidate-manifest.json')
        Copy-Item -LiteralPath $evidencePath -Destination (Join-Path $staging 'content-release-smoke.passed.json')
        Copy-Item -LiteralPath $toolEvidencePath -Destination `
            (Join-Path $staging $manifest.toolPackageSmokeEvidence)
        if ($npcRuntimeEvidencePath) {
            Copy-Item -LiteralPath $npcRuntimeEvidencePath -Destination `
                (Join-Path $staging 'npc-runtime-smoke.passed.json')
        }
        Move-Item -LiteralPath $staging -Destination $releaseDirectory
    }
    catch {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
        throw
    }
}

New-Item -ItemType Directory -Force -Path $publish | Out-Null
$currentPath = Join-Path $publish 'current.json'
$previous = $null
if (Test-Path -LiteralPath $currentPath -PathType Leaf) {
    $previous = (Get-Content -Raw -LiteralPath $currentPath | ConvertFrom-Json).releaseId
}
if ($Rollback -and -not $previous) { throw 'Rollback requires an existing current release.' }
$transaction = Resolve-ContainedPath $publish ('.transaction-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $transaction | Out-Null
$npcDestination = Resolve-ContainedPath $publish 'npc'
$hadNpc = Test-Path -LiteralPath $npcDestination
$hadCurrent = Test-Path -LiteralPath $currentPath
if ($hadCurrent) { Copy-Item -LiteralPath $currentPath -Destination (Join-Path $transaction 'current.json') }
if ($manifest.npcContentRoot -and $hadNpc) {
    Copy-Item -LiteralPath $npcDestination -Destination (Join-Path $transaction 'npc') -Recurse
}
$journal = [ordered]@{ hadNpc = $hadNpc; hadCurrent = $hadCurrent; touchesNpc = [bool]$manifest.npcContentRoot; committed = $false }
$journalPath = Join-Path $transaction 'journal.json'
Write-PublishJournal $journal $journalPath
$completed = $false
try {
# Publish current NPC payload before switching its index. Overall releases remain immutable.
$npcIndexSource = $null
if ($manifest.npcContentRoot) {
    $npcRoot = Resolve-ContainedPath $releaseDirectory $manifest.npcContentRoot
    $npcIndexSource = Resolve-ContainedPath $npcRoot 'npc/index.json'
    $npcIndexFiles = @($manifest.files | Where-Object {
        $_.path.Replace('\', '/') -eq ($manifest.npcContentRoot + '/npc/index.json')
    })
    if ($npcIndexFiles.Count -ne 1) { throw 'NPC index must appear exactly once in the candidate file manifest.' }
    $npcFiles = @($manifest.files | Where-Object {
        $_.path.Replace('\', '/').StartsWith($manifest.npcContentRoot + '/npc/') -and
        $_.path.Replace('\', '/') -ne ($manifest.npcContentRoot + '/npc/index.json')
    })
    foreach ($file in $npcFiles) {
        $relative = $file.path.Replace('\', '/').Substring($manifest.npcContentRoot.Length + 1)
        $destination = Resolve-ContainedPath $publish $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
        $npcPayloadTemporary = $destination + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        Copy-Item -LiteralPath (Resolve-ContainedPath $releaseDirectory $file.path) -Destination $npcPayloadTemporary
        Move-Item -LiteralPath $npcPayloadTemporary -Destination $destination -Force
    }
}

$pointer = [ordered]@{
    schemaVersion = 1
    releaseId = $manifest.releaseId
    releaseKind = $manifest.releaseKind
    candidateManifestSha256 = $manifestHash
    previousReleaseId = $previous
    promotedAtUtc = [DateTime]::UtcNow.ToString('O')
    rollback = [bool]$Rollback
}
$temporary = Join-Path $transaction 'next-current.json'
    $pointer | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $currentPath -Force
    # The mutable list switches last, after all payload and the release pointer.
    if ($npcIndexSource) {
        $npcIndexPath = Resolve-ContainedPath $publish 'npc/index.json'
        $npcTemporary = Join-Path $transaction 'next-index.json'
        New-Item -ItemType Directory -Force -Path (Split-Path $npcIndexPath -Parent) | Out-Null
        Copy-Item -LiteralPath $npcIndexSource -Destination $npcTemporary
        Move-Item -LiteralPath $npcTemporary -Destination $npcIndexPath -Force
        $expected = @($npcFiles | ForEach-Object {
            Resolve-ContainedPath $publish ($_.path.Replace('\', '/').Substring($manifest.npcContentRoot.Length + 1))
        }) + @($npcIndexPath)
        foreach ($obsolete in Get-ChildItem -LiteralPath $npcDestination -File -Recurse) {
            if ($expected -notcontains $obsolete.FullName) { Remove-Item -LiteralPath $obsolete.FullName -Force }
        }
        foreach ($empty in Get-ChildItem -LiteralPath $npcDestination -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending) {
            if (@(Get-ChildItem -LiteralPath $empty.FullName -Force).Count -eq 0) { Remove-Item -LiteralPath $empty.FullName }
        }
    }
    $journal.committed = $true
    Write-PublishJournal $journal $journalPath
    $completed = $true
}
catch {
    if ($manifest.npcContentRoot) {
        if (Test-Path -LiteralPath $npcDestination) { Remove-Item -LiteralPath $npcDestination -Recurse -Force }
        if ($hadNpc) { Copy-Item -LiteralPath (Join-Path $transaction 'npc') -Destination $npcDestination -Recurse }
    }
    if ($hadCurrent) {
        Copy-Item -LiteralPath (Join-Path $transaction 'current.json') -Destination $currentPath -Force
    } elseif (Test-Path -LiteralPath $currentPath) { Remove-Item -LiteralPath $currentPath }
    $completed = $true
    throw
}
finally {
    # Keep backups/journal if rollback itself failed (for example disk or permission
    # failure), so the next invocation can recover instead of losing the old world.
    if ($completed -and (Test-Path -LiteralPath $transaction)) { Remove-Item -LiteralPath $transaction -Recurse -Force }
}
Write-Output "Content release '$($manifest.releaseId)' published; previous release '$previous' remains retained."
} finally { $publishLock.Dispose() }
