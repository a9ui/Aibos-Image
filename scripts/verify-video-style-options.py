"""Synthetic preservation and refusal checks for reviewed inline options."""

from copy import deepcopy
from decimal import Decimal
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("style_options", Path(__file__).with_name("prepare-video-style-options.py"))
options = importlib.util.module_from_spec(spec)
spec.loader.exec_module(options)


class StyleOptionsChecks(unittest.TestCase):
    def fixture(self):
        body = "[Shot 1] The camera stays still.\r\nShe waves, turns, and smiles.\r\n"
        program = dict(Version=1, Enabled=False, UseSourceVariants=True, BaseH3Template=body,
                       PhotorealBaseH3Template=body.replace("waves", "nods"), Template="", PhotorealTemplate="",
                       Description="日本語の説明", PhotorealDescription="実写の説明", Options={},
                       OriginalStyleText={"Prompt": "exact archived original"}, Future=Decimal("1.234567890123456789"))
        source = dict(Version=1, SelectedVideoStyleName="Example", VideoStyles=[dict(Name="Example", Prompt=body, InstructionProgram=program, Unknown=[1,2])], Extra=True)
        variants = {}
        for kind, field in [("anime", "BaseH3Template"), ("photoreal", "PhotorealBaseH3Template")]:
            text = program[field]
            variants[kind] = dict(sha256=options.notes.digest(text.encode()), spans=[
                dict(start=text.index("The camera"), length=len("The camera stays still."), category="camera", label="カメラワーク"),
                dict(start=text.index("turns"), length=5, category="action", label="動作")])
        return source, dict(version=1, styles=[dict(name="Example", variants=variants)])

    def test_preserves_original_bodies_notes_unknowns_and_defaults(self):
        source, manifest = self.fixture()
        untouched = deepcopy(source)
        prepared, report = options.prepare(source, manifest)
        self.assertEqual(source, untouched)
        self.assertEqual(report["preparedStyles"], 1)
        program = prepared["VideoStyles"][0]["InstructionProgram"]
        self.assertTrue(program["AnnotatedH3"])
        self.assertIn(r"\[Shot 1\]", program["Template"])
        self.assertIn("[turns]", program["Template"])
        for field in ["BaseH3Template", "PhotorealBaseH3Template", "Description", "PhotorealDescription", "Future", "OriginalStyleText"]:
            self.assertEqual(program[field], source["VideoStyles"][0]["InstructionProgram"][field])
        self.assertEqual(prepared["VideoStyles"][0]["Prompt"], source["VideoStyles"][0]["Prompt"])
        self.assertTrue(all(o["Mode"] == "on" and o["ChoiceIndex"] == 0 for o in program["Options"].values()))
        self.assertEqual(options.notes.read_document(options.notes.encode_document(prepared).encode()), prepared)

    def test_rejects_stale_review_and_overlapping_spans(self):
        source, manifest = self.fixture()
        manifest["styles"][0]["variants"]["anime"]["sha256"] = "stale"
        with self.assertRaises(ValueError): options.prepare(source, manifest)
        source, manifest = self.fixture()
        spans = manifest["styles"][0]["variants"]["anime"]["spans"]
        spans.append(deepcopy(spans[0]))
        with self.assertRaises(ValueError): options.prepare(source, manifest)

    def test_requires_both_variants_and_leaves_authored_programs_alone(self):
        source, manifest = self.fixture()
        del manifest["styles"][0]["variants"]["photoreal"]
        with self.assertRaises(ValueError): options.prepare(source, manifest)
        source, manifest = self.fixture()
        source["VideoStyles"][0]["InstructionProgram"]["Enabled"] = True
        with self.assertRaises(ValueError): options.prepare(source, manifest)

    def test_rejects_ambiguous_or_oversized_single_option(self):
        for body in ["a / b", "a" * 1001, " text "]:
            selection = dict(sha256=options.notes.digest(body.encode()), spans=[dict(start=0,length=len(body),category="action")])
            with self.assertRaises(ValueError): options.annotate(body, selection)

    def test_camera_defaults_off_when_no_original_camera_span(self):
        body = "A landscape."
        template, choices = options.annotate(body, dict(sha256=options.notes.digest(body.encode()), spans=[dict(start=0,length=0,category="camera")]))
        self.assertTrue(template.endswith(body))
        self.assertEqual(next(iter(choices.values()))["Mode"], "off")
        self.assertEqual(next(iter(choices.values()))["ChoiceLabels"][0], "固定")

    def test_exclusive_metadata_does_not_choose_on_import(self):
        body = "Ending one.\nEnding two."
        spans = [dict(start=0,length=11,category="ending",group="ending"), dict(start=12,length=11,category="ending",group="ending")]
        _, choices = options.annotate(body, dict(sha256=options.notes.digest(body.encode()),spans=spans))
        self.assertEqual([v["Mode"] for v in choices.values()], ["on", "on"])


if __name__ == "__main__":
    unittest.main()
