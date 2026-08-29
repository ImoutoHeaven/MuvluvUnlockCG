#!/usr/bin/env python3
"""Minimal localhost-only CDP probe for the licensed G4 WebView2 player."""

import argparse
import json
import time
import urllib.request
from urllib.parse import urlsplit

import websocket


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=9223)
    parser.add_argument("--navigate")
    parser.add_argument(
        "--eval",
        default="({href:location.href,title:document.title,ready:document.readyState})",
    )
    args = parser.parse_args()

    endpoint = f"http://127.0.0.1:{args.port}/json/list"
    with urllib.request.urlopen(endpoint, timeout=5) as response:
        targets = json.load(response)
    target = next((item for item in targets if item.get("type") == "page"), None)
    if target is None:
        raise SystemExit("no CDP page target")

    ws_url = target["webSocketDebuggerUrl"]
    parsed_ws = urlsplit(ws_url)
    if parsed_ws.scheme != "ws" or parsed_ws.hostname not in {"127.0.0.1", "localhost"}:
        raise SystemExit("refusing non-local CDP endpoint")
    if args.navigate:
        parsed_nav = urlsplit(args.navigate)
        if (parsed_nav.scheme, parsed_nav.hostname) != ("https", "mlg.g4.invalid"):
            raise SystemExit("refusing navigation outside the G4 virtual origin")

    sock = websocket.create_connection(
        ws_url,
        timeout=10,
        origin=f"http://127.0.0.1:{args.port}",
        suppress_origin=False,
    )
    next_id = 0

    def call(method: str, params: dict | None = None) -> dict:
        nonlocal next_id
        next_id += 1
        sock.send(json.dumps({"id": next_id, "method": method, "params": params or {}}))
        while True:
            message = json.loads(sock.recv())
            if message.get("id") != next_id:
                continue
            if "error" in message:
                raise RuntimeError(message["error"])
            return message.get("result", {})

    try:
        call("Page.enable")
        call("Runtime.enable")
        if args.navigate:
            call("Page.navigate", {"url": args.navigate})
            time.sleep(2)
        evaluation = call(
            "Runtime.evaluate",
            {"expression": args.eval, "returnByValue": True, "awaitPromise": True},
        )
        if "exceptionDetails" in evaluation:
            details = evaluation["exceptionDetails"]
            description = details.get("exception", {}).get("description", details.get("text"))
            raise SystemExit(f"evaluation failed: {description}")
        result = evaluation.get("result", {})
        value = result.get("value", result.get("unserializableValue", result.get("description")))
        print(json.dumps(value, ensure_ascii=False))
    finally:
        sock.close()


if __name__ == "__main__":
    main()
