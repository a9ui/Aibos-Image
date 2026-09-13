"""Prepare a separate, lossless copy of legacy video styles. Never edit the source."""

from __future__ import annotations

import argparse
from copy import deepcopy
from decimal import Decimal
from hashlib import sha256
import json
from pathlib import Path
import re
import unicodedata


MAX_BYTES = 4 * 1024 * 1024
MAX_TEXT_UNITS = 8000
SEPARATOR = re.compile(
    r"^[ \t]*▼▼▼[ \t]*使用時はこの行から末尾まで全削除[ \t]*[｜|][ \t]*日本語訳"
    r"[ \t]*▼▼▼[ \t]*(?:\r\n|\n|\r|$)",
    re.MULTILINE,
)


def digest(data: bytes) -> str:
    return sha256(data).hexdigest()


def unique_object(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON members are unsupported; source is unchanged.")
        result[key] = value
    return result


def reject_constant(value: str) -> None:
    raise ValueError("Non-finite JSON numbers are unsupported.")


def read_document(raw: bytes) -> dict:
    if len(raw) > MAX_BYTES:
        raise ValueError("The style document exceeds the 4 MiB read bound.")
    document = json.loads(
        raw.decode("utf-8-sig"), object_pairs_hook=unique_object,
        parse_float=Decimal, parse_constant=reject_constant,
    )
    if not isinstance(document, dict) or type(document.get("Version")) is not int or document["Version"] != 1:
        raise ValueError("Only version 1 style documents are supported.")
    if not isinstance(document.get("VideoStyles"), list):
        raise ValueError("VideoStyles must be an array.")

    def depth_check(value: object, depth: int = 0) -> None:
        if depth > 64:
            raise ValueError("JSON exceeds the supported nesting depth.")
        if isinstance(value, dict):
            for child in value.values():
                depth_check(child, depth + 1)
        elif isinstance(value, list):
            for child in value:
                depth_check(child, depth + 1)

    depth_check(document)
    return document


def encode_document(value: object, depth: int = 0) -> str:
    """Preserve unknown numeric values without a binary floating-point round trip."""
    if isinstance(value, Decimal):
        if not value.is_finite():
            raise ValueError("Non-finite numeric value.")
        return str(value)
    if isinstance(value, dict):
        if not value:
            return "{}"
        lines = ["  " * (depth + 1) + json.dumps(key, ensure_ascii=False) + ": "
                 + encode_document(child, depth + 1) for key, child in value.items()]
        return "{\n" + ",\n".join(lines) + "\n" + "  " * depth + "}"
    if isinstance(value, list):
        if not value:
            return "[]"
        lines = ["  " * (depth + 1) + encode_document(child, depth + 1) for child in value]
        return "[\n" + ",\n".join(lines) + "\n" + "  " * depth + "]"
    return json.dumps(value, ensure_ascii=False, allow_nan=False)


def split_note(prompt: str) -> tuple[str, str, str]:
    matches = list(SEPARATOR.finditer(prompt))
    if len(matches) > 1:
        raise ValueError("Multiple translation separators require manual review.")
    if not matches:
        if "使用時はこの行から末尾まで全削除" in prompt:
            raise ValueError("An unrecognized translation separator requires manual review.")
        return prompt, "", ""
    match = matches[0]
    return prompt[:match.start()], match.group(), prompt[match.end():]


def prepare_document(source: dict) -> tuple[dict, dict]:
    prepared = deepcopy(source)
    rows = []
    for index, style in enumerate(prepared["VideoStyles"], 1):
        if not isinstance(style, dict) or not isinstance(style.get("Prompt"), str):
            raise ValueError(f"Style {index} has an unsupported prompt value.")
        prompt = style["Prompt"]
        if len(prompt.encode("utf-16-le")) // 2 > MAX_TEXT_UNITS:
            raise ValueError(f"Style {index} exceeds the native prompt bound; no truncation is allowed.")
        if any(unicodedata.category(char) == "Cc" and char not in "\r\n\t" for char in prompt):
            raise ValueError(f"Style {index} has unsupported control characters.")
        program = style.get("InstructionProgram")
        if program is not None:
            if not isinstance(program, dict) or type(program.get("Version", 1)) is not int or program.get("Version", 1) != 1:
                raise ValueError(f"Style {index} has an unsupported instruction program.")
            rows.append({"index": index, "status": "existing-program-unchanged"})
            continue
        body, separator, note = split_note(prompt)
        proof = {
            "Kind": "split-japanese-note-v1",
            "Prompt": prompt,
            "Sha256": digest(prompt.encode("utf-8")),
            "Separator": separator,
        }
        style["Prompt"] = body
        style["InstructionProgram"] = {
            "Version": 1, "Enabled": False,
            "Template": "", "PhotorealTemplate": "",
            "BaseH3Template": body, "PhotorealBaseH3Template": "",
            "Description": note, "SourceRules": False,
            "ImageChoices": False, "ActionPlot": False,
            "PhysicalContinuity": False, "Options": {},
            "OriginalStyleText": proof,
        }
        if body + separator + note != prompt:
            raise ValueError(f"Style {index} failed exact text reconstruction.")
        unchanged = deepcopy(style)
        unchanged["Prompt"] = prompt
        if "InstructionProgram" in source["VideoStyles"][index - 1]:
            unchanged["InstructionProgram"] = None
        else:
            del unchanged["InstructionProgram"]
        if unchanged != source["VideoStyles"][index - 1]:
            raise ValueError(f"Style {index} changed a field outside the allowed split.")
        rows.append({
            "index": index,
            "status": "note-separated" if separator else "original-retained",
            "originalPromptSha256": proof["Sha256"],
            "exactReconstruction": True,
        })
    report = {
        "schemaVersion": 1, "styleCount": len(rows),
        "notesSeparated": sum(row["status"] == "note-separated" for row in rows),
        "existingProgramsUnchanged": sum(row["status"] == "existing-program-unchanged" for row in rows),
        "contentRewritten": False, "optionsInferred": False, "stylesMerged": False,
        "inferenceEnabled": False, "rows": rows,
    }
    return prepared, report


def merge_source_variants(source: dict, prepared: dict, report: dict) -> None:
    """Pair explicitly named legacy variants without interpreting their bodies."""
    pattern = re.compile(r"^(?P<prefix>(?:.*[_ -])?)(?P<kind>Anime|Photo)-(?P<stem>.+)$", re.IGNORECASE)
    originals = source["VideoStyles"]
    names = {}
    groups = {}
    for index, style in enumerate(originals):
        name = style.get("Name")
        if not isinstance(name, str) or not name or name.casefold() in names:
            raise ValueError("Style names must be nonempty and unambiguous.")
        names[name.casefold()] = index
        match = pattern.fullmatch(name)
        if match:
            family = match["prefix"] + match["stem"]
            members = groups.setdefault(family.casefold(), {})
            kind = match["kind"].lower()
            if kind in members:
                raise ValueError("A source variant is duplicated; no styles were merged.")
            members[kind] = (index, family)

    replacements, consumed, renamed, skipped = {}, set(), {}, []
    for key, pair in groups.items():
        if set(pair) != {"anime", "photo"}:
            continue
        ai, name = pair["anime"]
        pi, _ = pair["photo"]
        if key in names:
            raise ValueError("A merged style name already exists; no styles were merged.")
        # Authored programs may have independent decisions. Keep them intact.
        if originals[ai].get("InstructionProgram") is not None or originals[pi].get("InstructionProgram") is not None:
            skipped.append({"indices": [ai + 1, pi + 1], "reason": "existing-program"})
            continue
        def settings(index):
            return {k: v for k, v in originals[index].items() if k not in ("Name", "Prompt", "InstructionProgram")}
        if settings(ai) != settings(pi):
            skipped.append({"indices": [ai + 1, pi + 1], "reason": "different-settings"})
            continue
        anime, photo = prepared["VideoStyles"][ai], prepared["VideoStyles"][pi]
        merged = deepcopy(anime)
        merged["Name"] = name
        program = merged["InstructionProgram"]
        program["UseSourceVariants"] = True
        program["PhotorealBaseH3Template"] = photo["Prompt"]
        program["PhotorealDescription"] = photo["InstructionProgram"]["Description"]
        program["OriginalStyleVariants"] = {
            "Kind": "paired-anime-photo-v1",
            "Anime": deepcopy(originals[ai]), "Photo": deepcopy(originals[pi]),
        }
        for kind, index, body, description in [
            ("Anime", ai, program["BaseH3Template"], program["Description"]),
            ("Photo", pi, program["PhotorealBaseH3Template"], program["PhotorealDescription"]),
        ]:
            _, separator, _ = split_note(originals[index]["Prompt"])
            if body + separator + description != originals[index]["Prompt"]:
                raise ValueError("A source variant did not reconstruct exactly.")
            if program["OriginalStyleVariants"][kind] != originals[index]:
                raise ValueError("A source style snapshot changed unexpectedly.")
            renamed[originals[index]["Name"].casefold()] = name
        replacements[min(ai, pi)] = merged
        consumed.update((ai, pi))
    prepared["VideoStyles"] = [replacements[i] if i in replacements else style
        for i, style in enumerate(prepared["VideoStyles"]) if i in replacements or i not in consumed]
    selected = prepared.get("SelectedVideoStyleName")
    if isinstance(selected, str) and selected.casefold() in renamed:
        prepared["SelectedVideoStyleName"] = renamed[selected.casefold()]
    report.update({"sourceStyleCount": report["styleCount"], "styleCount": len(prepared["VideoStyles"]),
        "stylesMerged": bool(replacements), "mergedPairs": len(replacements), "skippedPairs": skipped,
        "sourceVariantOriginalsRetained": True})


def prepare_file(source_path: Path, output_directory: Path, merge_variants: bool = False) -> dict:
    source_path = source_path.resolve(strict=True)
    with source_path.open("rb") as handle:
        raw = handle.read(MAX_BYTES + 1)
    source = read_document(raw)
    prepared, report = prepare_document(source)
    if merge_variants:
        merge_source_variants(source, prepared, report)
    encoded = (encode_document(prepared) + "\n").encode("utf-8")
    if len(encoded) > MAX_BYTES:
        raise ValueError("The prepared document exceeds the native 4 MiB storage bound.")
    if read_document(encoded) != prepared:
        raise ValueError("Prepared JSON did not round-trip exactly.")
    report.update({"sourceSha256": digest(raw), "preparedSha256": digest(encoded),
                   "sourceBytes": len(raw), "preparedBytes": len(encoded)})
    # Fail before writing any output when the caller chose an existing location.
    output_directory.mkdir(parents=True, exist_ok=False)
    outputs = {
        "ai-styles.original.json": raw,
        "ai-styles.prepared.json": encoded,
        "preparation-report.json": (json.dumps(report, indent=2) + "\n").encode("utf-8"),
    }
    for name, data in outputs.items():
        with (output_directory / name).open("xb") as handle:
            handle.write(data)
    return {key: value for key, value in report.items() if key != "rows"}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output_directory", type=Path, help="A new private directory, outside the repository.")
    parser.add_argument("--merge-source-variants", action="store_true", help="Pair matching Anime-/Photo- legacy styles, retaining both exact originals.")
    args = parser.parse_args()
    repository = Path(__file__).resolve().parent.parent
    output = args.output_directory.resolve()
    if output == repository or repository in output.parents:
        parser.error("Private style copies must be kept outside the repository.")
    try:
        result = prepare_file(args.source, output, args.merge_source_variants)
    except (OSError, ValueError, UnicodeError, RecursionError) as error:
        parser.exit(1, f"Preparation failed: {error}\n")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
