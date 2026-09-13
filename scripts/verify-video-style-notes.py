"""Synthetic checks for lossless video-style note preparation."""

from copy import deepcopy
from decimal import Decimal
import importlib.util
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("style_notes", Path(__file__).with_name("prepare-video-style-notes.py"))
notes = importlib.util.module_from_spec(spec)
spec.loader.exec_module(notes)


class StyleNotesChecks(unittest.TestCase):
    def fixture(self, prompt):
        return {"Version": 1, "FutureRoot": {"ratio": Decimal("0.123456789012345678901")},
                "PhotorealStyles": [{"Untouched": True}], "SelectedVideoStyleName": "Sample",
                "VideoStyles": [{"Name": "Sample", "Prompt": prompt, "Steps": 20, "FutureStyle": [1, 2]}]}

    def test_exact_reconstruction_and_unknown_fields(self):
        for newline in ["\n", "\r\n"]:
            with self.subTest(newline=newline):
                marker = "▼▼▼ 使用時はこの行から末尾まで全削除｜日本語訳 ▼▼▼" + newline
                body = "[Shot 1] A person waves.  " + newline + "<d>[Japanese] こんにちは。</d>" + newline * 2
                original = body + marker + "手を振る。" + newline
                source = self.fixture(original)
                untouched = deepcopy(source)
                prepared, report = notes.prepare_document(source)
                style = prepared["VideoStyles"][0]
                program = style["InstructionProgram"]
                self.assertEqual(source, untouched)
                self.assertEqual(style["Prompt"], body)
                self.assertEqual(program["BaseH3Template"] + program["OriginalStyleText"]["Separator"] + program["Description"], original)
                self.assertEqual(prepared["PhotorealStyles"], source["PhotorealStyles"])
                self.assertFalse(program["Enabled"])
                self.assertEqual(program["Options"], {})
                self.assertEqual(report["notesSeparated"], 1)
                self.assertEqual(notes.read_document(notes.encode_document(prepared).encode("utf-8")), prepared)

    def test_no_separator_means_no_text_changes(self):
        original = " [Shot 1]\n  C:\\sample \\[literal\\] &#x20; \r\n"
        prepared, report = notes.prepare_document(self.fixture(original))
        self.assertEqual(prepared["VideoStyles"][0]["Prompt"], original)
        self.assertEqual(report["notesSeparated"], 0)

    def test_prepared_document_is_idempotent(self):
        once, _ = notes.prepare_document(self.fixture("A person waves."))
        twice, report = notes.prepare_document(once)
        self.assertEqual(once, twice)
        self.assertEqual(report["existingProgramsUnchanged"], 1)

    def test_duplicates_future_and_ambiguous_marker_refused(self):
        for raw in [b'{"Version":1,"Version":2,"VideoStyles":[]}', b'{"Version":2,"VideoStyles":[]}',
                    b'{"Version":1,"VideoStyles":[],"future":{"x":1,"x":2}}']:
            with self.assertRaises(ValueError):
                notes.read_document(raw)
        with self.assertRaises(ValueError):
            notes.prepare_document(self.fixture("unrecognized 使用時はこの行から末尾まで全削除 marker"))
        source = self.fixture("A person waves.")
        source["VideoStyles"][0]["InstructionProgram"] = {"Version": 2}
        with self.assertRaises(ValueError):
            notes.prepare_document(source)

    def test_bounds_reject_without_truncation(self):
        with self.assertRaises(ValueError):
            notes.prepare_document(self.fixture("x" * 8001))
        with self.assertRaises(ValueError):
            notes.prepare_document(self.fixture("😀" * 4001))
        with self.assertRaises(ValueError):
            notes.prepare_document(self.fixture("x\x00y"))
        with self.assertRaises(ValueError):
            notes.prepare_document(self.fixture("x\x7fy"))

    def test_file_copy_and_source_preservation(self):
        with tempfile.TemporaryDirectory(prefix="aibos-style-note-fixture-") as directory:
            root = Path(directory)
            original = b'\xef\xbb\xbf' + notes.encode_document(self.fixture("A person waves.")).encode("utf-8")
            source = root / "source.json"
            source.write_bytes(original)
            notes.prepare_file(source, root / "prepared")
            self.assertEqual(source.read_bytes(), original)
            self.assertEqual((root / "prepared/ai-styles.original.json").read_bytes(), original)
            prepared = (root / "prepared/ai-styles.prepared.json").read_bytes()
            with self.assertRaises(FileExistsError):
                notes.prepare_file(source, root / "prepared")
            self.assertEqual((root / "prepared/ai-styles.prepared.json").read_bytes(), prepared)
            source.write_text('{"Version":2,"VideoStyles":[]}', encoding="utf-8")
            with self.assertRaises(ValueError):
                notes.prepare_file(source, root / "refused")
            self.assertFalse((root / "refused").exists())


if __name__ == "__main__":
    unittest.main()
