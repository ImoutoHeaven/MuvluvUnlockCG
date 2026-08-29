#!/usr/bin/env python3
"""Read-only inventory for Muv-Luv's BestHTTP MessagePack cache.

The game serializes both MasterDataPackage and SceneFrameMaster[] payloads with
MessagePack-CSharp's Lz4BlockArray format.  This tool deliberately emits only
structural counts and identifiers: it does not print dialogue text, HTTP
headers, cookies, or authentication material.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from collections import Counter
from pathlib import Path
from typing import Any, Iterable

import lz4.block
import msgpack

from g4_scene_export import ExportCollector, compare_original_scene_frames, extract_scene_frames


LZ4_BLOCK_ARRAY_EXTENSION = 98
EPISODE_MASTER_TAG = 8
SCENE_MASTER_TAG = 13
MAX_UNCOMPRESSED_BLOCK_BYTES = 64 * 1024 * 1024
MAX_UNCOMPRESSED_PAYLOAD_BYTES = 512 * 1024 * 1024


class CacheDecodeError(ValueError):
    """Raised when a cache body is not a supported Lz4BlockArray payload."""


def _unpack(blob: bytes) -> Any:
    return msgpack.unpackb(blob, raw=False, strict_map_key=False)


def decode_lz4_block_array(blob: bytes) -> Any:
    """Decode MessagePack-CSharp's Lz4BlockArray representation."""

    outer = _unpack(blob)
    if not isinstance(outer, list) or len(outer) < 2:
        raise CacheDecodeError("outer value is not an Lz4BlockArray")

    descriptor = outer[0]
    if not isinstance(descriptor, msgpack.ExtType):
        raise CacheDecodeError("Lz4BlockArray descriptor is not an ExtType")
    if descriptor.code != LZ4_BLOCK_ARRAY_EXTENSION:
        raise CacheDecodeError(
            f"unexpected extension code {descriptor.code}; "
            f"expected {LZ4_BLOCK_ARRAY_EXTENSION}"
        )

    size_reader = msgpack.Unpacker(raw=False, strict_map_key=False)
    size_reader.feed(descriptor.data)
    uncompressed_sizes = list(size_reader)
    blocks = outer[1:]
    if len(uncompressed_sizes) != len(blocks):
        raise CacheDecodeError(
            "block descriptor count does not match compressed block count: "
            f"{len(uncompressed_sizes)} != {len(blocks)}"
        )

    total_uncompressed_size = 0
    decompressed: list[bytes] = []
    for index, (size, block) in enumerate(zip(uncompressed_sizes, blocks)):
        if not isinstance(size, int) or size < 0:
            raise CacheDecodeError(f"invalid uncompressed size at block {index}")
        if size > MAX_UNCOMPRESSED_BLOCK_BYTES:
            raise CacheDecodeError(
                f"uncompressed block {index} exceeds safety limit: {size}"
            )
        total_uncompressed_size += size
        if total_uncompressed_size > MAX_UNCOMPRESSED_PAYLOAD_BYTES:
            raise CacheDecodeError(
                "uncompressed payload exceeds safety limit: "
                f"{total_uncompressed_size}"
            )
        if not isinstance(block, bytes):
            raise CacheDecodeError(f"compressed block {index} is not binary")
        try:
            decompressed.append(
                lz4.block.decompress(block, uncompressed_size=size)
            )
        except lz4.block.LZ4BlockError as error:
            raise CacheDecodeError(f"LZ4 failure at block {index}: {error}") from error

    return _unpack(b"".join(decompressed))


def _tagged_rows(value: Any) -> list[tuple[int, list[Any]]] | None:
    """Return MessagePack union rows as (tag, payload) pairs when applicable."""

    if not isinstance(value, list):
        return None
    result: list[tuple[int, list[Any]]] = []
    for row in value:
        if (
            not isinstance(row, list)
            or len(row) != 2
            or not isinstance(row[0], int)
            or not isinstance(row[1], list)
        ):
            return None
        result.append((row[0], row[1]))
    return result


def _master_package(value: Any) -> tuple[list[tuple[int, list[Any]]], Any] | None:
    if not isinstance(value, list) or len(value) != 2:
        return None
    rows = _tagged_rows(value[0])
    if rows is None:
        return None
    return rows, value[1]


def _scene_frames(value: Any) -> list[list[Any]] | None:
    """Recognize SceneFrameMaster[] without relying on cache filenames."""

    if not isinstance(value, list) or not value:
        return None
    if all(
        isinstance(row, list)
        and len(row) == 5
        and isinstance(row[0], int)
        and isinstance(row[1], int)
        and (row[2] is None or isinstance(row[2], int))
        and (row[3] is None or isinstance(row[3], int))
        and isinstance(row[4], str)
        for row in value
    ):
        return value
    return None


def _non_null(values: Iterable[Any]) -> int:
    return sum(value is not None for value in values)


def summarize_master(
    rows: list[tuple[int, list[Any]]],
    version: Any,
    verbose: bool = False,
) -> tuple[dict[str, Any], set[int]]:
    tag_counts = Counter(tag for tag, _ in rows)
    episodes = [payload for tag, payload in rows if tag == EPISODE_MASTER_TAG]
    scenes = [payload for tag, payload in rows if tag == SCENE_MASTER_TAG]

    valid_episodes = [row for row in episodes if len(row) == 11]
    valid_scenes = [row for row in scenes if len(row) == 7]
    character_ids = sorted(
        {row[5] for row in valid_episodes if isinstance(row[5], int)}
    )
    adult_scene_ids = sorted(
        row[0] for row in valid_scenes if row[4] is True and isinstance(row[0], int)
    )
    adult_episode_ids = sorted(
        {row[1] for row in valid_scenes if row[4] is True and isinstance(row[1], int)}
    )
    episode_by_id = {
        row[0]: row
        for row in valid_episodes
        if isinstance(row[0], int)
    }
    adult_episode_rows = [
        episode_by_id[episode_id]
        for episode_id in adult_episode_ids
        if episode_id in episode_by_id
    ]

    result: dict[str, Any] = {
        "kind": "MasterDataPackage",
        "version": version,
        "row_count": len(rows),
        "tag_count": len(tag_counts),
        "selected_tag_counts": {
            str(tag): tag_counts[tag]
            for tag in (EPISODE_MASTER_TAG, SCENE_MASTER_TAG, 37)
        },
        "episode_master": {
            "tag": EPISODE_MASTER_TAG,
            "row_count": len(episodes),
            "valid_shape_count": len(valid_episodes),
            "character_episode_count": _non_null(row[5] for row in valid_episodes),
            "affection_requirement_count": _non_null(row[6] for row in valid_episodes),
            "memory_episode_count": _non_null(row[7] for row in valid_episodes),
            "spot_character_episode_count": _non_null(row[8] for row in valid_episodes),
            "character_master_id_count": len(character_ids),
            "character_master_id_sample": character_ids[:10],
        },
        "scene_master": {
            "tag": SCENE_MASTER_TAG,
            "row_count": len(scenes),
            "valid_shape_count": len(valid_scenes),
            "adult_scene_count": len(adult_scene_ids),
            "adult_episode_count": len(adult_episode_ids),
            "adult_episode_master_match_count": len(adult_episode_rows),
            "adult_character_episode_count": _non_null(
                row[5] for row in adult_episode_rows
            ),
            "adult_affection_requirement_count": _non_null(
                row[6] for row in adult_episode_rows
            ),
            "adult_memory_episode_count": _non_null(
                row[7] for row in adult_episode_rows
            ),
            "adult_spot_character_episode_count": _non_null(
                row[8] for row in adult_episode_rows
            ),
            "adult_scene_id_sample": adult_scene_ids[:10],
        },
    }
    if verbose:
        result["tag_counts"] = {
            str(tag): count for tag, count in sorted(tag_counts.items())
        }
        result["episode_master"]["character_master_ids"] = character_ids
        result["scene_master"]["adult_scene_ids"] = adult_scene_ids
    return result, set(adult_scene_ids)


def _walk_json(value: Any) -> Iterable[tuple[str, Any]]:
    if isinstance(value, dict):
        for key, child in value.items():
            yield str(key), child
            yield from _walk_json(child)
    elif isinstance(value, list):
        for child in value:
            yield from _walk_json(child)


def summarize_scene_frames(frames: list[list[Any]]) -> dict[str, Any]:
    scene_ids = sorted({row[1] for row in frames})
    branch_ids = {row[2] for row in frames if row[2] is not None}
    selection_ids = {row[3] for row in frames if row[3] is not None}
    json_key_counts: Counter[str] = Counter()
    adult_animation_counts: Counter[str] = Counter()
    invalid_json_count = 0

    for row in frames:
        try:
            configuration = json.loads(row[4])
        except (json.JSONDecodeError, TypeError):
            invalid_json_count += 1
            continue
        for key, value in _walk_json(configuration):
            json_key_counts[key] += 1
            if key == "AdultStillAnimationType" and value is not None:
                adult_animation_counts[str(value)] += 1

    return {
        "kind": "SceneFrameMaster[]",
        "scene_master_ids": scene_ids,
        "frame_count": len(frames),
        "branch_id_count": len(branch_ids),
        "selection_id_count": len(selection_ids),
        "invalid_configuration_json_count": invalid_json_count,
        "configuration_key_counts": dict(json_key_counts.most_common()),
        "adult_still_animation_type_counts": dict(
            sorted(adult_animation_counts.items())
        ),
    }


def validate_g4_export(
    export_root: Path,
    original_frame_payloads: list[list[list[Any]]],
) -> dict[str, Any]:
    """Validate a complete G4 export and every available original-cache overlap."""
    manifest = json.loads((export_root / "manifest.json").read_text(encoding="utf-8"))
    catalog_body = (export_root / "scenes.json").read_bytes()
    catalog = ExportCollector._catalog_value(json.loads(catalog_body.decode("utf-8-sig")))
    if catalog is None or manifest.get("format") != "muvluv-g4-scene-export-v1":
        raise ValueError("incompatible G4 export catalog or manifest format")
    if manifest.get("catalogBytes") != len(catalog_body) or manifest.get("catalogSha256") != hashlib.sha256(catalog_body).hexdigest():
        raise ValueError("G4 catalog length or hash does not match manifest")

    manifest_rows = manifest.get("scenes")
    if not isinstance(manifest_rows, list) or manifest.get("sceneCount") != len(catalog):
        raise ValueError("G4 manifest scene count does not match catalog")
    manifest_by_id: dict[str, dict[str, Any]] = {}
    for row in manifest_rows:
        if not isinstance(row, dict) or not isinstance(row.get("id"), str) or row["id"] in manifest_by_id:
            raise ValueError("G4 manifest contains an invalid or duplicate scene ID")
        manifest_by_id[row["id"]] = row

    catalog_ids = [str(row["id"]) for row in catalog]
    actual_ids = {
        path.parent.name for path in (export_root / "scene").glob("*/scene.json")
    }
    if set(catalog_ids) != set(manifest_by_id) or set(catalog_ids) != actual_ids:
        raise ValueError("G4 catalog, manifest, and scene files do not contain the same IDs")

    exported_by_id: dict[str, list[dict]] = {}
    total_bytes = 0
    total_frames = 0
    for scene_id in catalog_ids:
        body = (export_root / "scene" / scene_id / "scene.json").read_bytes()
        row = manifest_by_id[scene_id]
        if row.get("bytes") != len(body) or row.get("sha256") != hashlib.sha256(body).hexdigest():
            raise ValueError(f"scene {scene_id} length or hash does not match manifest")
        frames = extract_scene_frames(scene_id, body)
        if "frameCount" in row and row["frameCount"] != len(frames):
            raise ValueError(f"scene {scene_id} frame count does not match manifest")
        exported_by_id[scene_id] = frames
        total_bytes += len(body)
        total_frames += len(frames)

    overlap_records = 0
    overlap_ids: set[str] = set()
    for original_rows in original_frame_payloads:
        scene_ids = {str(row[1]) for row in original_rows}
        if len(scene_ids) != 1:
            raise ValueError("original SceneFrame cache record contains multiple Scene IDs")
        scene_id = scene_ids.pop()
        if scene_id not in exported_by_id:
            raise ValueError(f"original cache Scene {scene_id} is absent from G4 export")
        compare_original_scene_frames(scene_id, original_rows, exported_by_id[scene_id])
        overlap_records += 1
        overlap_ids.add(scene_id)
    if overlap_records == 0:
        raise ValueError("no original SceneFrame cache overlap was available")

    return {
        "status": "compatible",
        "scene_count": len(catalog_ids),
        "scene_bytes": total_bytes,
        "frame_count": total_frames,
        "original_overlap_record_count": overlap_records,
        "original_overlap_unique_scene_count": len(overlap_ids),
        "mismatch_count": 0,
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "cache_root",
        type=Path,
        help="BestHTTP LocalCache directory (mounted read-only is sufficient)",
    )
    parser.add_argument(
        "--verbose",
        action="store_true",
        help="include every MasterData tag and discovered character/adult scene ID",
    )
    parser.add_argument(
        "--g4-export",
        type=Path,
        help="validate a complete licensed-runtime G4 export against every cached SceneFrame overlap",
    )
    args = parser.parse_args()

    root = args.cache_root.resolve()
    content_files = sorted(root.glob("Content/*/content.cache"))
    if not content_files:
        raise SystemExit(f"no Content/*/content.cache files found under {root}")

    entries: list[dict[str, Any]] = []
    master_entries: list[tuple[dict[str, Any], set[int]]] = []
    frame_entries: list[dict[str, Any]] = []
    frame_payloads: list[list[list[Any]]] = []
    failures: list[dict[str, str]] = []

    for path in content_files:
        relative_path = path.relative_to(root).as_posix()
        try:
            decoded = decode_lz4_block_array(path.read_bytes())
            master = _master_package(decoded)
            frames = _scene_frames(decoded)
            if master is not None:
                summary, adult_ids = summarize_master(*master, verbose=args.verbose)
                master_entries.append((summary, adult_ids))
            elif frames is not None:
                summary = summarize_scene_frames(frames)
                frame_entries.append(summary)
                frame_payloads.append(frames)
            else:
                summary = {
                    "kind": "unrecognized",
                    "decoded_type": type(decoded).__name__,
                    "decoded_length": len(decoded) if hasattr(decoded, "__len__") else None,
                }
            entries.append(
                {
                    "cache_id": path.parent.name,
                    "relative_path": relative_path,
                    "compressed_bytes": path.stat().st_size,
                    **summary,
                }
            )
        except Exception as error:  # Keep a complete audit even with stale entries.
            failures.append(
                {
                    "cache_id": path.parent.name,
                    "relative_path": relative_path,
                    "error": f"{type(error).__name__}: {error}",
                }
            )

    latest_master_record = max(
        master_entries,
        key=lambda record: int(str(record[0]["version"]).split("_", 1)[0]),
        default=None,
    )
    latest_master = latest_master_record[0] if latest_master_record else None
    cached_scene_ids = sorted(
        {
            scene_id
            for entry in frame_entries
            for scene_id in entry["scene_master_ids"]
        }
    )
    adult_scene_ids = latest_master_record[1] if latest_master_record else set()

    g4_validation: dict[str, Any] | None = None
    g4_validation_failed = False
    if args.g4_export is not None:
        try:
            g4_validation = validate_g4_export(args.g4_export.resolve(), frame_payloads)
        except Exception as error:
            g4_validation_failed = True
            g4_validation = {
                "status": "failed",
                "error": f"{type(error).__name__}: {error}",
            }

    report = {
        "cache_root": root.as_posix(),
        "content_file_count": len(content_files),
        "decoded_count": len(entries),
        "failure_count": len(failures),
        "master_package_count": len(master_entries),
        "scene_frame_array_count": len(frame_entries),
        "cached_scene_master_ids": cached_scene_ids,
        "cached_adult_scene_master_ids": sorted(adult_scene_ids & set(cached_scene_ids)),
        "latest_master_summary": latest_master,
        "entries": entries,
        "failures": failures,
        "g4_export_validation": g4_validation,
    }
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 1 if failures or g4_validation_failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
