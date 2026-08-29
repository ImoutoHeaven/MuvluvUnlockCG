# G4 Scene JSON 导出

本教程从当前 Windows 用户可正常运行的合法 G4 离线版导出全部 `scene.json`，再用 Docker 校验并同步到 `MuvluvSceneFrames`。

离线版由用户手动更新。工具不下载更新、不实现 license/包解密、不保存密钥，也不写游戏、G4 或缓存。

## 路径

```text
插件仓库：C:\Users\Eden\Muv-Luv\MuvluvUnlockCG
G4 离线版：C:\Users\Eden\Muv-Luv\MuvLuvGGX-G4-1.0.52-full
BestHTTP：C:\Users\Eden\AppData\LocalLow\KMS\マブラヴ・ガールズガーデンX\com.Tivadar.Best.HTTP.v3\LocalCache
数据仓库：C:\Users\Eden\Muv-Luv\MuvluvSceneFrames
```

导出必须在 Windows host 运行，因为它需要当前用户的 license、G4 进程和 Frida attach。依赖只安装到仓库忽略的 uv venv。验证使用只读 Docker mounts。

## 首次准备

打开 PowerShell 7：

```powershell
Set-Location 'C:\Users\Eden\Muv-Luv\MuvluvUnlockCG'

$Repo = (Get-Location).Path
$Venv = Join-Path $Repo 'artifacts\frida-venv'

uv --version
uv venv $Venv --python 3.12
if ($LASTEXITCODE -ne 0) { throw 'uv venv 创建失败' }

uv pip install `
    --python (Join-Path $Venv 'Scripts\python.exe') `
    'frida==17.17.0'
if ($LASTEXITCODE -ne 0) { throw 'Frida 安装失败' }
```

只需要 `frida==17.17.0`，不需要 `frida-tools`。

## 导出

先正常启动 G4，确认 license 和内容可访问，再关闭全部 G4 进程。确认 Docker 可用：

```powershell
Get-Process -Name 'MuvLuvGGX*' -ErrorAction SilentlyContinue
docker info
if ($LASTEXITCODE -ne 0) { throw 'Docker daemon 不可用' }
```

`Get-Process` 必须没有输出。每次使用一个尚不存在的输出目录：

```powershell
$Repo = 'C:\Users\Eden\Muv-Luv\MuvluvUnlockCG'
$Offline = 'C:\Users\Eden\Muv-Luv\MuvLuvGGX-G4-1.0.52-full'
$Venv = Join-Path $Repo 'artifacts\frida-venv'
$Python = Join-Path $Venv 'Scripts\python.exe'
$Exporter = Join-Path $Repo 'tools\g4_scene_export.py'
$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Export = Join-Path $Repo "artifacts\g4-export-$Stamp"

if (-not (Test-Path -LiteralPath $Python -PathType Leaf)) {
    throw "uv venv 不存在：$Python"
}
if (Test-Path -LiteralPath $Export) {
    throw "输出目录已经存在：$Export"
}

& $Python $Exporter `
    --launcher (Join-Path $Offline 'MuvLuvGGX.exe') `
    --output $Export `
    --timeout 2400
if ($LASTEXITCODE -ne 0) { throw 'G4 Scene 导出失败' }

Write-Host "导出完成：$Export"
```

运行期间不要操作 G4 窗口或启动第二个实例。成功输出：

```text
<Export>/
  scenes.json
  manifest.json
  scene/<SceneId>/scene.json
```

成功标准是 `result=complete scenes=N`，并且目录、manifest 和实际 Scene 集合完全一致；不要把 `N=1032` 写成永久假设。

## Docker 校验

```powershell
$Repo = 'C:\Users\Eden\Muv-Luv\MuvluvUnlockCG'
$Cache = 'C:\Users\Eden\AppData\LocalLow\KMS\マブラヴ・ガールズガーデンX\com.Tivadar.Best.HTTP.v3\LocalCache'

$ReportLines = docker run --rm `
    --mount "type=bind,source=$Repo,target=/repo,readonly" `
    --mount "type=bind,source=$Export,target=/export,readonly" `
    --mount "type=bind,source=$Cache,target=/cache,readonly" `
    python:3.12-slim `
    sh -lc 'pip install --quiet -r /repo/tools/requirements.txt && python /repo/tools/http_cache_inventory.py /cache --g4-export /export'
if ($LASTEXITCODE -ne 0) { throw 'G4 export Docker 校验失败' }

$ReportJson = $ReportLines -join "`n"
$Report = $ReportJson | ConvertFrom-Json
$Validation = $Report.g4_export_validation
if ($Validation.status -ne 'compatible' -or $Validation.mismatch_count -ne 0) {
    throw '导出不满足 compatible + zero mismatch'
}

$ReportPath = "$Export-validation-report.json"
$ReportJson | Set-Content -LiteralPath $ReportPath -Encoding utf8
$Validation | Format-List
```

校验覆盖文件集合、ID、长度、SHA-256、Scene schema、frame 顺序，以及原游戏 BestHTTP 重叠记录。只有 `status=compatible` 且 `mismatch_count=0` 才能继续。

## 同步数据仓库

提升脚本要求 `MuvluvSceneFrames` 工作树干净，先校验新导出，再同步原始布局，最后校验同步结果。它不 commit 或 push。

先预览：

```powershell
$Archive = 'C:\Users\Eden\Muv-Luv\MuvluvSceneFrames'
$Promoter = Join-Path $Repo 'tools\promote_g4_scene_export.ps1'

pwsh -NoProfile -File $Promoter `
    -ExportRoot $Export `
    -DataRepo $Archive `
    -CacheRoot $Cache `
    -PluginRepo $Repo `
    -Preview
if ($LASTEXITCODE -ne 0) { throw '同步预览失败' }
```

确认 add/update/remove 计划后执行：

```powershell
pwsh -NoProfile -File $Promoter `
    -ExportRoot $Export `
    -DataRepo $Archive `
    -CacheRoot $Cache `
    -PluginRepo $Repo
if ($LASTEXITCODE -ne 0) { throw 'SceneFrame 同步失败' }

git -C $Archive status --short
git -C $Archive diff --check
```

审阅后由维护者自行 commit 和 push。

## 提供给插件

本地 fallback：

```text
BepInEx/plugins/MuvluvUnlockCG.SceneFrames/
  manifest.json
  scene/<SceneId>/scene.json
```

远端 static root 使用同一份原始导出，不需要转换：

```ini
[SceneFrames]
RemoteBaseUrl = https://raw.githubusercontent.com/ImoutoHeaven/MuvluvSceneFrame/main/
```

新生成的配置默认使用上面的 GitHub raw 根地址；已有配置值不会被覆盖。也可改成自己的固定 ref：

```text
https://raw.githubusercontent.com/<owner>/<repo>/<ref>/
```

插件只请求 `manifest.json` 和 canonical Scene 路径。清空配置即 local-only。

## 常见错误

| 错误 | 处理 |
| --- | --- |
| `output already exists` | 换一个新输出目录 |
| `close existing MuvLuvGGX processes first` | 关闭全部 Launcher/Broker/Host |
| `capture timed out` | 检查 G4 是否启动、当前用户和进程完整性级别 |
| `main.js patch anchor count was 0` | 离线版前端已变化；按新资源更新唯一 anchor |
| `missing scene JSON` | 本次导出不完整；换新目录重跑或调查固定失败 ID |
| Docker `status=failed` | 不得同步、发布或安装 |
