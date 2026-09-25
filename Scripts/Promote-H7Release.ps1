[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory = $true)]
    [string]$CandidateDirectory,
    [Parameter(Mandatory = $true)]
    [string]$PointerDirectory
)

$ErrorActionPreference = 'Stop'
$candidate = (Resolve-Path -LiteralPath $CandidateDirectory).Path
$manifestPath = Join-Path $candidate 'candidate-manifest.json'
$evidencePath = Join-Path $candidate 'h7-player-smoke.passed.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf) -or
    -not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) {
    throw 'Candidate manifest or Player smoke evidence is missing.'
}
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$evidence = Get-Content -Raw -LiteralPath $evidencePath | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $evidence.schemaVersion -ne 1 -or
    $manifest.releaseId -ne $evidence.releaseId -or
    $evidence.successMarker -ne 'H7_PLAYER_SMOKE_SUCCESS') {
    throw 'Candidate and smoke evidence do not describe the same successful release.'
}
foreach ($file in $manifest.files) {
    $fullPath = [IO.Path]::GetFullPath((Join-Path (Split-Path $candidate -Parent | Split-Path -Parent | Split-Path -Parent) $file.path))
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
        throw "Candidate artifact is missing: '$($file.path)'."
    }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $fullPath).Hash.ToLowerInvariant()
    if ($actual -ne $file.sha256) {
        throw "Candidate artifact hash mismatch: '$($file.path)'."
    }
}

$pointerRoot = [IO.Path]::GetFullPath($PointerDirectory)
New-Item -ItemType Directory -Force -Path $pointerRoot | Out-Null
$current = Join-Path $pointerRoot 'current.json'
$previousReleaseId = $null
if (Test-Path -LiteralPath $current -PathType Leaf) {
    $previousReleaseId = (Get-Content -Raw -LiteralPath $current | ConvertFrom-Json).releaseId
}
$pointer = [ordered]@{
    schemaVersion = 1
    releaseId = $manifest.releaseId
    toolSetVersion = $manifest.toolSetVersion
    playerBuildId = $manifest.playerBuildId
    candidateManifestSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $manifestPath).Hash.ToLowerInvariant()
    previousReleaseId = $previousReleaseId
    promotedAtUtc = [DateTime]::UtcNow.ToString('O')
}
$temporary = Join-Path $pointerRoot ("current.{0}.tmp" -f [Guid]::NewGuid().ToString('N'))
if ($PSCmdlet.ShouldProcess($current, "atomically promote H7 release '$($manifest.releaseId)'")) {
    $pointer | ConvertTo-Json | Set-Content -LiteralPath $temporary -Encoding utf8
    Move-Item -LiteralPath $temporary -Destination $current -Force
}
