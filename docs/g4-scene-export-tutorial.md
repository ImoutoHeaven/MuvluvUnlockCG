# Export G4 Scene JSON

This procedure uses an authorized G4 Windows runtime to export the complete `scene.json` corpus, validate it in Docker, and synchronize it to `MuvluvSceneFrames`.

The user supplies a licensed G4 distribution. The authorized runtime handles licensing, package decryption, and key management. The exporter copies Scene responses into a repository artifact directory.

## Prerequisites

- Windows and PowerShell 7.
- `uv`.
- A reachable Docker daemon.
- A licensed G4 offline distribution available to the current Windows user.
- Local clones of `MuvluvUnlockCG` and `MuvluvSceneFrames`.
- The original game's BestHTTP `LocalCache`.

Keep the G4 distribution, license, game installation, and cache read-only. The exporter is the sole host-runtime exception and writes to a new directory under `MuvluvUnlockCG/artifacts/`.

## Configure paths

Set these paths in one PowerShell 7 session. Replace each angle-bracketed value:

~~~powershell
Set-Location '<path-to-MuvluvUnlockCG-clone>'

$Repo = (Get-Location).Path
$Offline = (Resolve-Path '<path-to-G4-offline-distribution>').Path
$Cache = (Resolve-Path '<path-to-BestHTTP-LocalCache>').Path
$Archive = (Resolve-Path '<path-to-MuvluvSceneFrames-clone>').Path
$Venv = Join-Path $Repo 'artifacts/frida-venv'
$Python = Join-Path $Venv 'Scripts/python.exe'
$Exporter = Join-Path $Repo 'tools/g4_scene_export.py'
$Promoter = Join-Path $Repo 'tools/promote_g4_scene_export.ps1'
~~~

## Prepare Frida

Install the dependency in the repository-ignored uv venv:

~~~powershell
uv --version
uv venv $Venv --python 3.12
if ($LASTEXITCODE -ne 0) { throw 'uv venv creation failed' }

uv pip install --python $Python 'frida==17.17.0'
if ($LASTEXITCODE -ne 0) { throw 'Frida installation failed' }
~~~

The exporter uses `frida==17.17.0`.

## Export the complete corpus

Launch G4 normally, verify license and content access, then close every G4 process:

~~~powershell
if (Get-Process -Name 'MuvLuvGGX*' -ErrorAction SilentlyContinue) {
    throw 'Close all G4 processes before export'
}

docker info | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'A reachable Docker daemon is required' }
~~~

Create a unique timestamped directory for each export:

~~~powershell
if (-not (Test-Path -LiteralPath $Python -PathType Leaf)) {
    throw "Create the uv venv first: $Python"
}

$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Export = Join-Path $Repo "artifacts/g4-export-$Stamp"
if (Test-Path -LiteralPath $Export) {
    throw "Choose a new export directory: $Export"
}

& $Python $Exporter --launcher (Join-Path $Offline 'MuvLuvGGX.exe') --output $Export --timeout 2400
if ($LASTEXITCODE -ne 0) { throw 'G4 Scene export failed' }
~~~

Keep the G4 window idle during export and run only the instance launched by the exporter. A successful export has this layout:

~~~text
<Export>/
  scenes.json
  manifest.json
  scene/<SceneId>/scene.json
~~~

Success requires `result=complete scenes=N` and identical Scene sets across the directory, manifest, and catalog. The manifest determines the Scene count.

## Validate and synchronize

Start with a clean `MuvluvSceneFrames` worktree and preview the promotion:

~~~powershell
pwsh -NoProfile -NonInteractive -File $Promoter -ExportRoot $Export -DataRepo $Archive -CacheRoot $Cache -PluginRepo $Repo -Preview
if ($LASTEXITCODE -ne 0) { throw 'Synchronization preview failed' }
~~~

The preview validates the fresh export with read-only Docker mounts and reports the add, update, and remove plan. Promotion requires `status=compatible` and `mismatch_count=0`.

Synchronize the reviewed plan:

~~~powershell
pwsh -NoProfile -NonInteractive -File $Promoter -ExportRoot $Export -DataRepo $Archive -CacheRoot $Cache -PluginRepo $Repo
if ($LASTEXITCODE -ne 0) { throw 'SceneFrame synchronization failed' }

git -C $Archive status --short
git -C $Archive diff --check
~~~

The synchronization pass validates the target corpus again. Maintainers review the resulting changes and perform the version-control operations.

## Supply the corpus to the plugin

Local fallback:

~~~text
BepInEx/plugins/MuvluvUnlockCG.SceneFrames/
  manifest.json
  scene/<SceneId>/scene.json
~~~

The remote static root serves the same exported files:

~~~ini
[SceneFrames]
RemoteBaseUrl = https://raw.githubusercontent.com/ImoutoHeaven/MuvluvSceneFrame/main/
~~~

A fixed ref is also supported:

~~~text
https://raw.githubusercontent.com/<owner>/<repo>/<ref>/
~~~

The plugin requests `manifest.json` and canonical Scene paths. Clear `RemoteBaseUrl` to select local-only mode.

## Common errors

| Error | Action |
| --- | --- |
| `output already exists` | Choose a new export directory. |
| `close existing MuvLuvGGX processes first` | Close every Launcher, Broker, and Host process. |
| `capture timed out` | Verify G4 startup, the current user, and process integrity levels. |
| `main.js patch anchor count was 0` | Update the exporter's unique anchor for the current G4 frontend resources. |
| `missing scene JSON` | Keep the target corpus intact and rerun into a new directory or investigate the fixed failing ID. |
| Docker `status=failed` | Stop the workflow and retain the target corpus. |
