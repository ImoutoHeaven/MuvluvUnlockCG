#!/usr/bin/env python3
"""Read-only Unity cache bundle inventory for the downloaded Muv-Luv assets."""

from __future__ import annotations

import argparse
import json
import os
import re
from collections import Counter
from concurrent.futures import ProcessPoolExecutor
from pathlib import Path


KEYWORDS = (
    "adult",
    "character",
    "episode",
    "hscene",
    "memory",
    "r18",
    "scenario",
    "scene",
    "spine",
    "still",
    "voice",
)


def inspect_bundle(path_text: str, root_text: str) -> dict[str, object]:
    import UnityPy

    UnityPy.config.FALLBACK_UNITY_VERSION = "6000.0.59f2"
    path = Path(path_text)
    root = Path(root_text)
    try:
        environment = UnityPy.load(str(path))
        keys = sorted(str(key) for key in environment.container.keys())
        relevant = [
            key
            for key in keys
            if any(keyword in key.lower() for keyword in KEYWORDS)
        ]
        type_counts = Counter(obj.type.name for obj in environment.objects)
        return {
            "path": path.relative_to(root).as_posix(),
            "bytes": path.stat().st_size,
            "container_count": len(keys),
            "object_count": sum(type_counts.values()),
            "type_counts": dict(type_counts),
            "relevant_keys": relevant,
        }
    except Exception as error:  # UnityPy errors vary by serialized-file version.
        return {
            "path": path.relative_to(root).as_posix(),
            "bytes": path.stat().st_size,
            "error": f"{type(error).__name__}: {error}",
        }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("cache_dir", type=Path)
    parser.add_argument("--workers", type=int, default=min(6, os.cpu_count() or 1))
    parser.add_argument("--sample-limit", type=int, default=1000)
    args = parser.parse_args()

    root = args.cache_dir.resolve()
    bundles = sorted(root.glob("*/*/__data"))
    if not bundles:
        raise SystemExit(f"no Unity cache __data files found under {root}")

    type_counts: Counter[str] = Counter()
    keyword_counts: Counter[str] = Counter()
    identifier_counts: Counter[str] = Counter()
    relevant_samples: list[dict[str, object]] = []
    failures: list[dict[str, object]] = []
    total_containers = 0
    total_objects = 0

    with ProcessPoolExecutor(max_workers=args.workers) as pool:
        results = pool.map(
            inspect_bundle,
            (str(path) for path in bundles),
            (str(root) for _ in bundles),
            chunksize=16,
        )
        for result in results:
            if "error" in result:
                failures.append(result)
                continue

            total_containers += int(result["container_count"])
            total_objects += int(result["object_count"])
            type_counts.update(result["type_counts"])
            keys = result["relevant_keys"]
            for key in keys:
                lowered = key.lower()
                for keyword in KEYWORDS:
                    if keyword in lowered:
                        keyword_counts[keyword] += 1
                identifier_counts.update(
                    match.lower()
                    for match in re.findall(
                        r"(?i)(?:character|chara|memory|episode|scenario|scene|still)[_/-]?[0-9]{3,}",
                        key,
                    )
                )
            if keys and len(relevant_samples) < args.sample_limit:
                relevant_samples.append(
                    {
                        "bundle": result["path"],
                        "keys": keys[:100],
                    }
                )

    report = {
        "cache_root": root.as_posix(),
        "bundle_count": len(bundles),
        "bundle_bytes": sum(path.stat().st_size for path in bundles),
        "parsed_bundle_count": len(bundles) - len(failures),
        "failed_bundle_count": len(failures),
        "container_count": total_containers,
        "object_count": total_objects,
        "type_counts": dict(type_counts.most_common()),
        "keyword_key_counts": dict(keyword_counts.most_common()),
        "discovered_identifier_counts": dict(identifier_counts.most_common()),
        "relevant_bundle_samples": relevant_samples,
        "failures": failures[:100],
    }
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
