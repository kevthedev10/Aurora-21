"""Bounds, provenance, pixel preservation and safe rerun checks; synthetic inputs only."""
import io
import json
from pathlib import Path
import struct
import tempfile
import unittest

from PIL import Image
import build_gallery as gallery


def archive(members):
    header_size = 16 + sum(9 + len(name.encode()) for name, _ in members)
    header = bytearray(b"BIGF" + b"\0" * 4 + struct.pack(">II", len(members), header_size))
    payload = bytearray()
    for name, data in members:
        header.extend(struct.pack(">II", header_size + len(payload), len(data)))
        header.extend(name.encode() + b"\0")
        payload.extend(data)
    result = header + payload
    struct.pack_into("<I", result, 4, len(result))
    return bytes(result)


class GalleryTests(unittest.TestCase):
    def test_big_keeps_member_names_and_checks_offsets(self):
        source = archive([("sg1", b""), ("42", b"DDS example")])
        self.assertEqual(gallery.big_members(source), [("42", b"DDS example")])
        bad = bytearray(source)
        struct.pack_into(">I", bad, 16, len(bad) + 1)
        struct.pack_into(">I", bad, 20, 1)
        with self.assertRaises(ValueError): gallery.big_members(bytes(bad))

    def test_refpack_literals_overlap_and_truncation(self):
        # Four literal bytes followed by an overlapping distance-one run and end marker.
        source = b"\x10\xfb\0\0\x07\xe0ABCD\0\0\xfc"
        self.assertEqual(gallery.refpack(source), b"ABCDDDD")
        with self.assertRaises(ValueError): gallery.refpack(source[:-3])
        with self.assertRaises(ValueError): gallery.refpack(b"\x10\xfb\0\0\x03\0\0\xfc")

    def test_filename_retains_native_identity_without_collisions(self):
        a = "data/ui/external/ion_fut/screens/alpha/panel.big"
        b = "data/ui/external/ion_fut/screens/beta/panel.big"
        self.assertNotEqual(gallery.output_name(a, "1"), gallery.output_name(b, "1"))
        self.assertLessEqual(len(gallery.output_name(a + "a" * 300, "1")), 150)

    def test_selection_excludes_general_portraits_and_keeps_fut(self):
        inventory = [{"Name": "data/ui/imgAssets/heads/123.dds", "Type": "DDS"},
                     {"Name": "data/ui/external/ion_fut/imgAssets/itemBGs/frame.dds", "Type": "DDS"},
                     {"Name": "data/ui/imgAssets/tiles/mainHub/FUTLarge.dds", "Type": "DDS"}]
        selected = gallery.select_assets(inventory)
        self.assertEqual(len(selected), 2)
        self.assertNotIn(inventory[0]["Name"], selected)

    def test_pixels_alpha_and_provenance_survive_and_edits_are_preserved(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            native, output = root / "native", root / "gallery"
            name = "data/ui/external/ion_fut/artAssets/test.big"
            pixels = Image.new("RGBA", (2, 2), (100, 20, 50, 127))
            stream = io.BytesIO()
            pixels.save(stream, format="PNG")
            data = archive([("9", stream.getvalue())])
            path = native / "raw" / name
            path.parent.mkdir(parents=True)
            path.write_bytes(data)
            manifest = {"schema": 1, "game": "FIFA21", "gameProfile": {}, "fmtRevision": "synthetic",
                        "scope": "fixture", "failures": [], "assets": [
                            {"name": name, "type": "BIG", "sha256": gallery.digest(data)}]}
            (native / "native-manifest.json").write_text(json.dumps(manifest))
            result = gallery.build(native, output)
            item = result["images"][0]
            self.assertEqual((item["width"], item["height"], item["member"], item["alpha"]), (2, 2, "9", True))
            self.assertEqual(item["pixelSha256"], gallery.digest(pixels.tobytes()))
            destination = output / item["file"]
            original_index = (output / "index.json").read_bytes()
            edited = Image.new("RGBA", (2, 2), (0, 255, 0, 255))
            edited.save(destination)
            result = gallery.build(native, output)
            self.assertIn("refusing to overwrite", result["failures"][0]["error"])
            self.assertEqual(Image.open(destination).tobytes(), edited.tobytes())
            self.assertEqual((output / "index.json").read_bytes(), original_index)
            path.write_bytes(b"tampered")
            result = gallery.build(native, output)
            self.assertIn("SHA256 mismatch", result["failures"][0]["error"])

    def test_dds_and_png_fallback_names_are_both_preserved(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            native, output = root / "native", root / "gallery"
            image = Image.new("RGBA", (4, 4), (50, 100, 150, 255))
            assets = []
            for kind in ("DDS", "PNG"):
                name = "data/ui/imgAssets/banners/futlivemsggen4/notfound." + kind.lower()
                stream = io.BytesIO()
                image.save(stream, format=kind)
                data = stream.getvalue()
                path = native / "raw" / name
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(data)
                assets.append({"name": name, "type": kind, "sha256": gallery.digest(data)})
            manifest = {"schema": 1, "game": "FIFA21", "gameProfile": {}, "fmtRevision": "synthetic",
                        "scope": "fixture", "failures": [], "assets": assets}
            (native / "native-manifest.json").write_text(json.dumps(manifest))
            result = gallery.build(native, output)
            self.assertEqual(result["failures"], [])
            self.assertEqual(len(result["images"]), 2)
            self.assertEqual(len({item["file"] for item in result["images"]}), 2)
            self.assertTrue(any(item["file"].endswith("__source_png.png") for item in result["images"]))
            previous = json.loads((output / "index.json").read_text())
            # Simulate an earlier interrupted conversion of only the PNG representation.
            previous["images"] = [item for item in previous["images"] if item["source"].endswith(".dds")]
            png_name = assets[1]["name"]
            previous["failures"] = [{"source": png_name, "member": None, "error": "previous failure"}]
            (output / "index.json").write_text(json.dumps(previous))
            result = gallery.build(native, output, only_sources={png_name})
            self.assertEqual(result["failures"], [])
            self.assertEqual(len(result["images"]), 2)


if __name__ == "__main__":
    unittest.main(verbosity=2)
