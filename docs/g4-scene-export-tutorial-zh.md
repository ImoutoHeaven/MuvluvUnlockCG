# G4 Scene JSON 导出

此流程通过已授权的 G4 Windows runtime 导出完整 `scene.json` corpus，再用 Docker 校验并同步到 `MuvluvSceneFrames`。

使用者提供合法 G4 distribution；授权 runtime 负责 license、包解密和密钥管理。导出器仅复制 Scene responses，并把输出写入仓库 artifact 目录。

## 前提

- Windows 与 PowerShell 7。
- `uv`。
- 可用的 Docker daemon。
- 当前 Windows 用户可运行的合法 G4 offline distribution。
- `MuvluvUnlockCG` 与 `MuvluvSceneFrames` 的本地 clone。
- 原游戏 BestHTTP `LocalCache`。

G4 distribution、license、游戏安装和缓存必须保持只读。导出器是唯一的 host-runtime 例外，只写入 `MuvluvUnlockCG/artifacts/` 下的新目录。

## 路径配置

在同一个 PowerShell 7 会话中设置路径。尖括号内容由使用者替换：

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

## Frida 环境

依赖只安装到仓库忽略的 uv venv：

~~~powershell
uv --version
uv venv $Venv --python 3.12
if ($LASTEXITCODE -ne 0) { throw 'uv venv 创建失败' }

uv pip install --python $Python 'frida==17.17.0'
if ($LASTEXITCODE -ne 0) { throw 'Frida 安装失败' }
~~~

导出器依赖 `frida==17.17.0`。

## 全量导出

先正常启动 G4，确认 license 和内容可访问，再关闭全部 G4 进程：

~~~powershell
if (Get-Process -Name 'MuvLuvGGX*' -ErrorAction SilentlyContinue) {
    throw '请先关闭全部 G4 进程'
}

docker info | Out-Null
if ($LASTEXITCODE -ne 0) { throw '需要可用的 Docker daemon' }
~~~

每次导出创建带时间戳的唯一目录：

~~~powershell
if (-not (Test-Path -LiteralPath $Python -PathType Leaf)) {
    throw "请先创建 uv venv：$Python"
}

$Stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$Export = Join-Path $Repo "artifacts/g4-export-$Stamp"
if (Test-Path -LiteralPath $Export) {
    throw "输出目录已经存在：$Export"
}

& $Python $Exporter --launcher (Join-Path $Offline 'MuvLuvGGX.exe') --output $Export --timeout 2400
if ($LASTEXITCODE -ne 0) { throw 'G4 Scene 导出失败' }
~~~

导出期间保持 G4 窗口空闲，并只运行 exporter 启动的实例。成功输出：

~~~text
<Export>/
  scenes.json
  manifest.json
  scene/<SceneId>/scene.json
~~~

成功标准是 `result=complete scenes=N`，且目录、manifest 和实际 Scene 集合完全一致。Scene 数量由本次 manifest 决定。

## 校验与同步

目标仓库必须是 clean worktree。先运行预览：

~~~powershell
pwsh -NoProfile -NonInteractive -File $Promoter -ExportRoot $Export -DataRepo $Archive -CacheRoot $Cache -PluginRepo $Repo -Preview
if ($LASTEXITCODE -ne 0) { throw '同步预览失败' }
~~~

预览会在只读 Docker mounts 中校验新导出，并输出 add、update、remove 计划。只有 `status=compatible` 且 `mismatch_count=0` 才能继续。

确认计划后同步：

~~~powershell
pwsh -NoProfile -NonInteractive -File $Promoter -ExportRoot $Export -DataRepo $Archive -CacheRoot $Cache -PluginRepo $Repo
if ($LASTEXITCODE -ne 0) { throw 'SceneFrame 同步失败' }

git -C $Archive status --short
git -C $Archive diff --check
~~~

同步器会再次校验目标 corpus。版本控制操作由维护者审阅后执行。

## 提供给插件

本地 fallback：

~~~text
BepInEx/plugins/MuvluvUnlockCG.SceneFrames/
  manifest.json
  scene/<SceneId>/scene.json
~~~

远端 static root 使用同一份原始导出：

~~~ini
[SceneFrames]
RemoteBaseUrl = https://raw.githubusercontent.com/ImoutoHeaven/MuvluvSceneFrame/main/
~~~

远端也可以使用固定 ref：

~~~text
https://raw.githubusercontent.com/<owner>/<repo>/<ref>/
~~~

插件只请求 `manifest.json` 和 canonical Scene 路径。清空 `RemoteBaseUrl` 即启用 local-only 模式。

## 常见错误

| 错误 | 处理 |
| --- | --- |
| `output already exists` | 使用新的输出目录。 |
| `close existing MuvLuvGGX processes first` | 关闭全部 Launcher、Broker 和 Host。 |
| `capture timed out` | 检查 G4 是否启动，以及当前用户和进程完整性级别。 |
| `main.js patch anchor count was 0` | 更新导出器的唯一 anchor 以匹配当前 G4 前端资源。 |
| `missing scene JSON` | 保留现有目标 corpus，使用新目录重跑或调查固定失败 ID。 |
| Docker `status=failed` | 终止流程并保留现有目标 corpus。 |
