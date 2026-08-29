#!/usr/bin/env python3
"""Extract review-sized Cpp2IL method fixtures from the persisted full ISIL.

The whitelist is intentionally narrow and contains only code paths used by the
plugin. It never reads or emits HTTP metadata, credentials, dialogue catalogs,
or media assets.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path


METHODS: dict[str, tuple[str, ...]] = {
    "GameUi/Assets/GameUi/Episode/EpisodeController.txt": (
        "GenerateCharacterCellArgs(",
        "<GenerateCharacterCellArgs>b__54_0(",
        "GenerateMemoryCellArgs(",
        "<GenerateMemoryCellArgs>b__56_0(",
        "<GenerateMemoryCellArgs>b__56_1(",
        "MoveToAdventure(",
    ),
    "GameUi/Assets/GameUi/Episode/EpisodeController_NestedType___c.txt": (
        "<GenerateCharacterCellArgs>b__54_1(",
        "<GenerateCharacterCellArgs>b__54_2(",
        "<GenerateMemoryCellArgs>b__56_2(",
    ),
    "GameUi/Assets/GameUi/Episode/EpisodeController_NestedType___c__DisplayClass54_0.txt": (
        "<GenerateCharacterCellArgs>b__4(",
        "<GenerateCharacterCellArgs>b__6(",
    ),
    "GameUi/Assets/GameUi/Episode/EpisodeController_NestedType___c__DisplayClass56_0.txt": (
        "<GenerateMemoryCellArgs>b__3(",
    ),
    "GameUi/Assets/GameUi/Episode/EpisodeController_NestedType__MoveToAdventure_d__60.txt": (
        "MoveNext(",
    ),
    "GameUi/Assets/GameUi/Episode/EpisodeController_NestedType__MoveToAdventure_d__61.txt": (
        "MoveNext(",
    ),
    "GameUi/Assets/GameUi/Service/LocationService.txt": (
        "CanAccessCharacterEpisode(",
    ),
    "GameUi/Assets/GameUi/Service/EpisodeService.txt": (
        "TrySendDataTrackingEvent(",
        "DownloadSceneFrameMasters(",
        "PostRead(",
        "MoveToScenario(",
        "MoveToFirstSceneOrRated(",
    ),
    "GameUi/Assets/GameUi/Service/EpisodeService_NestedType__DownloadSceneFrameMasters_d__15.txt": (
        "MoveNext(",
    ),
    "GameUi/Assets/GameUi/Service/EpisodeService_NestedType__PostRead_d__16.txt": (
        "MoveNext(",
    ),
    "GameUi/Assets/GameUi/Service/EpisodeService_NestedType__MoveToFirstSceneOrRated_d__22.txt": (
        "MoveNext(",
    ),
    "GameUi/Assets/GameUi/Scenario/ScenarioController.txt": (
        "Refresh(",
        "Leave(",
        "PostBranchSelection(",
        "PostRead(",
    ),
    "GameUi/Assets/GameUi/Scenario/ScenarioController_NestedType__Refresh_d__76.txt": (
        "MoveNext(",
    ),
    "GameUi/Assets/GameUi/Scenario/ScenarioController_NestedType__Leave_d__85.txt": (
        "MoveNext(",
    ),
    "GameUi/Assets/GameUi/Scenario/ScenarioController_NestedType__PostBranchSelection_d__103.txt": (
        "MoveNext(",
    ),
    "GameUi/Assets/GameUi/Scenario/ScenarioController_NestedType__PostRead_d__104.txt": (
        "MoveNext(",
    ),
    "Api/Assets/Api/Client/ApiClient.txt": (
        "GetApiEpisodeScenesDataSource(",
        "PostApiEpisodeScenesBranchSelection(",
        "PostApiEpisodesReadPost(",
    ),
    "Api/Assets/Api/Client/Result`1.txt": (
        ".ctor(",
        "get_Data(",
        "set_Data(",
    ),
    "Api/Assets/Api/Client/EpisodeMaster.txt": (
        "get_ScenesMaster(",
        "get_CharacterMasterId(",
        "get_AffectionLevel(",
        "get_MemoryMasterId(",
    ),
    "Api/Assets/Api/Client/MemoryMaster.txt": (
        "get_IsReleased(",
        "get_Memories(",
    ),
}


def split_method_sections(text: str) -> tuple[str, list[str]]:
    lines = text.splitlines(keepends=True)
    first_method = next(
        (index for index, line in enumerate(lines) if line.startswith("Method: ")),
        len(lines),
    )
    preamble = "".join(lines[:first_method])
    sections: list[str] = []
    start = first_method
    for index in range(first_method + 1, len(lines)):
        if lines[index].startswith("Method: "):
            sections.append("".join(lines[start:index]))
            start = index
    if start < len(lines):
        sections.append("".join(lines[start:]))
    return preamble, sections


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("input_root", type=Path)
    parser.add_argument("output_root", type=Path)
    args = parser.parse_args()

    args.output_root.mkdir(parents=True, exist_ok=True)
    manifest: list[dict[str, object]] = []

    for relative, markers in METHODS.items():
        source = args.input_root / relative
        if not source.is_file():
            raise SystemExit(f"missing persisted ISIL source: {source}")

        preamble, sections = split_method_sections(source.read_text(encoding="utf-8"))
        selected = [
            section
            for section in sections
            if any(marker in section.splitlines()[0] for marker in markers)
        ]
        missing = [
            marker
            for marker in markers
            if not any(marker in section.splitlines()[0] for section in selected)
        ]
        if missing:
            raise SystemExit(f"missing method markers in {source}: {missing}")

        output_name = relative.replace("/", "__")
        destination = args.output_root / output_name
        destination.write_text(preamble + "\n".join(selected), encoding="utf-8")
        manifest.append(
            {
                "source": relative,
                "source_sha256": sha256(source),
                "fixture": output_name,
                "fixture_sha256": sha256(destination),
                "method_markers": list(markers),
            }
        )

    (args.output_root / "index.json").write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )
    print(f"selected-evidence: {len(manifest)} files")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
