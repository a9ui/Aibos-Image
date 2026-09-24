"""Annotate reviewed prompt spans in a separate private style copy, without rewriting text."""

from copy import deepcopy
from hashlib import sha256
import argparse
import importlib.util
import json
from pathlib import Path

spec = importlib.util.spec_from_file_location("style_notes", Path(__file__).with_name("prepare-video-style-notes.py"))
notes = importlib.util.module_from_spec(spec)
spec.loader.exec_module(notes)
REPOSITORY = Path(__file__).resolve().parent.parent
CAMERAS = json.loads((REPOSITORY / "local-native/PhotoViewer.Wpf/VideoCameraChoices.json").read_text(encoding="utf-8"))
CATEGORIES = {"camera", "action", "expression", "viewpoint", "ending", "sound", "detail"}


def escape(text):
    return "".join(("\\" if char in "\\[]{}［］｛｝/" else "") + char for char in text)


def annotate(body, selection):
    if selection.get("sha256") != notes.digest(body.encode("utf-8")):
        raise ValueError("Reviewed prompt changed; review the current text before preparing it.")
    spans = selection.get("spans")
    if not isinstance(spans, list) or not 1 <= len(spans) <= 128:
        raise ValueError("Expected 1 to 128 reviewed spans.")
    cursor, chunks, reconstruction, options = 0, [], [], {}
    for span in sorted(spans, key=lambda s: s["start"]):
        start, length = span["start"], span["length"]
        if type(start) is not int or type(length) is not int or start < cursor or length < 0 or start + length > len(body):
            raise ValueError("Reviewed spans overlap or exceed the original prompt.")
        category, label, group = span["category"], span.get("label", ""), span.get("group", "")
        if category not in CATEGORIES or not isinstance(label, str) or not isinstance(group, str) or max(len(label), len(group)) > 120:
            raise ValueError("Invalid reviewed option metadata.")
        original = body[start:start + length]
        if original != original.strip() or (not original and category != "camera") or " / " in original or " ／ " in original:
            raise ValueError("Span cannot be represented losslessly by one manual option.")
        choices = [original] if original else []
        labels = []
        if category == "camera":
            choices.extend(item["text"] for item in CAMERAS)
            labels = (["元のカメラ指示"] if original else []) + [item["label"] for item in CAMERAS]
        value = " / ".join(choices)
        key = "[" + value + "]"
        option = {"Mode": "on" if original else "off", "DefaultOn": bool(original), "ChoiceIndex": 0,
                  "Label": label, "Category": category, "Group": group, "ChoiceLabels": labels}
        if key in options and options[key] != option:
            raise ValueError("Identical option text has conflicting metadata.")
        if len(value.encode("utf-16-le")) // 2 > 1000:
            raise ValueError("One option exceeds the native 1000-character bound.")
        options[key] = option
        chunks.extend([escape(body[cursor:start]), "[" + " / ".join(map(escape, choices)) + "]"])
        reconstruction.extend([body[cursor:start], original])
        cursor = start + length
    chunks.append(escape(body[cursor:]))
    reconstruction.append(body[cursor:])
    template = "".join(chunks)
    if "".join(reconstruction) != body or len(template.encode("utf-16-le")) // 2 > 8000:
        raise ValueError("Exact reconstruction or native template bound failed.")
    return template, options


def prepare(source, manifest):
    if manifest.get("version") != 1 or not isinstance(manifest.get("styles"), list):
        raise ValueError("Unsupported reviewed-span manifest.")
    prepared = deepcopy(source)
    names = [s.get("Name") for s in prepared["VideoStyles"]]
    if any(not isinstance(n, str) for n in names) or len({n.casefold() for n in names}) != len(names):
        raise ValueError("Style names are ambiguous.")
    reviews = manifest["styles"]
    if len({r["name"] for r in reviews}) != len(reviews):
        raise ValueError("Duplicate reviewed style.")
    rows = []
    for review in reviews:
        if review["name"] not in names:
            raise ValueError("Reviewed style no longer exists.")
        index = names.index(review["name"])
        style = prepared["VideoStyles"][index]
        program = style.get("InstructionProgram")
        if not isinstance(program, dict) or program.get("Version", 1) != 1 or program.get("Enabled") or program.get("Options"):
            raise ValueError("Only unannotated version 1 programs can be prepared; keep authored choices intact.")
        if program.get("Template") or program.get("PhotorealTemplate") or any(program.get(k) for k in ("SourceRules", "ImageChoices", "ActionPlot", "PhysicalContinuity")):
            raise ValueError("Existing authoring or automatic settings require individual review.")
        variants = review["variants"]
        expected = {"anime", "photoreal"} if program.get("UseSourceVariants") else {"anime"}
        if set(variants) != expected:
            raise ValueError("Review every source variant without dropping a branch.")
        options = {}
        counts = {}
        for kind, selection in variants.items():
            field = "PhotorealBaseH3Template" if kind == "photoreal" else "BaseH3Template"
            body = program.get(field)
            if not isinstance(body, str) or not body:
                raise ValueError("Missing original body.")
            template, branch_options = annotate(body, selection)
            program["PhotorealTemplate" if kind == "photoreal" else "Template"] = template
            for key, value in branch_options.items():
                if key in options and options[key] != value:
                    raise ValueError("Shared option text has conflicting variant settings.")
                options[key] = value
            counts[kind] = len(selection["spans"])
        if len(options) > 128:
            raise ValueError("Style option count exceeds the native bound.")
        program.update(Enabled=True, AnnotatedH3=True, Options=options)
        original_program = source["VideoStyles"][index]["InstructionProgram"]
        for field, value in original_program.items():
            if field not in {"Enabled", "AnnotatedH3", "Template", "PhotorealTemplate", "Options"} and program[field] != value:
                raise ValueError("Original or unknown field changed.")
        rows.append({"index": index + 1, "optionsByVariant": counts, "exactDefaultReconstruction": True})
    return prepared, {"version": 1, "preparedStyles": len(rows), "contentRewritten": False, "rows": rows}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("manifest", type=Path, help="Private reviewed character offsets and body hashes.")
    parser.add_argument("output_directory", type=Path)
    args = parser.parse_args()
    output = args.output_directory.resolve()
    if output == REPOSITORY or REPOSITORY in output.parents:
        parser.error("Private style copies must be kept outside the repository.")
    try:
        with args.source.open("rb") as handle:
            raw = handle.read(notes.MAX_BYTES + 1)
        with args.manifest.open("rb") as handle:
            manifest_raw = handle.read(512 * 1024 + 1)
        if len(manifest_raw) > 512 * 1024:
            raise ValueError("Review manifest exceeds its bound.")
        manifest = json.loads(manifest_raw.decode("utf-8-sig"), object_pairs_hook=notes.unique_object)
        if manifest.get("sourceSha256") != notes.digest(raw):
            raise ValueError("The style library changed after review.")
        source = notes.read_document(raw)
        prepared, report = prepare(source, manifest)
        encoded = (notes.encode_document(prepared) + "\n").encode("utf-8")
        if notes.read_document(encoded) != prepared:
            raise ValueError("Prepared document did not round-trip exactly.")
        report.update(sourceSha256=notes.digest(raw), preparedSha256=notes.digest(encoded), sourceBytes=len(raw), preparedBytes=len(encoded))
        output.mkdir(parents=True, exist_ok=False)
        for name, data in {"ai-styles.original.json": raw, "ai-styles.prepared.json": encoded,
                           "reviewed-spans.json": manifest_raw, "preparation-report.json": (json.dumps(report, indent=2) + "\n").encode()}.items():
            with (output / name).open("xb") as handle:
                handle.write(data)
        print(json.dumps(report, indent=2))
    except (ValueError, KeyError, TypeError, UnicodeError, OSError, RecursionError) as error:
        parser.exit(1, f"Preparation failed: {error}\n")


if __name__ == "__main__":
    main()
