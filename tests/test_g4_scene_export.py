"""Public resource-frame contract for the licensed G4 runtime exporter.

Evidence:
- evidence/g4-offline/README.md, "Frontend route and chunk evidence"
- evidence/decomp/interop-src/Api/Assets.Api.Client/SceneFrameMaster.cs
"""

from __future__ import annotations

import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).parents[1] / "tools" / "g4_scene_export.py"
SPEC = importlib.util.spec_from_file_location("g4_scene_export", MODULE_PATH)
assert SPEC and SPEC.loader
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


def resource_frame(response_id: bytes, body: bytes, *, declared_delta: int = 0) -> bytes:
    payload_length = 1 + len(body) + declared_delta
    return b"\x03\x00" + response_id + payload_length.to_bytes(4, "little") + b"\x01" + body


class ResourceFrameContractTests(unittest.TestCase):
    def test_validates_envelope_and_marks_only_short_chunks_final(self) -> None:
        response_id = bytes(range(16))
        tail = MODULE.decode_resource_frame(resource_frame(response_id, b'[{"id":"40003302"}]'))
        self.assertEqual(response_id, tail.response_id)
        self.assertEqual(b'[{"id":"40003302"}]', tail.body)
        self.assertTrue(tail.is_final)
        self.assertFalse(MODULE.decode_resource_frame(resource_frame(response_id, b"x" * 65_536)).is_final)
        with self.assertRaisesRegex(ValueError, "payload length"):
            MODULE.decode_resource_frame(resource_frame(response_id, b"{}", declared_delta=1))

    def test_catalog_markers_bound_an_ordered_complete_export(self) -> None:
        catalog = (
            b'[{"id":"10000001","rootUrl":"./public/scene/10000001/",'
            b'"sceneUrl":"./public/scene/10000001/scene.json","title":"'
            + b"x" * 65_536
            + b'","storyType":"main","preloaded":false},'
            b'{"id":"40000002","rootUrl":"./public/scene/40000002/",'
            b'"sceneUrl":"./public/scene/40000002/scene.json","storyType":"character","preloaded":false}]'
        )
        first_scene = b'{"frames":"' + b"x" * (65_536 - len(b'{"frames":"') - len(b'"}')) + b'"}'
        second_scene = b'{"frames":[{"order":2}]}'
        marker = b'{"g4export":"marker"}'

        def collect(documents: tuple[bytes, ...]):
            collector = MODULE.ExportCollector()
            frame_index = 0
            for body in documents:
                for offset in range(0, len(body), 65_536):
                    frame_index += 1
                    collector.feed(
                        resource_frame(
                            frame_index.to_bytes(16, "little"), body[offset : offset + 65_536]
                        )
                    )
            return collector

        collector = collect(
            (catalog, catalog, marker, first_scene, marker, second_scene, marker, catalog)
        )
        result = collector.result()
        self.assertEqual(("10000001", "40000002"), tuple(result.scenes))
        self.assertEqual(first_scene, result.scenes["10000001"])
        self.assertEqual(second_scene, result.scenes["40000002"])

        missing = collect((catalog, catalog, marker, first_scene, marker, marker, catalog))
        with self.assertRaisesRegex(ValueError, "40000002"):
            missing.result()

    def test_extracts_only_lossless_muvluv_frames_and_rejects_scene_mismatch(self) -> None:
        # Evidence: all ten original BestHTTP overlap records match these five
        # G4 frame fields exactly; SceneFrameMaster.cs persists their types.
        scene = (
            b'{"assets":[],"commands":['
            b'{"line":1,"type":"text","text":"ignored"},'
            b'{"line":2,"type":"muvluvFrame","frame":{'
            b'"order":7,"sceneId":40003301,"branchId":null,'
            b'"selectedBranchId":9,"configuration":{"Phrase":{"Text":"fixture"}},'
            b'"background":{},"characters":[],"needsHideText":false}}],'
            b'"id":"40003301","notes":[],"preloaded":false,"title":"fixture"}'
        )
        frames = MODULE.extract_scene_frames("40003301", scene)
        self.assertEqual(1, len(frames))
        self.assertEqual(7, frames[0]["order"])
        self.assertEqual(9, frames[0]["selectedBranchId"])

        with self.assertRaisesRegex(ValueError, "Scene ID"):
            MODULE.extract_scene_frames("40003302", scene)

    def test_compares_all_five_original_scene_frame_fields(self) -> None:
        exported = [{
            "order": 7,
            "sceneId": 40003301,
            "branchId": None,
            "selectedBranchId": 9,
            "configuration": {"Phrase": {"Text": "fixture"}},
        }]
        original = [[7, 40003301, None, 9, '{"Phrase":{"Text":"fixture"}}']]
        MODULE.compare_original_scene_frames("40003301", original, exported)

        changed = [[7, 40003301, None, 9, '{"Phrase":{"Text":"different"}}']]
        with self.assertRaisesRegex(ValueError, "ConfigurationJson"):
            MODULE.compare_original_scene_frames("40003301", changed, exported)


    def test_writes_only_real_scenes_and_skips_g4_placeholders(self) -> None:
        catalog = json.dumps(
            [
                {
                    "id": "40000002",
                    "rootUrl": "./public/scene/40000002/",
                    "sceneUrl": "./public/scene/40000002/scene.json",
                    "storyType": "character",
                    "preloaded": False,
                },
                {
                    "id": "missing-400089",
                    "rootUrl": "./public/scene/missing-400089/",
                    "sceneUrl": "./public/scene/missing-400089/scene.json",
                    "storyType": "missing",
                    "preloaded": False,
                    "visualOnly": True,
                },
            ],
            ensure_ascii=False,
            indent=2,
        ).encode("utf-8") + b"\n"
        scene = (
            b'{"assets":[],"commands":[{"line":1,"type":"muvluvFrame","frame":{'
            b'"order":1,"sceneId":40000002,"branchId":null,"selectedBranchId":null,'
            b'"configuration":{},"background":{},"characters":[],"needsHideText":false}}],'
            b'"id":"40000002","notes":[],"preloaded":false,"title":"fixture"}'
        )
        result = MODULE.ExportResult(
            catalog, {"40000002": scene, "missing-400089": b'{"id":"missing-400089"}'}
        )

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            launcher = root / "MuvLuvGGX.exe"
            launcher.write_bytes(b"launcher")
            output = root / "export"

            self.assertEqual(1, MODULE.write_export(output, launcher, result))

            written = json.loads((output / "scenes.json").read_text(encoding="utf-8"))
            self.assertEqual(["40000002"], [row["id"] for row in written])
            manifest = json.loads((output / "manifest.json").read_text(encoding="utf-8"))
            self.assertEqual(1, manifest["sceneCount"])
            self.assertFalse((output / "scene" / "missing-400089").exists())


if __name__ == "__main__":
    unittest.main()
