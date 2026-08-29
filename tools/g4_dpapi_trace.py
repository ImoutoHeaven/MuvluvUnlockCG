#!/usr/bin/env python3
"""Trace G4 Broker DPAPI shape without persisting protected plaintext or entropy."""

from __future__ import annotations

import argparse
import hashlib
import queue
import threading
import time
from pathlib import Path

import frida


HOOK = r"""
const pointerOffset = Process.pointerSize === 8 ? 8 : 4;
let nextCallId = 1;

function readBlob(blob) {
    if (blob.isNull())
        return { length: 0, data: NULL };
    return {
        length: blob.readU32(),
        data: blob.add(pointerOffset).readPointer()
    };
}

function startsWith(data, length, expected) {
    if (data.isNull() || length < expected.length)
        return false;
    for (let i = 0; i !== expected.length; i++) {
        if (data.add(i).readU8() !== expected[i])
            return false;
    }
    return true;
}

const dpapiPrefix = [
    0x01, 0x00, 0x00, 0x00, 0xd0, 0x8c, 0x9d, 0xdf,
    0x01, 0x15, 0xd1, 0x11, 0x8c, 0x7a, 0x00, 0xc0,
    0x4f, 0xc2, 0x97, 0xeb
];

const target = Module.getGlobalExportByName("CryptUnprotectData");
Interceptor.attach(target, {
    onEnter(args) {
        this.callId = nextCallId++;
        this.outputBlob = args[6];
        const input = readBlob(args[0]);
        const entropy = readBlob(args[2]);
        send({
            event: "dpapi-enter",
            callId: this.callId,
            inputLength: input.length,
            inputLooksDpapi: startsWith(input.data, input.length, dpapiPrefix),
            entropyLength: entropy.length
        }, entropy.length > 0 && !entropy.data.isNull()
            ? entropy.data.readByteArray(entropy.length)
            : null);
    },
    onLeave(result) {
        const success = result.toInt32() !== 0;
        if (!success) {
            send({ event: "dpapi-leave", callId: this.callId, success: false });
            return;
        }
        const output = readBlob(this.outputBlob);
        send({
            event: "dpapi-leave",
            callId: this.callId,
            success: true,
            outputLength: output.length,
            outputLooksDpapi: startsWith(output.data, output.length, dpapiPrefix),
            outputLooksDerSequence: !output.data.isNull() && output.length > 0 && output.data.readU8() === 0x30
        });
    }
});
send({ event: "hook-ready", target: target.toString() });
"""


def entropy_candidates(device_path: Path) -> dict[bytes, str]:
    data = device_path.read_bytes()
    fingerprint = data[501:533]
    binding = data[533:565]
    values: dict[str, bytes] = {
        "binding": binding,
        "fingerprint": fingerprint,
        "fingerprint+binding": fingerprint + binding,
        "binding+fingerprint": binding + fingerprint,
    }
    for text in (
        "mlg-g4-device",
        "muvluv-girls-garden-x-g4",
        "muvluv-girls-garden-x-secure",
        "offline-secure-2026-08-g4",
        "muvluv-girls-garden-x-secure|offline-secure-2026-08-g4",
    ):
        for encoding in ("utf-8", "utf-16le"):
            value = text.encode(encoding)
            name = f"{encoding}:{text}"
            values[name] = value
            values[f"sha256:{name}"] = hashlib.sha256(value).digest()
    return {hashlib.sha256(value).digest(): name for name, value in values.items()}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--launcher", required=True, type=Path)
    parser.add_argument("--device", required=True, type=Path)
    parser.add_argument("--timeout", type=float, default=90.0)
    args = parser.parse_args()

    launcher = args.launcher.resolve(strict=True)
    device_path = args.device.resolve(strict=True)
    known_entropy = entropy_candidates(device_path)
    captured = threading.Event()
    sessions: list[frida.Session] = []
    scripts: list[frida.Script] = []
    spawn_queue: queue.Queue[tuple[int, str] | None] = queue.Queue()
    pending_pids: set[int] = set()
    observed_pids: set[int] = set()
    lock = threading.Lock()
    broker_hooked = threading.Event()
    monitor_stop = threading.Event()

    device = frida.get_local_device()

    def on_message(message: dict, data: bytes | None) -> None:
        if message.get("type") == "error":
            print(f"trace_script_error={message.get('description', 'unknown')}", flush=True)
            return
        payload = message.get("payload", {})
        event = payload.get("event")
        if event == "hook-ready":
            broker_hooked.set()
            print(f"broker_dpapi_hook=ready target={payload['target']}", flush=True)
        elif event == "dpapi-enter":
            entropy_name = "none"
            if data:
                entropy_name = known_entropy.get(hashlib.sha256(data).digest(), "unmatched")
            print(
                "dpapi_enter="
                f"call:{payload['callId']} input:{payload['inputLength']} "
                f"input_dpapi:{str(payload['inputLooksDpapi']).lower()} "
                f"entropy:{payload['entropyLength']} entropy_match:{entropy_name}",
                flush=True,
            )
        elif event == "dpapi-leave":
            if payload["success"]:
                print(
                    "dpapi_leave="
                    f"call:{payload['callId']} success:true output:{payload['outputLength']} "
                    f"output_dpapi:{str(payload['outputLooksDpapi']).lower()} "
                    f"output_der:{str(payload['outputLooksDerSequence']).lower()}",
                    flush=True,
                )
                captured.set()
            else:
                print(f"dpapi_leave=call:{payload['callId']} success:false", flush=True)

    def safe_resume(pid: int) -> None:
        try:
            device.resume(pid)
        except (frida.InvalidOperationError, frida.ProcessNotFoundError):
            pass

    def enqueue_process(pid: int, identifier: str) -> None:
        with lock:
            if pid in observed_pids:
                return
            observed_pids.add(pid)
            pending_pids.add(pid)
        spawn_queue.put((pid, identifier))

    def on_child(child) -> None:
        identifier = getattr(child, "path", "") or getattr(child, "identifier", "")
        enqueue_process(child.pid, identifier)

    def monitor_broker() -> None:
        while not monitor_stop.is_set():
            for process in device.enumerate_processes():
                if process.name.casefold() == "muvluvggx-g4-broker.exe":
                    enqueue_process(process.pid, process.name)
            monitor_stop.wait(0.01)

    def process_spawns() -> None:
        while True:
            item = spawn_queue.get()
            if item is None:
                return
            pid, identifier = item
            name = Path(identifier).name
            print(f"spawn={name or identifier or '<unknown>'} pid={pid}", flush=True)
            try:
                if name.casefold() == "muvluvggx-g4-broker.exe":
                    session = device.attach(pid)
                    script = session.create_script(HOOK)
                    script.on("message", on_message)
                    script.load()
                    with lock:
                        sessions.append(session)
                        scripts.append(script)
            except Exception as exception:
                print(f"spawn_attach_failed={name}: {type(exception).__name__}: {exception}", flush=True)
            finally:
                safe_resume(pid)
                with lock:
                    pending_pids.discard(pid)

    worker = threading.Thread(target=process_spawns, name="g4-spawn-worker", daemon=True)
    monitor = threading.Thread(target=monitor_broker, name="g4-broker-monitor", daemon=True)
    worker.start()
    monitor.start()
    device.on("child-added", on_child)
    try:
        launcher_pid = device.spawn(str(launcher), cwd=str(launcher.parent))
        launcher_session = device.attach(launcher_pid)
        launcher_session.enable_child_gating()
        with lock:
            sessions.append(launcher_session)
        print(f"launcher={launcher.name} pid={launcher_pid}", flush=True)
        safe_resume(launcher_pid)

        captured.wait(args.timeout)
        if captured.is_set():
            time.sleep(2.0)
    finally:
        device.off("child-added", on_child)
        monitor_stop.set()
        monitor.join(10.0)
        spawn_queue.put(None)
        worker.join(10.0)
        with lock:
            stranded_pids = tuple(pending_pids)
        for pid in stranded_pids:
            safe_resume(pid)
        with lock:
            attached_sessions = tuple(reversed(sessions))
        for session in attached_sessions:
            try:
                session.detach()
            except frida.InvalidOperationError:
                pass

    if not broker_hooked.is_set():
        print("result=no_broker_hook", flush=True)
        return 1
    if not captured.is_set():
        print("result=no_successful_dpapi_call", flush=True)
        return 1
    print("result=dpapi_shape_captured", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
