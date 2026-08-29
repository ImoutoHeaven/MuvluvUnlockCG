[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [Alias('FreshExport', 'Export')]
    [string] $ExportRoot,

    [Parameter(Mandatory)]
    [string] $DataRepo,

    [Parameter(Mandatory)]
    [Alias('Cache')]
    [string] $CacheRoot,

    [string] $PluginRepo = (Split-Path -Parent $PSScriptRoot),

    [string] $ValidatorImage = 'python:3.12-slim',

    [switch] $Preview
)

$ErrorActionPreference = 'Stop'

function Resolve-Directory([string] $Path, [string] $Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Label directory not found: $Path"
    }

    $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    return [IO.Path]::GetFullPath($resolved.Path)
}

function Assert-ChildPath([string] $Root, [string] $Candidate, [string] $Label) {
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $candidateFull = [IO.Path]::GetFullPath($Candidate)
    $prefix = $rootFull + [IO.Path]::DirectorySeparatorChar
    if (-not $candidateFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label is outside its intended repository: $candidateFull"
    }
}

function Invoke-GitReadOnly([string] $Repository, [string[]] $GitArguments) {
    $args = @(
        'run', '--rm',
        '--mount', "type=bind,source=$Repository,target=/data,readonly",
        'mcr.microsoft.com/dotnet/sdk:8.0',
        'git', '-C', '/data'
    ) + $GitArguments
    $output = & docker @args
    if ($LASTEXITCODE -ne 0) {
        throw "git check failed for $Repository"
    }

    return @($output)
}

function Invoke-G4Validation([string] $SourceRoot, [string] $Label) {
    $args = @(
        'run', '--rm',
        '--mount', "type=bind,source=$PluginRepo,target=/repo,readonly",
        '--mount', "type=bind,source=$SourceRoot,target=/export,readonly",
        '--mount', "type=bind,source=$CacheRoot,target=/cache,readonly",
        $ValidatorImage,
        'sh', '-lc',
        'pip install --quiet -r /repo/tools/requirements.txt && python /repo/tools/http_cache_inventory.py /cache --g4-export /export'
    )
    $lines = & docker @args
    if ($LASTEXITCODE -ne 0) {
        throw "$Label Docker validation failed"
    }

    try {
        $report = ($lines -join [Environment]::NewLine) | ConvertFrom-Json
    }
    catch {
        throw "$Label Docker validator did not return JSON: $($_.Exception.Message)"
    }

    $validation = $report.g4_export_validation
    if ($null -eq $validation -or $validation.status -ne 'compatible' -or $validation.mismatch_count -ne 0) {
        $status = if ($null -eq $validation) { 'missing' } else { $validation.status }
        throw "$Label Docker validation is not compatible: status=$status"
    }

    Write-Output "$Label validation=compatible scenes=$($validation.scene_count) bytes=$($validation.scene_bytes) frames=$($validation.frame_count) overlap=$($validation.original_overlap_record_count) mismatch=$($validation.mismatch_count)"
    return $report
}

function Get-FileMap([string] $Root) {
    $resolved = [IO.Path]::GetFullPath($Root)
    $map = @{}
    foreach ($file in Get-ChildItem -LiteralPath $resolved -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($resolved, $file.FullName).Replace('\', '/')
        $map[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }

    return $map
}

function Get-SyncPlan([string] $SourceRoot, [string] $TargetRoot) {
    $sourceAll = Get-FileMap $SourceRoot
    $targetAll = Get-FileMap $TargetRoot
    $sourceMap = @{}
    $targetMap = @{}
    foreach ($relative in $sourceAll.Keys) {
        if ($relative -eq 'manifest.json' -or $relative -eq 'scenes.json' -or $relative.StartsWith('scene/', [StringComparison]::Ordinal)) {
            $sourceMap[$relative] = $sourceAll[$relative]
        }
    }
    foreach ($relative in $targetAll.Keys) {
        if ($relative -eq 'manifest.json' -or $relative -eq 'scenes.json' -or $relative.StartsWith('scene/', [StringComparison]::Ordinal)) {
            $targetMap[$relative] = $targetAll[$relative]
        }
    }
    $add = @()
    $update = @()
    $remove = @()

    foreach ($relative in ($sourceMap.Keys | Sort-Object)) {
        if (-not $targetMap.ContainsKey($relative)) {
            $add += $relative
        }
        elseif ($sourceMap[$relative] -ne $targetMap[$relative]) {
            $update += $relative
        }
    }

    foreach ($relative in ($targetMap.Keys | Sort-Object)) {
        if (-not $sourceMap.ContainsKey($relative)) {
            $remove += $relative
        }
    }

    return [pscustomobject]@{ Add = $add; Update = $update; Remove = $remove }
}

function Write-ValidationLedger([string] $TargetRoot, $ValidationReport, $Manifest) {
    $manifestPath = Join-Path $TargetRoot 'manifest.json'
    $ledgerRoot = Join-Path $TargetRoot 'validation'
    $ledgerPath = Join-Path $ledgerRoot 'full-export-validation.json'
    New-Item -ItemType Directory -Path $ledgerRoot -Force | Out-Null

    $existingTargets = [ordered]@{}
    if (Test-Path -LiteralPath $ledgerPath -PathType Leaf) {
        try {
            $existing = Get-Content -LiteralPath $ledgerPath -Raw | ConvertFrom-Json
            if ($existing.targetScenes) {
                foreach ($property in $existing.targetScenes.psobject.Properties) {
                    $existingTargets[$property.Name] = [string] $property.Value
                }
            }
        }
        catch {
            $existingTargets = [ordered]@{}
        }
    }

    $manifestRows = @{}
    foreach ($row in $Manifest.scenes) {
        $manifestRows[[string] $row.id] = [string] $row.sha256
    }
    $targetScenes = [ordered]@{}
    foreach ($sceneId in $existingTargets.Keys) {
        if ($manifestRows.ContainsKey($sceneId)) {
            $targetScenes[$sceneId] = $manifestRows[$sceneId]
        }
    }

    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $ledger = [ordered]@{
        format = 'muvluv-g4-export-validation-v1'
        observedAt = (Get-Date -Format 'yyyy-MM-dd')
        exportManifestSha256 = $manifestHash
        catalogSha256 = [string] $Manifest.catalogSha256
        sceneCount = [int] $ValidationReport.g4_export_validation.scene_count
        sceneBytes = [int64] $ValidationReport.g4_export_validation.scene_bytes
        muvluvFrameCount = [int] $ValidationReport.g4_export_validation.frame_count
        originalOverlapRecordCount = [int] $ValidationReport.g4_export_validation.original_overlap_record_count
        originalOverlapUniqueSceneCount = [int] $ValidationReport.g4_export_validation.original_overlap_unique_scene_count
        mismatchCount = [int] $ValidationReport.g4_export_validation.mismatch_count
        targetScenes = $targetScenes
    }
    $ledger | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ledgerPath -Encoding utf8
}

$export = Resolve-Directory $ExportRoot 'fresh export'
$data = Resolve-Directory $DataRepo 'SceneFrame data'
$cache = Resolve-Directory $CacheRoot 'original cache'
$plugin = Resolve-Directory $PluginRepo 'plugin'

$exportInsideData = $export.StartsWith($data + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
$dataInsideExport = $data.StartsWith($export + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
if ([StringComparer]::OrdinalIgnoreCase.Equals($export, $data) -or $exportInsideData -or $dataInsideExport) {
    throw 'fresh export and data repository must be separate paths'
}

foreach ($required in @('manifest.json', 'scenes.json', 'scene')) {
    if (-not (Test-Path -LiteralPath (Join-Path $export $required))) {
        throw "fresh export is missing $required"
    }
}

$status = Invoke-GitReadOnly $data @('status', '--porcelain')
if ($status.Count -ne 0) {
    throw 'SceneFrame data repository must be clean before promotion'
}

$freshReport = Invoke-G4Validation $export 'fresh-export'
$freshManifest = Get-Content -LiteralPath (Join-Path $export 'manifest.json') -Raw | ConvertFrom-Json
$plan = Get-SyncPlan $export $data
Write-Output "sync-plan add=$($plan.Add.Count) update=$($plan.Update.Count) remove=$($plan.Remove.Count)"
if ($plan.Add.Count -gt 0) { Write-Output ('add: ' + ($plan.Add -join ', ')) }
if ($plan.Update.Count -gt 0) { Write-Output ('update: ' + ($plan.Update -join ', ')) }
if ($plan.Remove.Count -gt 0) { Write-Output ('remove: ' + ($plan.Remove -join ', ')) }

if ($Preview) {
    Write-Output 'promotion=preview; no data repository files changed'
    exit 0
}

if ($PSCmdlet.ShouldProcess($data, 'synchronize validated G4 export')) {
    foreach ($relative in @('manifest.json', 'scenes.json')) {
        Copy-Item -LiteralPath (Join-Path $export $relative) -Destination (Join-Path $data $relative) -Force
    }

    $sourceSceneRoot = Join-Path $export 'scene'
    $targetSceneRoot = Join-Path $data 'scene'
    New-Item -ItemType Directory -Path $targetSceneRoot -Force | Out-Null
    foreach ($sourceFile in Get-ChildItem -LiteralPath $sourceSceneRoot -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($sourceSceneRoot, $sourceFile.FullName)
        $destination = Join-Path $targetSceneRoot $relative
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Copy-Item -LiteralPath $sourceFile.FullName -Destination $destination -Force
    }

    foreach ($relative in $plan.Remove) {
        $target = Join-Path $data $relative
        if (Test-Path -LiteralPath $target) {
            Assert-ChildPath $data $target 'stale synchronized path'
            Remove-Item -LiteralPath $target -Force
        }
    }

    Get-ChildItem -LiteralPath $targetSceneRoot -Directory -Recurse |
        Sort-Object FullName -Descending |
        Where-Object { @(Get-ChildItem -LiteralPath $_.FullName -Force).Count -eq 0 } |
        Remove-Item -Force

    $syncedReport = Invoke-G4Validation $data 'synced-data'
    $syncedManifest = Get-Content -LiteralPath (Join-Path $data 'manifest.json') -Raw | ConvertFrom-Json
    Write-ValidationLedger $data $syncedReport $syncedManifest

    $check = Invoke-GitReadOnly $data @('diff', '--check')
    $stat = Invoke-GitReadOnly $data @('diff', '--stat')
    if ($stat.Count -gt 0) { $stat | Write-Output }
    Write-Output 'promotion=complete; commit and push remain the maintainer action'
}
