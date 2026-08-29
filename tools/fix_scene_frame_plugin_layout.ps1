[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [string] $GameDir
)

$ErrorActionPreference = 'Stop'
$pluginsRoot = Join-Path $GameDir 'BepInEx/plugins'
$directRoot = Join-Path $pluginsRoot 'MuvluvUnlockCG.SceneFrames'
$nestedRoot = Join-Path $pluginsRoot 'MuvluvUnlock/MuvluvUnlockCG.SceneFrames'

if (-not (Test-Path -LiteralPath $pluginsRoot -PathType Container)) {
    throw "BepInEx plugins directory not found: $pluginsRoot"
}

if (-not (Test-Path -LiteralPath $nestedRoot -PathType Container)) {
    Write-Output "layout=ok nested=false direct=$([bool](Test-Path -LiteralPath $directRoot -PathType Container))"
    exit 0
}

if (-not (Test-Path -LiteralPath $directRoot -PathType Container)) {
    if ($PSCmdlet.ShouldProcess($nestedRoot, "Move SceneFrames to $directRoot")) {
        Move-Item -LiteralPath $nestedRoot -Destination $directRoot
    }

    Write-Output 'layout=fixed action=move'
    exit 0
}

function Get-FileHashes([string] $Root) {
    $resolvedRoot = [IO.Path]::GetFullPath($Root)
    $hashes = @{}
    foreach ($file in Get-ChildItem -LiteralPath $resolvedRoot -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($resolvedRoot, $file.FullName).Replace('\', '/')
        $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }

    return $hashes
}

$directHashes = Get-FileHashes $directRoot
$nestedHashes = Get-FileHashes $nestedRoot
if ($directHashes.Count -ne $nestedHashes.Count) {
    throw "SceneFrames copies differ: direct=$($directHashes.Count) files, nested=$($nestedHashes.Count) files"
}

foreach ($relative in $directHashes.Keys) {
    if (-not $nestedHashes.ContainsKey($relative) -or $nestedHashes[$relative] -ne $directHashes[$relative]) {
        throw "SceneFrames copies differ at: $relative"
    }
}

if ($PSCmdlet.ShouldProcess($nestedRoot, 'Remove byte-identical nested SceneFrames copy')) {
    Remove-Item -LiteralPath $nestedRoot -Recurse -Force
}

Write-Output "layout=fixed action=remove-identical-duplicate files=$($directHashes.Count)"
