#!/usr/bin/env python3
"""Export catalog-referenced Scene JSON through the licensed G4 runtime."""

from __future__ import annotations

import argparse
import hashlib
import json
import queue
import threading
import time
from typing import NamedTuple
from pathlib import Path


MAX_BODY_CHUNK = 65_536
PATCH_OLD = b"""  const scene = await fetchJson(versionedUrl(selected.sceneUrl))
  const translations = new TranslationManager({
    baseUrl: './public/translations',
    version: translationVersion,
    onRetry: (retry) => sceneLoading.update(retry),
  })
  await translations.load(selected.id, selectedLanguage)

  let player = new ScenePlayer(scene, {
    rootUrl: selected.rootUrl,
    translations,
    protagonistName: loadProtagonistName(),
    autoStart: false,
    onLoadProgress: (loadProgress) => sceneLoading.update(loadProgress),
  })
  await player.mount({ loadAssets: hasSceneRequest })"""
PATCH_NEW = b"""  await fetchJson(sceneListUrl+'&g4exportstart=1')
  await fetchJson(bedroomMetadataUrl+'&g4exportmarker=start')
  for(const x of scenes){try{await fetchJson(versionedUrl(x.sceneUrl))}catch(e){console.error(e)}await fetchJson(bedroomMetadataUrl+'&g4exportmarker='+x.id)}
  await fetchJson(sceneListUrl+'&g4exportdone=1')
  throw Error('g4-export-complete')"""


class ResourceChunk(NamedTuple):
    response_id: bytes
    body: bytes
    is_final: bool


def decode_resource_frame(frame: bytes) -> ResourceChunk:
    """Validate one proven Host response envelope and return its resource chunk."""
    if len(frame) < 23 or frame[:2] != b"\x03\x00" or frame[22] != 1:
        raise ValueError("unsupported G4 resource frame")
    if int.from_bytes(frame[18:22], "little") != len(frame) - 22:
        raise ValueError("invalid payload length")
    body = frame[23:]
    if len(body) > MAX_BODY_CHUNK:
        raise ValueError("resource chunk exceeds 65536 bytes")
    return ResourceChunk(frame[2:18], body, len(body) < MAX_BODY_CHUNK)


class ExportResult(NamedTuple):
    catalog: bytes
    scenes: dict[str, bytes]


SCENE_KEYS = {"assets", "commands", "id", "notes", "preloaded", "title"}
FRAME_KEYS = {
    "background",
    "branchId",
    "characters",
    "configuration",
    "needsHideText",
    "order",
    "sceneId",
    "selectedBranchId",
}


def extract_scene_frames(scene_id: str, body: bytes) -> list[dict]:
    """Validate one exported scene and return its lossless muvluvFrame rows."""
    try:
        document = json.loads(body.decode("utf-8-sig"))
    except (UnicodeDecodeError, json.JSONDecodeError) as error:
        raise ValueError(f"scene {scene_id} is not valid UTF-8 JSON") from error
    if not isinstance(document, dict) or set(document) != SCENE_KEYS:
        raise ValueError(f"scene {scene_id} has an incompatible top-level schema")
    if document.get("id") != scene_id:
        raise ValueError(f"scene {scene_id} body Scene ID does not match its catalog row")
    commands = document.get("commands")
    if not isinstance(commands, list):
        raise ValueError(f"scene {scene_id} commands are not an array")

    frames: list[dict] = []
    previous_order: int | None = None
    expected_scene_id = int(scene_id)
    for command in commands:
        if not isinstance(command, dict) or command.get("type") != "muvluvFrame":
            continue
        frame = command.get("frame")
        if not isinstance(frame, dict) or set(frame) != FRAME_KEYS:
            raise ValueError(f"scene {scene_id} has an incompatible muvluvFrame schema")
        order = frame.get("order")
        branch_id = frame.get("branchId")
        selected_branch_id = frame.get("selectedBranchId")
        if (
            type(order) is not int
            or type(frame.get("sceneId")) is not int
            or frame["sceneId"] != expected_scene_id
            or (branch_id is not None and type(branch_id) is not int)
            or (selected_branch_id is not None and type(selected_branch_id) is not int)
            or not isinstance(frame.get("configuration"), dict)
        ):
            raise ValueError(f"scene {scene_id} has incompatible SceneFrame values")
        if previous_order is not None and order <= previous_order:
            raise ValueError(f"scene {scene_id} frame order is not strictly increasing")
        previous_order = order
        frames.append(frame)
    if not frames:
        raise ValueError(f"scene {scene_id} contains no muvluvFrame commands")
    return frames


def compare_original_scene_frames(
    scene_id: str,
    original_rows: list[list],
    exported_frames: list[dict],
) -> None:
    """Require exact equality with original five-field SceneFrameMaster rows."""
    if len(original_rows) != len(exported_frames):
        raise ValueError(
            f"scene {scene_id} frame count differs: {len(original_rows)} != {len(exported_frames)}"
        )
    field_pairs = (
        (0, "order", "Order"),
        (1, "sceneId", "SceneMasterId"),
        (2, "branchId", "SceneBranchMasterId"),
        (3, "selectedBranchId", "SelectedSceneBranchSelectionMasterId"),
    )
    for index, (original, exported) in enumerate(zip(original_rows, exported_frames)):
        if not isinstance(original, list) or len(original) != 5:
            raise ValueError(f"scene {scene_id} original frame {index} has an incompatible shape")
        for original_index, exported_key, label in field_pairs:
            if original[original_index] != exported.get(exported_key):
                raise ValueError(f"scene {scene_id} frame {index} differs at {label}")
        try:
            configuration = json.loads(original[4])
        except (TypeError, json.JSONDecodeError) as error:
            raise ValueError(
                f"scene {scene_id} original frame {index} has invalid ConfigurationJson"
            ) from error
        if configuration != exported.get("configuration"):
            raise ValueError(f"scene {scene_id} frame {index} differs at ConfigurationJson")


class ExportCollector:
    """Collect complete JSON resources between identical catalog markers."""

    def __init__(self) -> None:
        self._buffer = bytearray()
        self._catalog: bytes | None = None
        self._catalog_rows: list[dict] = []
        self._catalog_hits = 0
        self._marker: bytes | None = None
        self._pending_scene: list[bytes] = []
        self._scene_index = 0
        self._scenes: dict[str, bytes] = {}
        self._missing: list[str] = []

    def feed(self, frame: bytes) -> str | None:
        chunk = decode_resource_frame(frame)
        self._buffer.extend(chunk.body)
        if len(self._buffer) > 64 * 1024 * 1024:
            raise ValueError("resource exceeds 64 MiB capture limit")
        body = bytes(self._buffer)
        try:
            value = json.loads(body.decode("utf-8-sig"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            if not chunk.is_final:
                return None
            self._buffer.clear()
            return None
        self._buffer.clear()

        rows = self._catalog_value(value)
        if rows is not None:
            if self._catalog is None:
                self._catalog = body
                self._catalog_rows = rows
            elif body != self._catalog:
                raise ValueError("catalog changed during capture")
            self._catalog_hits += 1
            if self._catalog_hits > 3:
                raise ValueError("unexpected extra catalog marker")
            return ("catalog", "start", "done")[self._catalog_hits - 1]
        elif self._catalog_hits == 2:
            if self._marker is None:
                self._marker = body
                return "marker"
            if body != self._marker:
                self._pending_scene.append(body)
                if len(self._pending_scene) > 1:
                    raise ValueError("one catalog entry produced multiple JSON resources")
                return None
            if self._scene_index >= len(self._catalog_rows):
                raise ValueError("received more per-scene markers than catalog rows")
            scene_id = str(self._catalog_rows[self._scene_index]["id"])
            if self._pending_scene:
                self._scenes[scene_id] = self._pending_scene.pop()
                state = "scene"
            else:
                self._missing.append(scene_id)
                state = f"missing:{scene_id}"
            self._scene_index += 1
            return state
        return None

    def result(self) -> ExportResult:
        if self._buffer:
            raise ValueError("capture ended with incomplete resource chunks")
        if self._catalog is None or self._catalog_hits != 3:
            raise ValueError("capture did not contain catalog start and completion markers")
        if self._marker is None or self._scene_index != len(self._catalog_rows):
            raise ValueError(
                f"received {self._scene_index} markers for {len(self._catalog_rows)} catalog rows"
            )
        if self._pending_scene:
            raise ValueError("capture ended before the final per-scene marker")
        if self._missing:
            raise ValueError("missing scene JSON for catalog ids: " + ", ".join(self._missing))
        return ExportResult(self._catalog, self._scenes)

    @staticmethod
    def _catalog_value(value: object) -> list[dict] | None:
        if not isinstance(value, list) or not value:
            return None
        rows: list[dict] = []
        ids: set[str] = set()
        for item in value:
            if not isinstance(item, dict):
                return None
            scene_id = item.get("id")
            scene_url = item.get("sceneUrl")
            root_url = item.get("rootUrl")
            if not all(isinstance(field, str) and field for field in (scene_id, scene_url, root_url)):
                return None
            if scene_id in ids:
                raise ValueError(f"duplicate catalog scene id: {scene_id}")
            if scene_url.split("?", 1)[0] != f"./public/scene/{scene_id}/scene.json":
                raise ValueError(f"catalog sceneUrl does not match id: {scene_id}")
            if root_url.split("?", 1)[0] != f"./public/scene/{scene_id}/":
                raise ValueError(f"catalog rootUrl does not match id: {scene_id}")
            ids.add(scene_id)
            rows.append(item)
        return rows


def build_hook() -> str:
    if len(PATCH_NEW) > len(PATCH_OLD):
        raise RuntimeError("main.js export patch no longer fits its verified anchor")
    replacement = PATCH_NEW + b" " * (len(PATCH_OLD) - len(PATCH_NEW))
    pattern = " ".join(f"{value:02x}" for value in PATCH_OLD)
    main_marker = " ".join(f"{value:02x}" for value in b"const sceneListUrl")
    return (
        r"""
const patchPattern = __PATCH_PATTERN__;
const mainMarker = __MAIN_MARKER__;
const patchBytes = __PATCH_BYTES__;
let installed = false;
let mainPatched = false;

function installHook() {
  if (installed) return;
  let target;
  try {
    target = Module.getGlobalExportByName('BCryptDecrypt');
  } catch (_) {
    return;
  }
  installed = true;
  Interceptor.attach(target, {
    onEnter(args) {
      this.output = args[6];
      this.capacity = args[7].toUInt32();
      this.resultLength = args[8];
    },
    onLeave(status) {
      if (status.toInt32() !== 0 || this.output.isNull() || this.resultLength.isNull()) return;
      const length = this.resultLength.readU32();
      if (length < 23 || length > this.capacity) return;
      if (this.output.readU8() !== 3 || this.output.add(1).readU8() !== 0) return;
      if (this.output.add(18).readU32() !== length - 22 || this.output.add(22).readU8() !== 1) return;

      const body = this.output.add(23);
      const bodyLength = length - 23;
      if (!mainPatched) {
        const hits = Memory.scanSync(body, bodyLength, patchPattern);
        if (hits.length === 1) {
          hits[0].address.writeByteArray(patchBytes);
          mainPatched = true;
          send({event: 'main-patched', bytes: patchBytes.length});
        } else if (Memory.scanSync(body, bodyLength, mainMarker).length !== 0) {
          send({event: 'fatal', reason: `main.js patch anchor count was ${hits.length}`});
        }
      }
      send({event: 'frame', length: length}, this.output.readByteArray(length));
    }
  });
  send({event: 'hook-ready', target: target.toString()});
}

installHook();
const timer = setInterval(() => {
  installHook();
  if (installed) clearInterval(timer);
}, 10);
"""
        .replace("__PATCH_PATTERN__", json.dumps(pattern))
        .replace("__MAIN_MARKER__", json.dumps(main_marker))
        .replace("__PATCH_BYTES__", json.dumps(list(replacement)))
    )


def write_export(output: Path, launcher: Path, result: ExportResult) -> None:
    if output.exists():
        raise ValueError(f"output already exists: {output}")
    catalog = json.loads(result.catalog.decode("utf-8-sig"))
    frame_counts = {
        str(row["id"]): len(extract_scene_frames(str(row["id"]), result.scenes[str(row["id"])]))
        for row in catalog
    }
    output.mkdir(parents=True)
    (output / "scenes.json").write_bytes(result.catalog)
    manifest_scenes: list[dict[str, object]] = []
    for row in catalog:
        scene_id = str(row["id"])
        body = result.scenes[scene_id]
        destination = output / "scene" / scene_id / "scene.json"
        destination.parent.mkdir(parents=True)
        destination.write_bytes(body)
        manifest_scenes.append(
            {
                "id": scene_id,
                "sceneUrl": row["sceneUrl"],
                "bytes": len(body),
                "sha256": hashlib.sha256(body).hexdigest(),
                "frameCount": frame_counts[scene_id],
            }
        )
    with launcher.open("rb") as stream:
        launcher_sha256 = hashlib.file_digest(stream, "sha256").hexdigest()
    manifest = {
        "format": "muvluv-g4-scene-export-v1",
        "launcher": launcher.name,
        "launcherSha256": launcher_sha256,
        "catalogBytes": len(result.catalog),
        "catalogSha256": hashlib.sha256(result.catalog).hexdigest(),
        "sceneCount": len(manifest_scenes),
        "frameCount": sum(frame_counts.values()),
        "scenes": manifest_scenes,
    }
    (output / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )


def capture(launcher: Path, timeout: float) -> ExportResult:
    try:
        import frida
    except ImportError as error:
        raise RuntimeError("install frida in a project-local uv venv before running") from error

    device = frida.get_local_device()
    existing = [
        process for process in device.enumerate_processes() if process.name.casefold().startswith("muvluvggx")
    ]
    if existing:
        names = ", ".join(f"{process.name}:{process.pid}" for process in existing)
        raise RuntimeError(f"close existing MuvLuvGGX processes first: {names}")

    collector = ExportCollector()
    hook_ready = threading.Event()
    main_patched = threading.Event()
    capture_done = threading.Event()
    fatal = threading.Event()
    fatal_messages: list[str] = []
    sessions = []
    scripts = []
    observed_pids: set[int] = set()
    pending_pids: set[int] = set()
    process_queue: queue.Queue[tuple[int, str] | None] = queue.Queue()
    lock = threading.Lock()
    stop_monitor = threading.Event()
    scene_count = 0

    def on_message(message: dict, data: bytes | None) -> None:
        nonlocal scene_count
        if message.get("type") == "error":
            fatal_messages.append(message.get("description", "Frida script error"))
            fatal.set()
            return
        payload = message.get("payload", {})
        event = payload.get("event")
        if event == "hook-ready":
            print(f"host_hook=ready target={payload['target']}", flush=True)
            hook_ready.set()
        elif event == "main-patched":
            print(f"main_patch=applied bytes={payload['bytes']}", flush=True)
            main_patched.set()
        elif event == "fatal":
            fatal_messages.append(str(payload.get("reason", "Host hook failed")))
            fatal.set()
        elif event == "frame" and data is not None:
            try:
                state = collector.feed(bytes(data))
                if state in {"catalog", "start", "done"}:
                    print(f"catalog_marker={state}", flush=True)
                elif state == "marker":
                    print("scene_marker=ready", flush=True)
                elif state == "scene":
                    scene_count += 1
                    if scene_count == 1 or scene_count % 25 == 0:
                        print(f"scene_json_captured={scene_count}", flush=True)
                elif state and state.startswith("missing:"):
                    print(f"scene_json_{state}", flush=True)
                if state == "done":
                    capture_done.set()
            except ValueError as error:
                fatal_messages.append(str(error))
                fatal.set()

    def safe_resume(pid: int) -> None:
        try:
            device.resume(pid)
        except (frida.InvalidOperationError, frida.ProcessNotFoundError):
            pass

    def enqueue(pid: int, identifier: str) -> None:
        with lock:
            if pid in observed_pids:
                return
            observed_pids.add(pid)
            pending_pids.add(pid)
        process_queue.put((pid, identifier))

    def on_child(child) -> None:
        identifier = getattr(child, "path", "") or getattr(child, "identifier", "")
        enqueue(child.pid, identifier)

    def monitor_host() -> None:
        while not stop_monitor.is_set():
            for process in device.enumerate_processes():
                if "muvluvggx-g4-host" in process.name.casefold():
                    enqueue(process.pid, process.name)
            stop_monitor.wait(0.02)

    def process_children() -> None:
        while True:
            item = process_queue.get()
            if item is None:
                return
            pid, identifier = item
            name = Path(identifier).name
            try:
                if "muvluvggx-g4-host" in name.casefold():
                    session = device.attach(pid)
                    script = session.create_script(build_hook())
                    script.on("message", on_message)
                    script.load()
                    with lock:
                        sessions.append(session)
                        scripts.append(script)
                    print(f"host_attached={name} pid={pid}", flush=True)
            except Exception as error:
                fatal_messages.append(f"attach {name or pid}: {type(error).__name__}: {error}")
                fatal.set()
            finally:
                safe_resume(pid)
                with lock:
                    pending_pids.discard(pid)

    worker = threading.Thread(target=process_children, name="g4-export-spawns", daemon=True)
    monitor = threading.Thread(target=monitor_host, name="g4-export-host-monitor", daemon=True)
    worker.start()
    monitor.start()
    device.on("child-added", on_child)
    launcher_pid = 0
    try:
        launcher_pid = device.spawn(str(launcher), cwd=str(launcher.parent))
        observed_pids.add(launcher_pid)
        launcher_session = device.attach(launcher_pid)
        launcher_session.enable_child_gating()
        sessions.append(launcher_session)
        print(f"launcher={launcher.name} pid={launcher_pid}", flush=True)
        safe_resume(launcher_pid)

        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline and not capture_done.is_set() and not fatal.is_set():
            capture_done.wait(0.25)
        if fatal.is_set():
            raise RuntimeError("; ".join(fatal_messages))
        if not capture_done.is_set():
            raise RuntimeError(
                "capture timed out "
                f"(hook_ready={hook_ready.is_set()}, main_patched={main_patched.is_set()}, scenes={scene_count})"
            )
        if not hook_ready.is_set() or not main_patched.is_set():
            raise RuntimeError("capture completed without the verified Host hook and main.js patch")
        return collector.result()
    finally:
        device.off("child-added", on_child)
        stop_monitor.set()
        monitor.join(5)
        process_queue.put(None)
        worker.join(5)
        with lock:
            stranded = tuple(pending_pids)
            attached = tuple(reversed(sessions))
            launched = tuple(sorted(observed_pids, reverse=True))
        for pid in stranded:
            safe_resume(pid)
        for session in attached:
            try:
                session.detach()
            except frida.InvalidOperationError:
                pass
        for pid in launched:
            try:
                device.kill(pid)
            except (frida.InvalidOperationError, frida.ProcessNotFoundError):
                pass


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--launcher", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--timeout", type=float, default=1_200.0)
    args = parser.parse_args()

    launcher = args.launcher.resolve(strict=True)
    output = args.output.resolve()
    if output == launcher.parent or launcher.parent in output.parents:
        raise SystemExit("refusing to write inside the read-only offline distribution")
    if output.exists():
        raise SystemExit(f"output already exists: {output}")
    try:
        result = capture(launcher, args.timeout)
        write_export(output, launcher, result)
    except (RuntimeError, ValueError) as error:
        print(f"result=failed reason={error}", flush=True)
        return 1
    print(f"result=complete scenes={len(result.scenes)} output={output}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
