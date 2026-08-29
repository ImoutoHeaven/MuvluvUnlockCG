#!/usr/bin/env python3
"""Read-only fingerprint and Addressables inventory for a mounted game directory."""

from __future__ import annotations

import hashlib
import json
import re
import sys
from pathlib import Path


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def printable_strings(blob: bytes) -> list[str]:
    return [match.decode("ascii") for match in re.findall(rb"[ -~]{4,}", blob)]


def describe(path: Path) -> dict[str, object]:
    return {
        "path": path.as_posix(),
        "size": path.stat().st_size,
        "sha256": sha256(path),
    }


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: inventory.py GAME_DIR", file=sys.stderr)
        return 2

    game = Path(sys.argv[1]).resolve()
    expected = {
        "game_assembly": game / "GameAssembly.dll",
        "metadata": game
        / "muv_luv_girlsgardenx_cl_Data"
        / "il2cpp_data"
        / "Metadata"
        / "global-metadata.dat",
        "api_interop": game / "BepInEx" / "interop" / "Api.dll",
        "game_ui_interop": game / "BepInEx" / "interop" / "GameUi.dll",
        "catalog": game
        / "muv_luv_girlsgardenx_cl_Data"
        / "StreamingAssets"
        / "aa"
        / "catalog.bin",
        "addressables_settings": game
        / "muv_luv_girlsgardenx_cl_Data"
        / "StreamingAssets"
        / "aa"
        / "settings.json",
    }

    missing = [name for name, path in expected.items() if not path.is_file()]
    if missing:
        print(json.dumps({"error": "missing expected files", "missing": missing}, indent=2))
        return 1

    bundles = sorted(game.rglob("*.bundle"))
    bundle_names = {path.name.lower() for path in bundles}

    catalog_blob = expected["catalog"].read_bytes()
    catalog_strings = printable_strings(catalog_blob)
    bundle_refs: set[str] = set()
    for value in catalog_strings:
        for match in re.finditer(
            r"(?:(?:rlp|StandaloneWindows64)[/\\])?[0-9a-f]{32}\.bundle",
            value,
            re.IGNORECASE,
        ):
            bundle_refs.add(match.group(0))

    def is_rlp(value: str) -> bool:
        normalized = value.replace("\\", "/").lower()
        return normalized.startswith("rlp/") or "/rlp/" in normalized

    rlp_refs = sorted(ref for ref in bundle_refs if is_rlp(ref))
    catalog_bundle_names = {
        Path(ref.replace("\\", "/")).name.lower() for ref in bundle_refs
    }
    rlp_bundle_names = {
        Path(ref.replace("\\", "/")).name.lower() for ref in rlp_refs
    }
    absent_ref_basenames = sorted(catalog_bundle_names - bundle_names)

    scene_payload_candidates = sorted(
        path.relative_to(game).as_posix()
        for path in game.rglob("*")
        if path.is_file()
        and "scene" in path.as_posix().lower()
        and path.suffix.lower() in {".bin", ".json", ".bytes", ".dat"}
    )

    settings = json.loads(expected["addressables_settings"].read_text(encoding="utf-8-sig"))
    catalog_locations = [
        {
            "keys": location.get("m_Keys", []),
            "internal_id": location.get("m_InternalId"),
            "provider": location.get("m_Provider"),
        }
        for location in settings.get("m_CatalogLocations", [])
    ]

    report = {
        "game_root": game.as_posix(),
        "fingerprints": {
            name: describe(path)
            for name, path in expected.items()
            if name not in {"addressables_settings"}
        },
        "filesystem": {
            "file_count": sum(1 for path in game.rglob("*") if path.is_file()),
            "total_bytes": sum(path.stat().st_size for path in game.rglob("*") if path.is_file()),
            "bundle_file_count": len(bundles),
            "bundle_bytes": sum(path.stat().st_size for path in bundles),
        },
        "catalog": {
            "unique_bundle_name_count": len(catalog_bundle_names),
            "non_rlp_bundle_name_count": len(catalog_bundle_names - rlp_bundle_names),
            "rlp_bundle_name_count": len(rlp_bundle_names),
            "absent_ref_basename_count": len(absent_ref_basenames),
            "rlp_bundle_refs": rlp_refs,
            "absent_ref_basenames": absent_ref_basenames,
        },
        "addressables": {
            "catalog_locations": catalog_locations,
        },
        "scene_payload_candidates": scene_payload_candidates,
    }
    print(json.dumps(report, indent=2, ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
