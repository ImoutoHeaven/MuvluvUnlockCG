#!/usr/bin/env python3
"""Search Unity cache bundle keys/object names without extracting game assets."""

from __future__ import annotations

import argparse
import json
import os
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path


def inspect_bundle(
    path_text: str,
    root_text: str,
    patterns: tuple[str, ...],
) -> dict[str, object]:
    import UnityPy

    UnityPy.config.FALLBACK_UNITY_VERSION = "6000.0.59f2"
    path = Path(path_text)
    root = Path(root_text)
    lowered_patterns = tuple(pattern.casefold() for pattern in patterns)
    key_matches: list[str] = []
    object_matches: list[dict[str, str]] = []

    try:
        environment = UnityPy.load(str(path))
        for key in environment.container.keys():
            text = str(key)
            lowered = text.casefold()
            if any(pattern in lowered for pattern in lowered_patterns):
                key_matches.append(text)

        for obj in environment.objects:
            try:
                name = obj.peek_name()
            except Exception:
                continue
            if not isinstance(name, str):
                continue
            lowered = name.casefold()
            if any(pattern in lowered for pattern in lowered_patterns):
                object_matches.append({"type": obj.type.name, "name": name})

        return {
            "bundle": path.relative_to(root).as_posix(),
            "key_matches": sorted(set(key_matches)),
            "object_matches": sorted(
                object_matches,
                key=lambda item: (item["type"], item["name"]),
            ),
        }
    except Exception as error:
        return {
            "bundle": path.relative_to(root).as_posix(),
            "error": f"{type(error).__name__}: {error}",
        }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("cache_dir", type=Path)
    parser.add_argument("patterns", nargs="+", help="case-insensitive substrings")
    parser.add_argument("--workers", type=int, default=min(6, os.cpu_count() or 1))
    args = parser.parse_args()

    root = args.cache_dir.resolve()
    bundles = sorted(root.glob("*/*/__data"))
    if not bundles:
        raise SystemExit(f"no Unity cache __data files found under {root}")

    patterns = tuple(dict.fromkeys(args.patterns))
    matches: list[dict[str, object]] = []
    failures: list[dict[str, object]] = []
    with ProcessPoolExecutor(max_workers=args.workers) as pool:
        results = pool.map(
            inspect_bundle,
            (str(path) for path in bundles),
            (str(root) for _ in bundles),
            (patterns for _ in bundles),
            chunksize=16,
        )
        for result in results:
            if "error" in result:
                failures.append(result)
            elif result["key_matches"] or result["object_matches"]:
                matches.append(result)

    report = {
        "cache_root": root.as_posix(),
        "patterns": patterns,
        "bundle_count": len(bundles),
        "matching_bundle_count": len(matches),
        "key_match_count": sum(len(item["key_matches"]) for item in matches),
        "object_match_count": sum(len(item["object_matches"]) for item in matches),
        "failed_bundle_count": len(failures),
        "matches": matches,
        "failures": failures,
    }
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
