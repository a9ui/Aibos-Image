#!/usr/bin/env python3
"""Build the public-safe M2 overlay from the immutable M1 ledger."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import tempfile
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from typing import Any


M1_CUTOFF = "M1-20260723T172645Z"
M1_MANIFEST_SHA256 = "d265e7559f42784121d464843398b59221158a546fe8fdd209e1f9d421653b09"
AIBOS_EVIDENCE_COMMIT = "de955891932540aa275de701a205b2fce668b478"
AIBOS_EVIDENCE_TREE = "0504e7a9c160692910185e1eaf51fbf4d3020b33"
H25_EVIDENCE_COMMIT = "0d451cadfe47433fdb45f00bc78168965955fe2d"
H25_EVIDENCE_TREE = "025503c573661d8d8a474268d24cb2596a453c33"
H25_REPOSITORY = "a9ui/tools-h000025-photoviewer"
AIBOS_REPOSITORY = "a9ui/Aibos-Image"
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
SUPERSEDING_PR = 343
SUPERSEDING_MERGE = "0895ee98744b7edc050e0e1c23c4f7356457b287"
ISSUE_33_AUDIT_COMMENT = 4924370621
ISSUE_33_AUDIT_COMMENT_SHA256 = "88354a5e4b82642d8cea27991df6e8a9375a62be439019cdc15af0e58462367e"
ISSUE_323_EXIT_COMMENT = 5017060499
ISSUE_323_EXIT_COMMENT_SHA256 = "b2f24bca5154a68394e1cd422046f2c38845515193cb9ebb28c92e12482f9fbb"
FOCAL_OPEN_ISSUES = {33, 97, 105, 106, 316, 318, 320, 321, 323, 328, 329}
FOCAL_CLOSED_ISSUES = {330}
FOCAL_OPEN_DRAFT_PULLS = {325, 326}
FOCAL_MERGED_PULLS = {
    331: "310647c291358b40e62b391ab8c49c3044be884f",
}
FOCAL_CLOSED_UNMERGED_PULLS = {24, 29, 96, 134, 147, 152, 154, 156, 158, 160, 317, 319}
REFERENCED_MERGED_PULLS = {
    28: "0a25fda6f4a10f2a01583afcd0eaf18a618e3220",
    133: "ffe9c51d6e98066c772e04758100f6bc5d2de204",
    148: "c81ecbacf7c5e742cef6a4ed7fce5580db1f8846",
    151: "182269cc70644abe962fac14a3637afb99c36c58",
    153: "adc2789bd8f6a20266b922684079c72af5f2c563",
    155: "7b7863b7f64de33d8d9daec45fd7e9da3679aac1",
    157: "c4af4cf59bcee9f4306e2f718c5eed68b7f8f5a3",
    159: "0032b26207fd7173dd58ca4b13dd17f36cce310e",
    213: "8a10adbdb36355640c3abc6106dd72f442515cce",
    322: "5d4460c4126f7f0cc4b34aa3f21f506e178f991c",
    331: "310647c291358b40e62b391ab8c49c3044be884f",
    343: SUPERSEDING_MERGE,
}
DIRECT_REACHABLE_COMMITS = {
    "0e833e92b25efcd973419f73254f0e0ecb263a1c",
    "901c223ad6720995bc5fd09b4baf8b70f94afe90",
}
REQUIRED_REACHABLE_COMMITS = DIRECT_REACHABLE_COMMITS | set(REFERENCED_MERGED_PULLS.values())

TERMINAL = (
    "ADOPT_BASELINE",
    "ADOPT_FOUNDATION",
    "KEEP_H25",
    "MERGED_H25",
    "DEFER_M6",
    "CLOSE_SUPERSEDED",
    "HISTORICAL_PROVENANCE",
    "REJECT",
)


@dataclass(frozen=True)
class Decision:
    disposition: str
    evidence: str
    target: str
    reason: str


def d(disposition: str, evidence: str, target: str, reason: str) -> Decision:
    return Decision(disposition, evidence, target, reason)


def aibos_blob_evidence(asset_id: str) -> str:
    path, blob = AIBOS_SEMANTIC_BINDINGS[asset_id]
    return f"AIBOS-BLOB:{blob}@{path}"


FOCAL: dict[str, Decision] = {
    "M1A-000016": d(
        "KEEP_H25",
        "H25-GH-ISSUE#33@open;H25-GH-COMMENT#4924370621",
        "M1-SU-001",
        "Open H25 responsibility; public audit found no reachable current implementation SHA.",
    ),
    "M1A-000145": d(
        "KEEP_H25",
        "H25-GH-ISSUE#318@open",
        "M1-SU-001",
        "Open H25 security scope retains loopback request-authentication work.",
    ),
    "M1A-000337": d(
        "KEEP_H25",
        "H25-GH-PR#325@head=0b98bfbd50e5d418baaa20064908de4470a41e28;draft=open",
        "M1-SU-001",
        "Open draft remains an H25 Browser responsibility and has no merge evidence.",
    ),
    "M1A-000338": d(
        "KEEP_H25",
        "H25-GH-PR#326@head=bbe01bea22d4cc34837d7636c149329742340b67;draft=open",
        "M1-SU-001",
        "Open draft remains an H25 Browser responsibility and has no merge evidence.",
    ),
    "M1A-000152": d(
        "KEEP_H25",
        "H25-GH-ISSUE#329@open",
        "M1-SU-002",
        "Open Browser session-scope work remains owned by H25.",
    ),
    "M1A-000153": d(
        "MERGED_H25",
        "H25-GH-PR#331@merge=310647c291358b40e62b391ab8c49c3044be884f",
        "M1-SU-002",
        "Closed issue is satisfied by the exact merged H25 performance change.",
    ),
    "M1A-000172": d(
        "HISTORICAL_PROVENANCE",
        "H25-COMMIT:0e833e92b25efcd973419f73254f0e0ecb263a1c;H25-GH-PR#24@closed-unmerged",
        "M1-SU-002",
        "Closed unmerged PR is retained as provenance for its direct main integration.",
    ),
    "M1A-000339": d(
        "MERGED_H25",
        "H25-GH-PR#331@merge=310647c291358b40e62b391ab8c49c3044be884f",
        "M1-SU-002",
        "Pull request is merged into H25 at the exact merge commit.",
    ),
    "M1A-000058": d(
        "KEEP_H25",
        "H25-GH-ISSUE#97@open;H25-GH-PR#213@merge=8a10adbdb36355640c3abc6106dd72f442515cce",
        "M1-SU-003",
        "Only the read-only status slice merged; the broader H25 queue lifecycle remains open.",
    ),
    "M1A-000177": d(
        "CLOSE_SUPERSEDED",
        "H25-GH-PR#28@merge=0a25fda6f4a10f2a01583afcd0eaf18a618e3220",
        "M1-SU-003",
        "Closed unmerged PR is explicitly superseded by the merged preset lane.",
    ),
    "M1A-000240": d(
        "CLOSE_SUPERSEDED",
        "H25-GH-PR#159@merge=0032b26207fd7173dd58ca4b13dd17f36cce310e",
        "M1-SU-003",
        "Closed unmerged PR is explicitly superseded by the merged official lane.",
    ),
    "M1A-000066": d(
        "KEEP_H25",
        "H25-GH-ISSUE#105@open",
        "M1-SU-004",
        "Open destructive bulk-operation policy remains an H25 responsibility.",
    ),
    "M1A-000067": d(
        "KEEP_H25",
        "H25-GH-ISSUE#106@open",
        "M1-SU-004",
        "Open confirmation policy remains coupled to H25 destructive-operation work.",
    ),
    "M1A-000147": d(
        "KEEP_H25",
        "H25-GH-ISSUE#321@open;H25-COMMIT:901c223ad6720995bc5fd09b4baf8b70f94afe90",
        "M1-SU-004",
        "Implementation is reachable but the issue remains open pending H25 acceptance.",
    ),
    "M1A-000200": d(
        "CLOSE_SUPERSEDED",
        "H25-GH-PR#96@closed-unmerged",
        "M1-SU-004",
        "Closed PR explicitly records that a revised direction superseded this lane.",
    ),
    "M1A-000215": d(
        "CLOSE_SUPERSEDED",
        "H25-GH-PR#133@merge=ffe9c51d6e98066c772e04758100f6bc5d2de204",
        "M1-SU-005",
        "Duplicate closed PR is superseded by the exact merged row.",
    ),
    "M1A-000227": d(
        "CLOSE_SUPERSEDED",
        "H25-GH-PR#148@merge=c81ecbacf7c5e742cef6a4ed7fce5580db1f8846",
        "M1-SU-005",
        "Duplicate closed PR is superseded by the exact merged row.",
    ),
    "M1A-000232": d(
        "CLOSE_SUPERSEDED",
        "H25-GH-PR#151@merge=182269cc70644abe962fac14a3637afb99c36c58",
        "M1-SU-005",
        "Duplicate closed PR is superseded by the exact merged row.",
    ),
    "M1A-000234": d(
        "CLOSE_SUPERSEDED",
        "H25-GH-PR#153@merge=adc2789bd8f6a20266b922684079c72af5f2c563",
        "M1-SU-005",
        "Duplicate closed PR is superseded by the exact merged row.",
    ),
    "M1A-000236": d(
        "CLOSE_SUPERSEDED",
        "H25-GH-PR#155@merge=7b7863b7f64de33d8d9daec45fd7e9da3679aac1",
        "M1-SU-005",
        "Duplicate closed PR is superseded by the exact merged row.",
    ),
    "M1A-000238": d(
        "CLOSE_SUPERSEDED",
        "H25-GH-PR#157@merge=c4af4cf59bcee9f4306e2f718c5eed68b7f8f5a3",
        "M1-SU-005",
        "Duplicate closed PR is superseded by the exact merged row.",
    ),
    "M1A-000146": d(
        "KEEP_H25",
        "H25-GH-ISSUE#320@open;H25-COMMIT:5d4460c4126f7f0cc4b34aa3f21f506e178f991c",
        "M1-SU-006",
        "Open cross-runtime reconciliation scope remains owned by H25.",
    ),
    "M1A-000148": d(
        "KEEP_H25",
        "H25-GH-ISSUE#323@open;H25-GH-COMMENT#5017060499",
        "M1-SU-006",
        "Exact exit records no implementation commit or remote branch; requirements stay in H25.",
    ),
    "M1A-000335": d(
        "CLOSE_SUPERSEDED",
        "H25-GH-PR#322@merge=5d4460c4126f7f0cc4b34aa3f21f506e178f991c",
        "M1-SU-006",
        "Closed baseline-sync PR is an ancestor of and superseded by merged PR 322.",
    ),
    "M1A-000144": d(
        "KEEP_H25",
        "H25-GH-ISSUE#316@open",
        "M1-SU-007",
        "Open H25 authoritative-contract review remains unfinished Browser work.",
    ),
    "M1A-000151": d(
        "KEEP_H25",
        "H25-GH-ISSUE#328@open",
        "M1-SU-007",
        "Open non-normative proposal has no public implementation or adoption evidence.",
    ),
    "M1A-000334": d(
        "CLOSE_SUPERSEDED",
        "H25-GH-PR#322@merge=5d4460c4126f7f0cc4b34aa3f21f506e178f991c",
        "M1-SU-007",
        "Closed documentation predecessor is explicitly superseded by merged PR 322.",
    ),
}

LOCAL_REF_KEEP = {
    "M1A-000350",
    "M1A-000354",
    "M1A-000363",
    "M1A-000399",
    "M1A-000404",
    "M1A-000406",
    "M1A-000408",
    "M1A-000433",
    "M1A-000436",
    "M1A-000437",
    "M1A-000454",
    "M1A-000458",
}

SU010_EXPLICIT: dict[str, Decision] = {
    "M1A-000480": d(
        "KEEP_H25",
        "M1-FP:f26fefe9d9dd117cbfc96b53f0c8905a5138b5120d30d1060aa487573486724c;AIBOS-ISSUE#17",
        "M1-SU-010",
        "Browser-state semantics are already present in H25 main; the active stash remains untouched.",
    ),
    "M1A-000481": d(
        "HISTORICAL_PROVENANCE",
        "M1-FP:e420b5b0318413657a780f0574b032d2d8862679773c4619ae1c3da1f7074457;AIBOS-ISSUE#17",
        "M1-SU-010",
        "Worktree registration is retained as recovery provenance without moving or deleting it.",
    ),
    "M1A-000482": d(
        "HISTORICAL_PROVENANCE",
        "M1-FP:5246000a34e535ad969a30f30d2fc59c6463531bc7913e7608ea2f0c3c5d32b9;AIBOS-ISSUE#17",
        "M1-SU-010",
        "Worktree registration is retained as recovery provenance without moving or deleting it.",
    ),
    "M1A-000483": d(
        "HISTORICAL_PROVENANCE",
        "M1-FP:6ecb382504d908ede72a5b36b4f05ac3aa73e6a1abdb6bc66403e26019deb3a1;AIBOS-ISSUE#17",
        "M1-SU-010",
        "Worktree registration is retained as recovery provenance without moving or deleting it.",
    ),
    "M1A-000484": d(
        "HISTORICAL_PROVENANCE",
        "M1-FP:594c1614b673e158c36b768ac945986f0da4704974ecb295950ec005f3fcfcb9;AIBOS-ISSUE#17",
        "M1-SU-010",
        "Worktree registration is retained as recovery provenance without moving or deleting it.",
    ),
    "M1A-000485": d(
        "HISTORICAL_PROVENANCE",
        "M1-FP:e0962d48565307de68a8c1372e0039b6830765e45e1232c380e5ec437ddc7782;AIBOS-ISSUE#17",
        "M1-SU-010",
        "Dirty worktree registration is a container witness; its assets are classified separately.",
    ),
    "M1A-000486": d(
        "HISTORICAL_PROVENANCE",
        "M1-FP:222ff55eaca1e168799222b4be3f36aac72135e9882b5c2bae51bc1cc6dac9a7;AIBOS-ISSUE#17",
        "M1-SU-010",
        "Worktree registration is retained as recovery provenance without moving or deleting it.",
    ),
    "M1A-000487": d(
        "ADOPT_BASELINE",
        f"AIBOS-COMMIT:{AIBOS_EVIDENCE_COMMIT};M1-FP:5fa3321fe90f552866f8569776aa8ff7e409759e711dd2b10a7f4a18dd6205c3",
        "M1-SU-010",
        "Private blob comparison is byte-identical to the exact public Aibos baseline.",
    ),
    "M1A-000488": d(
        "ADOPT_BASELINE",
        f"AIBOS-COMMIT:{AIBOS_EVIDENCE_COMMIT};M1-FP:47e87685c049ec102a5fb95a477da663fe159343231c043495e82bce44cdde2f",
        "M1-SU-010",
        "Private blob comparison is byte-identical to the exact public Aibos baseline.",
    ),
    "M1A-000489": d(
        "ADOPT_BASELINE",
        f"AIBOS-COMMIT:{AIBOS_EVIDENCE_COMMIT};{aibos_blob_evidence('M1A-000489')};"
        "M1-FP:049e4b08280d9a182d5e06c5a11b66c2d1a0cec4b9ab55815e4dfbfff9faef91",
        "M1-SU-010",
        "Exact Aibos baseline semantically supersedes the legacy WPF visual-resource foundation.",
    ),
    "M1A-000490": d(
        "ADOPT_BASELINE",
        f"AIBOS-COMMIT:{AIBOS_EVIDENCE_COMMIT};{aibos_blob_evidence('M1A-000490')};"
        "M1-FP:1ba25f7bf4dacd76e87d4c9fccba30fa91708254548cacb28bc483b972b2701e",
        "M1-SU-010",
        "Exact Aibos baseline supersedes the legacy workbench with functional product dialogs.",
    ),
    "M1A-000491": d(
        "HISTORICAL_PROVENANCE",
        "M1-FP:5f50a0fa1e2d9700324970b6704c1b6f835722b0d3fab47e41837946d0129bb8",
        "M1-SU-010",
        "Non-code visual evidence is retained without publishing or modifying its content.",
    ),
    "M1A-000492": d(
        "HISTORICAL_PROVENANCE",
        "M1-FP:ef4424009c2104de1f89901cf2f3be52b99f199a5104711402384def02ceb8e8",
        "M1-SU-010",
        "Non-code visual evidence is retained without publishing or modifying its content.",
    ),
    "M1A-000493": d(
        "HISTORICAL_PROVENANCE",
        f"H25-COMMIT:{H25_EVIDENCE_COMMIT};M1-FP:e4ddeef1ce2c430132e94d357fc2fe388da96a80acd9fb6b9c8748eb2c5cd727",
        "M1-SU-010",
        "Exact H25 blob is an obsolete pre-.NET10 foundation retained as provenance only.",
    ),
    "M1A-000494": d(
        "HISTORICAL_PROVENANCE",
        "M1-FP:74e001092a3c1ea40fb11591796bf4399f306593d267d522c4a19c2b4f6d510b",
        "M1-SU-010",
        "Non-code visual evidence is retained without publishing or modifying its content.",
    ),
    "M1A-000495": d(
        "HISTORICAL_PROVENANCE",
        "M1-FP:03f88a191c590e1b305ca695fcf66606085d20acd8905dd217c90d86bab91b8a",
        "M1-SU-010",
        "Non-code visual evidence is retained without publishing or modifying its content.",
    ),
    "M1A-000496": d(
        "REJECT",
        "M1-FP:d54448c775c5b45039da1fcc132bd540a365d2c6fd4ae9def358a8e1a713b4a3",
        "M1-SU-010",
        "Generic local build metadata has no product behavior or durable provenance value.",
    ),
    "M1A-000497": d(
        "ADOPT_BASELINE",
        f"AIBOS-COMMIT:{AIBOS_EVIDENCE_COMMIT};{aibos_blob_evidence('M1A-000497')};"
        "M1-FP:5801d9f5689e436952fad2aaf47088470779926d5b0305d339a2bd10368899f7",
        "M1-SU-010",
        "Exact Aibos baseline supersedes the legacy overlay handlers with guarded product routes.",
    ),
    "M1A-000498": d(
        "HISTORICAL_PROVENANCE",
        "M1-FP:d544b34a1857ddfe3da3223ad32a18fe79dfaa9864043ca9910c05b5b6a5f3d0",
        "M1-SU-010",
        "Non-code visual evidence is retained without publishing or modifying its content.",
    ),
    "M1A-000499": d(
        "ADOPT_BASELINE",
        f"AIBOS-COMMIT:{AIBOS_EVIDENCE_COMMIT};{aibos_blob_evidence('M1A-000499')};"
        "M1-FP:2ec2cb91b060ce18b31e07d65fbabc4d305734288519785eb2ccde82de939f15",
        "M1-SU-010",
        "Exact Aibos baseline semantically supersedes the legacy WPF bootstrap glue.",
    ),
    "M1A-000500": d(
        "HISTORICAL_PROVENANCE",
        "M1-FP:d26e7b5bda95030f1dd28416b1114eca9aa4e4f6efa44fe8ae344b0aa476a209",
        "M1-SU-010",
        "Local implementation brief is superseded by the exact current WPF baseline.",
    ),
    "M1A-000501": d(
        "HISTORICAL_PROVENANCE",
        "M1-FP:c680d5c029a7f3f74c87f7a5f543df5ba220e9fa4979471517c2865da5ad8432",
        "M1-SU-010",
        "Local presentation guide is non-runtime historical evidence only.",
    ),
    "M1A-000502": d(
        "HISTORICAL_PROVENANCE",
        "M1-FP:0bad2838b6dca73a9def70a15d5be4526862f9908a86c68d07ee87fa6cb08133",
        "M1-SU-010",
        "Non-code visual evidence is retained without publishing or modifying its content.",
    ),
    "M1A-000503": d(
        "HISTORICAL_PROVENANCE",
        "M1-FP:5cd78d7e58dcc89f090ffd2168266d0d5edb9bb1bc8f15357f6b33e486a7d9a9",
        "M1-SU-010",
        "Non-code visual evidence is retained without publishing or modifying its content.",
    ),
    "M1A-000504": d(
        "HISTORICAL_PROVENANCE",
        "M1-FP:224ed2881092e4cbe87ba6c7b740d3f7929dc6f2c8b31e518f08890b3cfec8bd",
        "M1-SU-010",
        "Early WPF shell documentation is superseded by the exact current implementation.",
    ),
    "M1A-000505": d(
        "HISTORICAL_PROVENANCE",
        "M1-FP:cd50a174f120e01147e2a3aa4061835a4f4922ac8f1e8ba27f110aa91660713c",
        "M1-SU-010",
        "Non-code visual evidence is retained without publishing or modifying its content.",
    ),
    "M1A-000506": d(
        "HISTORICAL_PROVENANCE",
        "M1-FP:e3185f7ca303c26615d57aa4e5da91fa2e96477bb7233ade847a5e2069f5d4a5",
        "M1-SU-010",
        "Non-code visual evidence is retained without publishing or modifying its content.",
    ),
    "M1A-000507": d(
        "HISTORICAL_PROVENANCE",
        "M1-FP:50c475ad52d3e9a5474b16211f1c28bd61d77197b2e22cb9d24657bca0b637b9",
        "M1-SU-010",
        "Static early UI evidence is historical and is not the selected Aibos design source.",
    ),
}


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
    parser.add_argument("--gh-path", type=Path)
    return parser.parse_args()


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def read_json(path: Path) -> tuple[bytes, Any]:
    raw = path.read_bytes()
    return raw, json.loads(raw.decode("utf-8", errors="strict"))


def gh_json(gh: str, endpoint: str) -> Any:
    result = subprocess.run(
        [gh, "api", endpoint],
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
    )
    if result.returncode:
        raise RuntimeError(f"GitHub API read failed for {endpoint}: {result.stderr.strip()}")
    return json.loads(result.stdout)


def gh_collection(gh: str, endpoint: str) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    page = 1
    while True:
        separator = "&" if "?" in endpoint else "?"
        batch = gh_json(gh, f"{endpoint}{separator}per_page=100&page={page}")
        if not isinstance(batch, list):
            raise ValueError(f"GitHub collection is not a list: {endpoint}")
        result.extend(batch)
        if len(batch) < 100:
            return result
        page += 1


def atomic_write_json(path: Path, value: Any) -> bytes:
    path.parent.mkdir(parents=True, exist_ok=True)
    raw = (json.dumps(value, ensure_ascii=True, indent=2) + "\n").encode("ascii")
    handle, temporary = tempfile.mkstemp(prefix=f"{path.name}.", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(handle, "wb") as stream:
            stream.write(raw)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    except BaseException:
        try:
            os.unlink(temporary)
        except FileNotFoundError:
            pass
        raise
    return raw


def get_exact_repo_identity(gh: str, repository: str) -> tuple[str, str]:
    ref = gh_json(gh, f"repos/{repository}/git/ref/heads/main")
    commit_sha = ref["object"]["sha"]
    commit = gh_json(gh, f"repos/{repository}/git/commits/{commit_sha}")
    return commit_sha, commit["tree"]["sha"]


def validate_focal_item(
    item: dict[str, Any],
    issues: dict[int, dict[str, Any]],
    pulls: dict[int, dict[str, Any]],
    reachable_commits: set[str],
) -> None:
    number = int(item["publicNumber"])
    if item["kind"] == "gh_issue":
        issue = issues.get(number)
        if issue is None:
            raise ValueError(f"missing focal public issue {number}")
        if number in FOCAL_OPEN_ISSUES:
            expected_state = "open"
        elif number in FOCAL_CLOSED_ISSUES:
            expected_state = "closed"
        else:
            raise ValueError(f"focal issue lacks an expected state: {number}")
        if issue["state"] != expected_state:
            raise ValueError(f"focal issue state drift for {number}: {issue['state']}")
        return
    if item["kind"] != "gh_pr":
        raise ValueError(f"unexpected focal kind for {item['assetId']}: {item['kind']}")
    pull = pulls.get(number)
    if pull is None:
        raise ValueError(f"missing focal public pull request {number}")
    if pull["head"]["sha"] != item["publicSha"]:
        raise ValueError(f"focal pull request head drift for {number}")
    if number in FOCAL_OPEN_DRAFT_PULLS:
        if pull["state"] != "open" or not pull["draft"] or pull.get("merged_at"):
            raise ValueError(f"focal draft state drift for pull request {number}")
        return
    if number in FOCAL_MERGED_PULLS:
        expected_merge = FOCAL_MERGED_PULLS[number]
        if (
            pull["state"] != "closed"
            or not pull.get("merged_at")
            or pull.get("merge_commit_sha") != expected_merge
            or pull["base"]["ref"] != "main"
            or expected_merge not in reachable_commits
        ):
            raise ValueError(f"focal merged evidence drift for pull request {number}")
        return
    if number in FOCAL_CLOSED_UNMERGED_PULLS:
        if pull["state"] != "closed" or pull.get("merged_at"):
            raise ValueError(f"focal closed-unmerged state drift for pull request {number}")
        return
    raise ValueError(f"focal pull request lacks an expected state: {number}")


def validate_referenced_merged_pulls(
    pulls: dict[int, dict[str, Any]],
    reachable_commits: set[str],
) -> None:
    for number, expected_merge in REFERENCED_MERGED_PULLS.items():
        pull = pulls.get(number)
        if (
            pull is None
            or pull["state"] != "closed"
            or not pull.get("merged_at")
            or pull.get("merge_commit_sha") != expected_merge
            or pull["base"]["ref"] != "main"
            or expected_merge not in reachable_commits
        ):
            raise ValueError(f"referenced merged pull request evidence drifted: {number}")


def validate_decision_evidence(
    item: dict[str, Any],
    decision: Decision,
    issues: dict[int, dict[str, Any]],
    pulls: dict[int, dict[str, Any]],
    reachable_commits: set[str],
    validated_comment_ids: set[int],
    aibos_blobs: dict[str, str],
) -> None:
    evidence = decision.evidence
    merge_matches = list(
        re.finditer(
            r"H25-GH-PR#(?P<number>\d+)@(?:merged;)?merge=(?P<merge>[0-9a-f]{40})"
            r"(?:;head=(?P<head>[0-9a-f]{40}))?",
            evidence,
        )
    )
    head_matches = list(
        re.finditer(
            r"H25-GH-PR#(?P<number>\d+)@head=(?P<head>[0-9a-f]{40});draft=open",
            evidence,
        )
    )
    closed_matches = list(
        re.finditer(r"H25-GH-PR#(?P<number>\d+)@closed-unmerged", evidence)
    )
    if evidence.count("H25-GH-PR#") != len(merge_matches) + len(head_matches) + len(closed_matches):
        raise ValueError(f"unvalidated H25 pull request token for {item['assetId']}")
    for match in merge_matches:
        number = int(match.group("number"))
        pull = pulls.get(number)
        merge_sha = match.group("merge")
        if (
            pull is None
            or pull["state"] != "closed"
            or not pull.get("merged_at")
            or pull.get("merge_commit_sha") != merge_sha
            or pull["base"]["ref"] != "main"
            or merge_sha not in reachable_commits
        ):
            raise ValueError(f"invalid merged pull evidence for {item['assetId']}")
        if match.group("head") and pull["head"]["sha"] != match.group("head"):
            raise ValueError(f"invalid merged pull head evidence for {item['assetId']}")
    for match in head_matches:
        number = int(match.group("number"))
        pull = pulls.get(number)
        if (
            pull is None
            or pull["state"] != "open"
            or not pull["draft"]
            or pull.get("merged_at")
            or pull["head"]["sha"] != match.group("head")
        ):
            raise ValueError(f"invalid draft pull evidence for {item['assetId']}")
    for match in closed_matches:
        pull = pulls.get(int(match.group("number")))
        if pull is None or pull["state"] != "closed" or pull.get("merged_at"):
            raise ValueError(f"invalid closed-unmerged pull evidence for {item['assetId']}")

    issue_matches = list(
        re.finditer(r"H25-GH-ISSUE#(?P<number>\d+)@(?P<state>open|closed)", evidence)
    )
    if evidence.count("H25-GH-ISSUE#") != len(issue_matches):
        raise ValueError(f"unvalidated H25 issue token for {item['assetId']}")
    for match in issue_matches:
        issue = issues.get(int(match.group("number")))
        if issue is None or issue["state"] != match.group("state"):
            raise ValueError(f"invalid H25 issue evidence for {item['assetId']}")

    comment_matches = list(re.finditer(r"H25-GH-COMMENT#(?P<id>\d+)", evidence))
    if evidence.count("H25-GH-COMMENT#") != len(comment_matches):
        raise ValueError(f"unvalidated H25 comment token for {item['assetId']}")
    for match in comment_matches:
        if int(match.group("id")) not in validated_comment_ids:
            raise ValueError(f"invalid H25 comment evidence for {item['assetId']}")

    h25_commit_matches = list(re.finditer(r"H25-COMMIT:(?P<sha>[0-9a-f]{40})", evidence))
    if evidence.count("H25-COMMIT:") != len(h25_commit_matches):
        raise ValueError(f"unvalidated H25 commit token for {item['assetId']}")
    for match in h25_commit_matches:
        if match.group("sha") not in reachable_commits:
            raise ValueError(f"unreachable H25 commit evidence for {item['assetId']}")

    fingerprint_matches = list(re.finditer(r"M1-FP:(?P<sha>[0-9a-f]{64})", evidence))
    if evidence.count("M1-FP:") != len(fingerprint_matches):
        raise ValueError(f"unvalidated M1 fingerprint token for {item['assetId']}")
    for match in fingerprint_matches:
        if match.group("sha") != item["fingerprintSha256"]:
            raise ValueError(f"wrong M1 fingerprint evidence for {item['assetId']}")

    aibos_commit_matches = list(re.finditer(r"AIBOS-COMMIT:(?P<sha>[0-9a-f]{40})", evidence))
    if evidence.count("AIBOS-COMMIT:") != len(aibos_commit_matches):
        raise ValueError(f"unvalidated Aibos commit token for {item['assetId']}")
    for match in aibos_commit_matches:
        if match.group("sha") != AIBOS_EVIDENCE_COMMIT:
            raise ValueError(f"wrong Aibos commit evidence for {item['assetId']}")

    aibos_blob_matches = list(
        re.finditer(
            r"AIBOS-BLOB:(?P<sha>[0-9a-f]{40})@(?P<path>[A-Za-z0-9._/-]+)",
            evidence,
        )
    )
    if evidence.count("AIBOS-BLOB:") != len(aibos_blob_matches):
        raise ValueError(f"unvalidated Aibos blob token for {item['assetId']}")
    expected_binding = AIBOS_SEMANTIC_BINDINGS.get(item["assetId"])
    if expected_binding is None:
        if aibos_blob_matches:
            raise ValueError(f"unexpected Aibos blob evidence for {item['assetId']}")
    else:
        expected_path, expected_blob = expected_binding
        if (
            decision.disposition != "ADOPT_BASELINE"
            or len(aibos_blob_matches) != 1
            or aibos_blob_matches[0].group("path") != expected_path
            or aibos_blob_matches[0].group("sha") != expected_blob
            or aibos_blobs.get(expected_path) != expected_blob
        ):
            raise ValueError(f"invalid semantic Aibos blob binding for {item['assetId']}")

    aibos_issue_matches = list(re.finditer(r"AIBOS-ISSUE#(?P<number>\d+)", evidence))
    if evidence.count("AIBOS-ISSUE#") != len(aibos_issue_matches):
        raise ValueError(f"unvalidated Aibos issue token for {item['assetId']}")
    for match in aibos_issue_matches:
        if int(match.group("number")) != int(item["ownerIssue"]):
            raise ValueError(f"wrong Aibos owner Issue evidence for {item['assetId']}")


def local_decision(item: dict[str, Any]) -> Decision:
    asset_id = item["assetId"]
    owner = item["ownerSemanticUnit"]
    kind = item["kind"]
    fingerprint = item["fingerprintSha256"]
    if owner == "M1-SU-009":
        if asset_id == "M1A-000001":
            return d(
                "KEEP_H25",
                f"H25-COMMIT:{H25_EVIDENCE_COMMIT}",
                "M1-SU-009",
                "The exact public default branch remains the Browser product authority.",
            )
        if asset_id == "M1A-000002":
            return d(
                "KEEP_H25",
                "H25-GH-PR#325@head=0b98bfbd50e5d418baaa20064908de4470a41e28;draft=open",
                "M1-SU-001",
                "Branch is owned by the unresolved H25 draft security semantic unit.",
            )
        if asset_id == "M1A-000003":
            return d(
                "KEEP_H25",
                "H25-GH-PR#326@head=bbe01bea22d4cc34837d7636c149329742340b67;draft=open",
                "M1-SU-001",
                "Branch is owned by the unresolved H25 draft operations semantic unit.",
            )
        if kind == "gh_tag":
            return d(
                "HISTORICAL_PROVENANCE",
                f"H25-COMMIT:{item['publicSha']};M1-FP:{fingerprint}",
                "M1-SU-009",
                "Release tag is retained as H25 history and is not an Aibos adoption target.",
            )
        if kind != "local_ref":
            raise ValueError(f"unexpected SU009 kind for {asset_id}: {kind}")
        if asset_id in LOCAL_REF_KEEP:
            return d(
                "KEEP_H25",
                f"M1-FP:{fingerprint};AIBOS-ISSUE#16",
                "M1-SU-009",
                "Active local or tracking ref remains preserved under H25 responsibility.",
            )
        return d(
            "HISTORICAL_PROVENANCE",
            f"M1-FP:{fingerprint};AIBOS-ISSUE#16",
            "M1-SU-009",
            "Local recovery ref is retained as opaque Git provenance without ref mutation.",
        )
    if owner == "M1-SU-010":
        try:
            return SU010_EXPLICIT[asset_id]
        except KeyError as error:
            raise ValueError(f"missing explicit SU010 decision for {asset_id}") from error
    raise ValueError(f"not a local disposition owner: {owner}")


def main() -> None:
    args = parse_args()
    gh = str(args.gh_path) if args.gh_path else shutil.which("gh")
    if not gh:
        raise ValueError("--gh-path is required when gh is not on PATH")

    manifest_raw, manifest = read_json(args.manifest)
    _, manifest_summary = read_json(args.manifest_summary)
    manifest_sha = sha256(manifest_raw)
    if manifest_sha != M1_MANIFEST_SHA256 or manifest_sha != manifest_summary["manifestSha256"]:
        raise ValueError("immutable M1 manifest SHA-256 mismatch")
    if manifest["cutoffId"] != M1_CUTOFF or len(manifest["items"]) != 507:
        raise ValueError("immutable M1 cutoff or row count mismatch")

    aibos_identity = get_exact_repo_identity(gh, AIBOS_REPOSITORY)
    h25_identity = get_exact_repo_identity(gh, H25_REPOSITORY)
    if aibos_identity != (AIBOS_EVIDENCE_COMMIT, AIBOS_EVIDENCE_TREE):
        raise ValueError(f"Aibos evidence identity drifted: {aibos_identity!r}")
    if h25_identity != (H25_EVIDENCE_COMMIT, H25_EVIDENCE_TREE):
        raise ValueError(f"H25 evidence identity drifted: {h25_identity!r}")
    aibos_tree = gh_json(
        gh,
        f"repos/{AIBOS_REPOSITORY}/git/trees/{AIBOS_EVIDENCE_TREE}?recursive=1",
    )
    if aibos_tree.get("truncated"):
        raise ValueError("Aibos evidence tree response is truncated")
    aibos_blobs = {
        entry["path"]: entry["sha"]
        for entry in aibos_tree.get("tree", [])
        if entry.get("type") == "blob"
    }
    for asset_id, (path, blob) in AIBOS_SEMANTIC_BINDINGS.items():
        if aibos_blobs.get(path) != blob:
            raise ValueError(f"Aibos semantic blob evidence drifted for {asset_id}")

    h25_repository = gh_json(gh, f"repos/{H25_REPOSITORY}")
    if h25_repository.get("default_branch") != "main":
        raise ValueError("H25 default branch is no longer main")
    issue_list = gh_collection(gh, f"repos/{H25_REPOSITORY}/issues?state=all")
    pull_list = gh_collection(gh, f"repos/{H25_REPOSITORY}/pulls?state=all")
    commit_list = gh_collection(
        gh,
        f"repos/{H25_REPOSITORY}/commits?sha={H25_EVIDENCE_COMMIT}",
    )
    issues = {int(item["number"]): item for item in issue_list if "pull_request" not in item}
    pulls = {int(item["number"]): item for item in pull_list}
    reachable_commits = {item["sha"] for item in commit_list}
    if H25_EVIDENCE_COMMIT not in reachable_commits:
        raise ValueError("H25 evidence main is absent from its reachable commit list")
    missing_reachable = REQUIRED_REACHABLE_COMMITS - reachable_commits
    if missing_reachable:
        raise ValueError(f"required H25 evidence is not reachable from main: {sorted(missing_reachable)!r}")
    superseding = pulls.get(SUPERSEDING_PR)
    if (
        superseding is None
        or not superseding.get("merged_at")
        or superseding.get("merge_commit_sha") != SUPERSEDING_MERGE
        or superseding["base"]["ref"] != "main"
        or SUPERSEDING_MERGE not in reachable_commits
    ):
        raise ValueError("the exact PR 343 supersession evidence is unavailable")
    validate_referenced_merged_pulls(pulls, reachable_commits)
    expected_comments = {
        33: (ISSUE_33_AUDIT_COMMENT, ISSUE_33_AUDIT_COMMENT_SHA256),
        323: (ISSUE_323_EXIT_COMMENT, ISSUE_323_EXIT_COMMENT_SHA256),
    }
    validated_comment_ids: set[int] = set()
    for issue_number, (comment_id, expected_body_sha) in expected_comments.items():
        comments = gh_collection(
            gh,
            f"repos/{H25_REPOSITORY}/issues/{issue_number}/comments",
        )
        comment = next((value for value in comments if int(value["id"]) == comment_id), None)
        if comment is None:
            raise ValueError(f"the exact Issue {issue_number} disposition comment is unavailable")
        actual_body_sha = sha256(comment["body"].encode("utf-8"))
        if actual_body_sha != expected_body_sha:
            raise ValueError(f"the exact Issue {issue_number} disposition comment changed")
        validated_comment_ids.add(comment_id)

    rows: list[dict[str, str]] = []
    su008_counts: Counter[str] = Counter()
    for item in manifest["items"]:
        asset_id = item["assetId"]
        owner = item["ownerSemanticUnit"]
        decision = FOCAL.get(asset_id)
        if decision is not None:
            validate_focal_item(item, issues, pulls, reachable_commits)
        if decision is None and owner == "M1-SU-008":
            number = int(item["publicNumber"])
            if item["kind"] == "gh_issue":
                issue = issues.get(number)
                if issue is None:
                    raise ValueError(f"missing public issue {number}")
                if issue["state"] == "open":
                    if number != 341:
                        raise ValueError(f"unexpected open SU008 issue {number}")
                    decision = d(
                        "KEEP_H25",
                        "H25-GH-ISSUE#341@open",
                        "M1-SU-008",
                        "Open H25 work remains owned by the Browser repository.",
                    )
                elif issue["state"] == "closed":
                    decision = d(
                        "HISTORICAL_PROVENANCE",
                        f"H25-GH-ISSUE#{number}@closed",
                        "M1-SU-008",
                        "Closed public issue is retained as provenance without implicit code adoption.",
                    )
                else:
                    raise ValueError(f"unsupported issue state for {number}: {issue['state']}")
            elif item["kind"] == "gh_pr":
                pull = pulls.get(number)
                if pull is None:
                    raise ValueError(f"missing public pull request {number}")
                if pull["head"]["sha"] != item["publicSha"]:
                    raise ValueError(f"pull request head drift for {number}")
                if pull.get("merged_at"):
                    merge_sha = pull.get("merge_commit_sha")
                    if (
                        not merge_sha
                        or pull["base"]["ref"] != "main"
                        or merge_sha not in reachable_commits
                    ):
                        raise ValueError(f"merged pull request is not reachable from main: {number}")
                    decision = d(
                        "MERGED_H25",
                        f"H25-GH-PR#{number}@merged;merge={merge_sha};head={item['publicSha']}",
                        "M1-SU-008",
                        "Merged pull request is retained as exact H25 implementation history.",
                    )
                else:
                    if number not in {338, 340} or pull["state"] != "closed":
                        raise ValueError(f"unexpected unmerged SU008 pull request {number}")
                    decision = d(
                        "CLOSE_SUPERSEDED",
                        f"H25-GH-PR#343@merge={SUPERSEDING_MERGE}",
                        "M1-SU-008",
                        "Closed unmerged pull request is explicitly superseded by merged PR 343.",
                    )
            else:
                raise ValueError(f"unexpected SU008 kind for {asset_id}: {item['kind']}")
            su008_counts[decision.disposition] += 1
        if decision is None and owner in {"M1-SU-009", "M1-SU-010"}:
            decision = local_decision(item)
        if decision is None:
            raise ValueError(f"asset lacks a disposition decision: {asset_id}")
        if decision.disposition not in TERMINAL:
            raise ValueError(f"asset has a non-terminal disposition: {asset_id}")
        validate_decision_evidence(
            item,
            decision,
            issues,
            pulls,
            reachable_commits,
            validated_comment_ids,
            aibos_blobs,
        )
        rows.append(
            {
                "m1_cutoff_id": M1_CUTOFF,
                "m1_manifest_sha256": M1_MANIFEST_SHA256,
                "asset_id": asset_id,
                "m1_owner": owner,
                "m2_disposition": decision.disposition,
                "evidence_sha_or_issue": decision.evidence,
                "target_semantic_unit": decision.target,
                "decision_reason": decision.reason,
            }
        )

    expected_su008 = {
        "HISTORICAL_PROVENANCE": 144,
        "KEEP_H25": 1,
        "MERGED_H25": 169,
        "CLOSE_SUPERSEDED": 2,
    }
    if dict(su008_counts) != expected_su008:
        raise ValueError(f"SU008 terminal counts drifted: {dict(su008_counts)!r}")
    if len(rows) != 507 or len({row["asset_id"] for row in rows}) != 507:
        raise ValueError("overlay is not an exact one-to-one M1 mapping")

    overlay = {
        "overlayVersion": 1,
        "hashDomain": "aibos-m2-disposition/v1",
        "m1CutoffId": M1_CUTOFF,
        "m1ManifestSha256": M1_MANIFEST_SHA256,
        "m1SourceStateSha256": manifest["sourceStateSha256"],
        "aibosEvidenceCommit": AIBOS_EVIDENCE_COMMIT,
        "aibosEvidenceTree": AIBOS_EVIDENCE_TREE,
        "h25EvidenceCommit": H25_EVIDENCE_COMMIT,
        "h25EvidenceTree": H25_EVIDENCE_TREE,
        "items": rows,
    }
    overlay_raw = atomic_write_json(args.overlay, overlay)
    dispositions = Counter(row["m2_disposition"] for row in rows)
    owners = Counter(row["m1_owner"] for row in rows)
    targets = Counter(row["target_semantic_unit"] for row in rows)
    summary = {
        "summaryVersion": 1,
        "hashDomain": "aibos-m2-disposition-summary/v1",
        "m1CutoffId": M1_CUTOFF,
        "m1ManifestSha256": M1_MANIFEST_SHA256,
        "overlaySha256": sha256(overlay_raw),
        "rowCount": 507,
        "terminalCount": 507,
        "transientCount": 0,
        "duplicateAssetIds": 0,
        "orphanRows": 0,
        "missingAssets": 0,
        "privateSurfaceFindings": 0,
        "dispositionCounts": {name: dispositions.get(name, 0) for name in TERMINAL},
        "ownerCounts": dict(sorted(owners.items())),
        "targetSemanticUnitCounts": dict(sorted(targets.items())),
        "su008PublicEvidenceCounts": expected_su008,
        "verification": {
            "authoritativeBuilder": "python",
            "independentVerifiersRequired": ["powershell", "python"],
        },
    }
    atomic_write_json(args.overlay_summary, summary)
    print(
        json.dumps(
            {
                "ok": True,
                "cutoffId": M1_CUTOFF,
                "manifestSha256": M1_MANIFEST_SHA256,
                "overlaySha256": summary["overlaySha256"],
                "rowCount": 507,
                "terminalCount": 507,
                "transientCount": 0,
                "duplicateAssetIds": 0,
                "orphanRows": 0,
                "missingAssets": 0,
                "dispositionCounts": summary["dispositionCounts"],
            },
            indent=2,
            sort_keys=True,
        )
    )


if __name__ == "__main__":
    main()
