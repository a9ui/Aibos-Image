#!/usr/bin/env python3
"""Independently verify the checked-in M2 disposition overlay."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from collections import Counter
from pathlib import Path


M1_CUTOFF = "M1-20260723T172645Z"
M1_MANIFEST_SHA256 = "d265e7559f42784121d464843398b59221158a546fe8fdd209e1f9d421653b09"
M1_SUMMARY_SHA256 = "22385ac029cf37159abb6cc7f8059118d1ee2aef649013ffd33944c0c9fe9699"
M1_SOURCE_STATE_SHA256 = "1889a84bc760a649762cf35bb3998c0f56db8bec66e465dffec30e3094556f58"
AIBOS_EVIDENCE_COMMIT = "de955891932540aa275de701a205b2fce668b478"
AIBOS_EVIDENCE_TREE = "0504e7a9c160692910185e1eaf51fbf4d3020b33"
H25_EVIDENCE_COMMIT = "0d451cadfe47433fdb45f00bc78168965955fe2d"
H25_EVIDENCE_TREE = "025503c573661d8d8a474268d24cb2596a453c33"
AIBOS_SEMANTIC_BINDINGS = {
    "M1A-000489": (
        "local-native/PhotoViewer.Wpf/App.xaml",
        "7c366a4b849040c9f07dfe720e9b72b8a445031a",
    ),
    "M1A-000490": (
        "local-native/PhotoViewer.Wpf/MainWindow.xaml",
        "228b5897b60bf12031f53297286e005658e28b94",
    ),
    "M1A-000497": (
        "local-native/PhotoViewer.Wpf/MainWindow.xaml.cs",
        "92809cb1fc46957eef265bd268e87c862fb3e33e",
    ),
    "M1A-000499": (
        "local-native/PhotoViewer.Wpf/App.xaml.cs",
        "fc2357a7092c2699061c6d889bca5545094b51f1",
    ),
}
TERMINAL = {
    "ADOPT_BASELINE",
    "ADOPT_FOUNDATION",
    "KEEP_H25",
    "MERGED_H25",
    "DEFER_M6",
    "CLOSE_SUPERSEDED",
    "HISTORICAL_PROVENANCE",
    "REJECT",
}
TRANSIENT = {"PENDING_M2", "UNKNOWN", "NOT_VERIFIED", "NEEDS_OWNER"}
ROW_FIELDS = {
    "m1_cutoff_id",
    "m1_manifest_sha256",
    "asset_id",
    "m1_owner",
    "m2_disposition",
    "evidence_sha_or_issue",
    "target_semantic_unit",
    "decision_reason",
}
OVERLAY_FIELDS = {
    "overlayVersion",
    "hashDomain",
    "m1CutoffId",
    "m1ManifestSha256",
    "m1SourceStateSha256",
    "aibosEvidenceCommit",
    "aibosEvidenceTree",
    "h25EvidenceCommit",
    "h25EvidenceTree",
    "items",
}
SUMMARY_FIELDS = {
    "summaryVersion",
    "hashDomain",
    "m1CutoffId",
    "m1ManifestSha256",
    "overlaySha256",
    "rowCount",
    "terminalCount",
    "transientCount",
    "duplicateAssetIds",
    "orphanRows",
    "missingAssets",
    "privateSurfaceFindings",
    "dispositionCounts",
    "ownerCounts",
    "targetSemanticUnitCounts",
    "su008PublicEvidenceCounts",
    "verification",
}
PRIVATE_PATTERNS = [
    re.compile(r"[A-Z]:[\\/]", re.IGNORECASE),
    re.compile(r"(?:^|[\"'])/(?:Users|home)/", re.IGNORECASE),
    re.compile(r"Desktop[\\/]+Tools", re.IGNORECASE),
    re.compile(r"https://chatgpt\.com/g/g-p-", re.IGNORECASE),
    re.compile(r"(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{20,}", re.IGNORECASE),
    re.compile(r"github_pat_[A-Za-z0-9_]+", re.IGNORECASE),
    re.compile(r"(?<![A-Za-z0-9])sk-(?:proj-)?[A-Za-z0-9_-]{20,}", re.IGNORECASE),
    re.compile(r"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----"),
    re.compile(r"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", re.IGNORECASE),
]


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def read_json(path: Path) -> tuple[bytes, str, object]:
    raw = path.read_bytes()
    text = raw.decode("utf-8", errors="strict")
    return raw, text, json.loads(text)


def assert_count_map(actual: Counter[str], expected: dict[str, int], name: str) -> None:
    normalized = {key: value for key, value in actual.items() if value or key in expected}
    if normalized != expected:
        raise ValueError(f"{name} counts do not match: {normalized!r} != {expected!r}")


def parse_args() -> argparse.Namespace:
    repo_root = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--manifest",
        type=Path,
        default=repo_root / "docs" / "legacy-ledger" / "manifest-v3.json",
    )
    parser.add_argument(
        "--manifest-summary",
        type=Path,
        default=repo_root / "docs" / "legacy-ledger" / "summary-v3.json",
    )
    parser.add_argument(
        "--overlay",
        type=Path,
        default=repo_root / "docs" / "legacy-disposition" / "overlay-v1.json",
    )
    parser.add_argument(
        "--overlay-summary",
        type=Path,
        default=repo_root / "docs" / "legacy-disposition" / "summary-v1.json",
    )
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    manifest_raw, _, manifest = read_json(args.manifest)
    manifest_summary_raw, _, manifest_summary = read_json(args.manifest_summary)
    overlay_raw, overlay_text, overlay = read_json(args.overlay)
    _, overlay_summary_text, overlay_summary = read_json(args.overlay_summary)

    manifest_sha = sha256(manifest_raw)
    manifest_summary_sha = sha256(manifest_summary_raw)
    overlay_sha = sha256(overlay_raw)
    if (
        manifest_sha != M1_MANIFEST_SHA256
        or manifest_summary_sha != M1_SUMMARY_SHA256
        or manifest["cutoffId"] != M1_CUTOFF
        or manifest["sourceStateSha256"] != M1_SOURCE_STATE_SHA256
        or manifest["manifestVersion"] != 3
        or manifest["hashDomain"] != "aibos-m1-ledger/v3"
    ):
        raise ValueError("immutable M1 identity does not match the accepted cutoff")
    if (
        manifest_sha != manifest_summary["manifestSha256"]
        or manifest_summary["cutoffId"] != M1_CUTOFF
        or manifest_summary["sourceStateSha256"] != M1_SOURCE_STATE_SHA256
    ):
        raise ValueError("the immutable M1 manifest no longer matches its summary")
    if overlay["m1ManifestSha256"] != manifest_sha:
        raise ValueError("overlay is not bound to the immutable M1 manifest")
    if overlay_summary["m1ManifestSha256"] != manifest_sha:
        raise ValueError("overlay summary is not bound to the immutable M1 manifest")
    if overlay["m1CutoffId"] != manifest["cutoffId"]:
        raise ValueError("overlay is not bound to the immutable M1 cutoff")
    if overlay_summary["m1CutoffId"] != manifest["cutoffId"]:
        raise ValueError("overlay summary is not bound to the immutable M1 cutoff")
    if overlay["overlayVersion"] != 1 or overlay["hashDomain"] != "aibos-m2-disposition/v1":
        raise ValueError("unsupported overlay version or hash domain")
    if (
        overlay_summary["summaryVersion"] != 1
        or overlay_summary["hashDomain"] != "aibos-m2-disposition-summary/v1"
    ):
        raise ValueError("unsupported overlay summary version or hash domain")
    if overlay_summary["overlaySha256"] != overlay_sha:
        raise ValueError("overlay SHA-256 does not match its summary")
    if set(overlay) != OVERLAY_FIELDS or set(overlay_summary) != SUMMARY_FIELDS:
        raise ValueError("M2 artifact top-level schema has missing or unexpected fields")
    if (
        overlay["m1SourceStateSha256"] != M1_SOURCE_STATE_SHA256
        or overlay["aibosEvidenceCommit"] != AIBOS_EVIDENCE_COMMIT
        or overlay["aibosEvidenceTree"] != AIBOS_EVIDENCE_TREE
        or overlay["h25EvidenceCommit"] != H25_EVIDENCE_COMMIT
        or overlay["h25EvidenceTree"] != H25_EVIDENCE_TREE
    ):
        raise ValueError("M2 overlay evidence identity does not match the accepted exact pair")

    public_entries = list(args.overlay.parent.iterdir())
    if any(path.is_symlink() or not path.is_file() for path in public_entries):
        raise ValueError("M2 public directory contains a nested or redirected entry")
    public_files = sorted(public_entries)
    if {path.name for path in public_files} != {"README.md", "overlay-v1.json", "summary-v1.json"}:
        raise ValueError("M2 public directory contains a missing or unexpected file")
    for public_file in public_files:
        public_text = public_file.read_bytes().decode("utf-8", errors="strict")
        for pattern in PRIVATE_PATTERNS:
            if pattern.search(public_text):
                raise ValueError("forbidden private-surface pattern in M2 artifacts")

    manifest_items = manifest["items"]
    overlay_items = overlay["items"]
    if len(manifest_items) != 507 or len(overlay_items) != 507:
        raise ValueError("M2 must contain exactly 507 immutable M1 assets")
    manifest_by_id = {item["assetId"]: item for item in manifest_items}
    if len(manifest_by_id) != len(manifest_items):
        raise ValueError("immutable M1 manifest contains duplicate asset IDs")

    seen: set[str] = set()
    dispositions: Counter[str] = Counter({name: 0 for name in TERMINAL})
    owners: Counter[str] = Counter()
    targets: Counter[str] = Counter()
    transient_count = 0
    target_pattern = re.compile(r"^(?:M1-SU-0(?:0[1-9]|10)|M3-WPF-PARITY)$")

    for index, row in enumerate(overlay_items):
        if set(row) != ROW_FIELDS:
            raise ValueError("M2 row has missing or unexpected fields")
        asset_id = row["asset_id"]
        if asset_id != manifest_items[index]["assetId"]:
            raise ValueError("M2 rows are not in canonical M1 asset order")
        if asset_id in seen:
            raise ValueError("duplicate M2 asset ID")
        seen.add(asset_id)
        if asset_id not in manifest_by_id:
            raise ValueError("orphan M2 asset ID")
        m1 = manifest_by_id[asset_id]
        if (
            row["m1_cutoff_id"] != manifest["cutoffId"]
            or row["m1_manifest_sha256"] != manifest_sha
            or row["m1_owner"] != m1["ownerSemanticUnit"]
        ):
            raise ValueError("M2 row is not bound to its exact M1 owner")
        disposition = row["m2_disposition"]
        if disposition in TRANSIENT:
            transient_count += 1
        if disposition not in TERMINAL:
            raise ValueError(f"unsupported or non-terminal disposition: {disposition}")
        for field in ("evidence_sha_or_issue", "decision_reason"):
            value = row[field]
            if (
                not isinstance(value, str)
                or not value.strip()
                or len(value) > 512
                or any(ord(character) < 0x20 or ord(character) > 0x7E for character in value)
            ):
                raise ValueError(f"invalid public-safe text in {field}")
        evidence = row["evidence_sha_or_issue"]
        blob_matches = list(
            re.finditer(
                r"AIBOS-BLOB:(?P<sha>[0-9a-f]{40})@(?P<path>[A-Za-z0-9._/-]+)",
                evidence,
            )
        )
        if evidence.count("AIBOS-BLOB:") != len(blob_matches):
            raise ValueError("M2 row contains an unvalidated Aibos blob token")
        expected_binding = AIBOS_SEMANTIC_BINDINGS.get(asset_id)
        if expected_binding is None:
            if blob_matches:
                raise ValueError("M2 row contains unexpected Aibos blob evidence")
        else:
            expected_path, expected_blob = expected_binding
            if (
                disposition != "ADOPT_BASELINE"
                or len(blob_matches) != 1
                or blob_matches[0].group("path") != expected_path
                or blob_matches[0].group("sha") != expected_blob
            ):
                raise ValueError("M2 semantic adoption lacks its exact public blob binding")
        target = row["target_semantic_unit"]
        if not isinstance(target, str) or not target_pattern.fullmatch(target):
            raise ValueError("unsupported target semantic unit")
        dispositions[disposition] += 1
        owners[row["m1_owner"]] += 1
        targets[target] += 1

    duplicate_count = len(overlay_items) - len({row["asset_id"] for row in overlay_items})
    missing_count = len(set(manifest_by_id) - seen)
    orphan_count = len(seen - set(manifest_by_id))
    if duplicate_count or missing_count or orphan_count or transient_count:
        raise ValueError("M2 reconciliation is not terminal and exact")

    expected_scalars = {
        "rowCount": 507,
        "terminalCount": 507,
        "transientCount": 0,
        "duplicateAssetIds": 0,
        "orphanRows": 0,
        "missingAssets": 0,
        "privateSurfaceFindings": 0,
    }
    for key, expected in expected_scalars.items():
        if overlay_summary[key] != expected:
            raise ValueError(f"overlay summary field {key} is not {expected}")
    assert_count_map(dispositions, overlay_summary["dispositionCounts"], "disposition")
    assert_count_map(owners, overlay_summary["ownerCounts"], "owner")
    assert_count_map(targets, overlay_summary["targetSemanticUnitCounts"], "target")

    print(
        json.dumps(
            {
                "ok": True,
                "implementation": "python",
                "cutoffId": manifest["cutoffId"],
                "manifestSha256": manifest_sha,
                "overlaySha256": overlay_sha,
                "rowCount": len(overlay_items),
                "terminalCount": len(overlay_items),
                "transientCount": transient_count,
                "duplicateAssetIds": duplicate_count,
                "orphanRows": orphan_count,
                "missingAssets": missing_count,
                "privateSurfaceFindings": 0,
            },
            indent=2,
            sort_keys=True,
        )
    )


if __name__ == "__main__":
    main()
