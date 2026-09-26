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

$publish = [IO.Path]::GetFullPath($PublishRoot)
$releases = Join-Path $publish 'releases'
$releaseDirectory = Join-Path $releases $manifest.releaseId
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
        }
        Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $staging 'candidate-manifest.json')
        Copy-Item -LiteralPath $evidencePath -Destination (Join-Path $staging 'content-release-smoke.passed.json')
        Copy-Item -LiteralPath $toolEvidencePath -Destination `
            (Join-Path $staging $manifest.toolPackageSmokeEvidence)
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
$pointer = [ordered]@{
    schemaVersion = 1
    releaseId = $manifest.releaseId
    releaseKind = $manifest.releaseKind
    candidateManifestSha256 = $manifestHash
    previousReleaseId = $previous
    promotedAtUtc = [DateTime]::UtcNow.ToString('O')
    rollback = [bool]$Rollback
}
$temporary = Join-Path $publish ("current.{0}.tmp" -f [Guid]::NewGuid().ToString('N'))
if ($PSCmdlet.ShouldProcess($currentPath, "atomically publish content release '$($manifest.releaseId)'")) {
    $pointer | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $currentPath -Force
}
Write-Output "Content release '$($manifest.releaseId)' published; previous release '$previous' remains retained."
