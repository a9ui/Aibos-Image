#!/usr/bin/env python3
"""Independent, privacy-safe H25 legacy-asset ledger capture.

This script is intentionally standard-library-only. It reads GitHub through an
already authenticated `gh` executable and reads local Git state with
GIT_OPTIONAL_LOCKS=0. It never fetches, checks out, stages, writes Git objects,
or changes a worktree.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
from collections import Counter
from pathlib import Path
from typing import Any, Iterable


MANIFEST_VERSION = 3
HASH_DOMAIN = "aibos-m1-ledger/v3"
KIND_ORDER = {
    "gh_branch": 0,
    "gh_issue": 1,
    "gh_pr": 2,
    "gh_tag": 3,
    "local_ref": 4,
    "stash": 5,
    "worktree": 6,
    "staged_path": 7,
    "unstaged_path": 8,
    "untracked_path": 9,
}
OWNER_ISSUES = {
    "M1-SU-001": 8,
    "M1-SU-002": 9,
    "M1-SU-003": 10,
    "M1-SU-004": 11,
    "M1-SU-005": 12,
    "M1-SU-006": 13,
    "M1-SU-007": 14,
    "M1-SU-008": 15,
    "M1-SU-009": 16,
    "M1-SU-010": 17,
}
FOCAL_OWNERS = {
    ("gh_issue", 33): "M1-SU-001",
    ("gh_issue", 318): "M1-SU-001",
    ("gh_pr", 325): "M1-SU-001",
    ("gh_pr", 326): "M1-SU-001",
    ("gh_issue", 329): "M1-SU-002",
    ("gh_issue", 330): "M1-SU-002",
    ("gh_pr", 24): "M1-SU-002",
    ("gh_pr", 331): "M1-SU-002",
    ("gh_issue", 97): "M1-SU-003",
    ("gh_pr", 29): "M1-SU-003",
    ("gh_pr", 160): "M1-SU-003",
    ("gh_issue", 105): "M1-SU-004",
    ("gh_issue", 106): "M1-SU-004",
    ("gh_issue", 321): "M1-SU-004",
    ("gh_pr", 96): "M1-SU-004",
    ("gh_pr", 134): "M1-SU-005",
    ("gh_pr", 147): "M1-SU-005",
    ("gh_pr", 152): "M1-SU-005",
    ("gh_pr", 154): "M1-SU-005",
    ("gh_pr", 156): "M1-SU-005",
    ("gh_pr", 158): "M1-SU-005",
    ("gh_issue", 320): "M1-SU-006",
    ("gh_issue", 323): "M1-SU-006",
    ("gh_pr", 319): "M1-SU-006",
    ("gh_issue", 316): "M1-SU-007",
    ("gh_issue", 328): "M1-SU-007",
    ("gh_pr", 317): "M1-SU-007",
}


def scalar(value: Any) -> str:
    if value is None:
        return ""
    if isinstance(value, bool):
        return "true" if value else "false"
    return str(value)


def nested(value: Any, *path: str) -> Any:
    current = value
    for key in path:
        if not isinstance(current, dict):
            return None
        current = current.get(key)
    return current


def field_hash(domain: str, *fields: Any) -> str:
    digest = hashlib.sha256()
    for field in (domain, *fields):
        encoded = scalar(field).encode("utf-8")
        digest.update(str(len(encoded)).encode("ascii"))
        digest.update(b":")
        digest.update(encoded)
    return digest.hexdigest()


def value_list_hash(domain: str, values: Iterable[Any]) -> str:
    normalized = sorted((scalar(value) for value in values))
    return field_hash(domain, *normalized)


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def run(
    arguments: list[str],
    *,
    cwd: str | None = None,
    check: bool = True,
    binary: bool = False,
) -> str | bytes:
    environment = os.environ.copy()
    environment["GIT_OPTIONAL_LOCKS"] = "0"
    environment["GIT_TERMINAL_PROMPT"] = "0"
    completed = subprocess.run(
        arguments,
        cwd=cwd,
        env=environment,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        check=False,
    )
    if check and completed.returncode != 0:
        command_name = Path(arguments[0]).name
        raise RuntimeError(
            f"{command_name} exited {completed.returncode}; output is intentionally redacted"
        )
    if binary:
        return completed.stdout
    return completed.stdout.decode("utf-8", errors="strict")


def gh_object(gh_path: str, endpoint: str) -> dict[str, Any]:
    raw = run([gh_path, "api", endpoint])
    value = json.loads(str(raw))
    if not isinstance(value, dict):
        raise RuntimeError("GitHub API returned an unexpected object shape")
    return value


def gh_items(gh_path: str, endpoint: str) -> list[dict[str, Any]]:
    raw = run(
        [
            gh_path,
            "api",
            "--paginate",
            endpoint,
            "--jq",
            ".[] | @json",
        ]
    )
    items: list[dict[str, Any]] = []
    for line in str(raw).splitlines():
        if not line:
            continue
        value = json.loads(line)
        if not isinstance(value, dict):
            raise RuntimeError("GitHub API returned an unexpected list item shape")
        items.append(value)
    return items


def owner_for(kind: str, public_number: int | None) -> str:
    focal = FOCAL_OWNERS.get((kind, public_number or -1))
    if focal:
        return focal
    if kind in {"gh_issue", "gh_pr"}:
        return "M1-SU-008"
    if kind in {"gh_branch", "gh_tag", "local_ref"}:
        return "M1-SU-009"
    return "M1-SU-010"


def make_record(
    *,
    kind: str,
    source: str,
    identity_fields: Iterable[Any],
    fingerprint_fields: Iterable[Any],
    public_number: int | None = None,
    public_sha: str = "",
) -> dict[str, Any]:
    if kind not in KIND_ORDER:
        raise RuntimeError(f"unsupported ledger kind: {kind}")
    owner = owner_for(kind, public_number)
    return {
        "kind": kind,
        "source": source,
        "publicNumber": "" if public_number is None else str(public_number),
        "publicSha": public_sha.lower(),
        "identitySha256": field_hash(
            f"{HASH_DOMAIN}/identity/{kind}", *identity_fields
        ),
        "fingerprintSha256": field_hash(
            f"{HASH_DOMAIN}/fingerprint/{kind}", *fingerprint_fields
        ),
        "ownerSemanticUnit": owner,
        "ownerIssue": OWNER_ISSUES[owner],
    }


def capture_github(
    gh_path: str, repository: str
) -> tuple[list[dict[str, Any]], str]:
    repo = gh_object(gh_path, f"/repos/{repository}")
    default_branch = scalar(repo.get("default_branch"))
    if not default_branch:
        raise RuntimeError("GitHub repository has no default branch")
    default_commit = gh_object(
        gh_path, f"/repos/{repository}/commits/{default_branch}"
    )
    default_sha = scalar(default_commit.get("sha")).lower()
    if not re.fullmatch(r"[0-9a-f]{40}", default_sha):
        raise RuntimeError("GitHub default commit is not a SHA-1")

    records: list[dict[str, Any]] = []
    issues = gh_items(
        gh_path, f"/repos/{repository}/issues?state=all&per_page=100"
    )
    for issue in issues:
        if issue.get("pull_request") is not None:
            continue
        number = int(issue["number"])
        labels_hash = value_list_hash(
            f"{HASH_DOMAIN}/github/issue-labels",
            (
                label.get("name", "")
                for label in issue.get("labels", [])
                if isinstance(label, dict)
            ),
        )
        assignees_hash = value_list_hash(
            f"{HASH_DOMAIN}/github/issue-assignees",
            (
                assignee.get("login", "")
                for assignee in issue.get("assignees", [])
                if isinstance(assignee, dict)
            ),
        )
        records.append(
            make_record(
                kind="gh_issue",
                source="H25-GITHUB",
                public_number=number,
                identity_fields=(repository, number),
                fingerprint_fields=(
                    repository,
                    number,
                    issue.get("id"),
                    issue.get("node_id"),
                    issue.get("state"),
                    issue.get("state_reason"),
                    issue.get("locked"),
                    issue.get("active_lock_reason"),
                    issue.get("author_association"),
                    nested(issue, "user", "login"),
                    issue.get("title"),
                    issue.get("body"),
                    issue.get("comments"),
                    issue.get("created_at"),
                    issue.get("updated_at"),
                    issue.get("closed_at"),
                    nested(issue, "milestone", "number"),
                    nested(issue, "milestone", "state"),
                    nested(issue, "milestone", "title"),
                    labels_hash,
                    assignees_hash,
                ),
            )
        )

    pulls = gh_items(
        gh_path, f"/repos/{repository}/pulls?state=all&per_page=100"
    )
    for pull in pulls:
        number = int(pull["number"])
        labels_hash = value_list_hash(
            f"{HASH_DOMAIN}/github/pr-labels",
            (
                label.get("name", "")
                for label in pull.get("labels", [])
                if isinstance(label, dict)
            ),
        )
        assignees_hash = value_list_hash(
            f"{HASH_DOMAIN}/github/pr-assignees",
            (
                assignee.get("login", "")
                for assignee in pull.get("assignees", [])
                if isinstance(assignee, dict)
            ),
        )
        reviewers_hash = value_list_hash(
            f"{HASH_DOMAIN}/github/pr-reviewers",
            (
                reviewer.get("login", "")
                for reviewer in pull.get("requested_reviewers", [])
                if isinstance(reviewer, dict)
            ),
        )
        head_sha = scalar(nested(pull, "head", "sha")).lower()
        records.append(
            make_record(
                kind="gh_pr",
                source="H25-GITHUB",
                public_number=number,
                public_sha=head_sha if re.fullmatch(r"[0-9a-f]{40}", head_sha) else "",
                identity_fields=(repository, number),
                fingerprint_fields=(
                    repository,
                    number,
                    pull.get("id"),
                    pull.get("node_id"),
                    pull.get("state"),
                    pull.get("locked"),
                    pull.get("draft"),
                    pull.get("author_association"),
                    nested(pull, "user", "login"),
                    pull.get("title"),
                    pull.get("body"),
                    pull.get("created_at"),
                    pull.get("updated_at"),
                    pull.get("closed_at"),
                    pull.get("merged_at"),
                    pull.get("merge_commit_sha"),
                    nested(pull, "head", "sha"),
                    nested(pull, "head", "ref"),
                    nested(pull, "head", "repo", "full_name"),
                    nested(pull, "base", "sha"),
                    nested(pull, "base", "ref"),
                    nested(pull, "base", "repo", "full_name"),
                    labels_hash,
                    assignees_hash,
                    reviewers_hash,
                ),
            )
        )

    branches = gh_items(gh_path, f"/repos/{repository}/branches?per_page=100")
    for branch in branches:
        name = scalar(branch.get("name"))
        sha = scalar(nested(branch, "commit", "sha")).lower()
        records.append(
            make_record(
                kind="gh_branch",
                source="H25-GITHUB",
                public_sha=sha if re.fullmatch(r"[0-9a-f]{40}", sha) else "",
                identity_fields=(repository, name),
                fingerprint_fields=(
                    repository,
                    name,
                    nested(branch, "commit", "sha"),
                    branch.get("protected"),
                ),
            )
        )

    tags = gh_items(gh_path, f"/repos/{repository}/tags?per_page=100")
    for tag in tags:
        name = scalar(tag.get("name"))
        sha = scalar(nested(tag, "commit", "sha")).lower()
        records.append(
            make_record(
                kind="gh_tag",
                source="H25-GITHUB",
                public_sha=sha if re.fullmatch(r"[0-9a-f]{40}", sha) else "",
                identity_fields=(repository, name),
                fingerprint_fields=(
                    repository,
                    name,
                    nested(tag, "commit", "sha"),
                    tag.get("node_id"),
                ),
            )
        )

    return records, default_sha


def normalize_worktree_path(path: str) -> str:
    normalized = path.replace("\\", "/")
    if len(normalized) > 3:
        normalized = normalized.rstrip("/")
    return normalized


def parse_worktrees(raw: bytes) -> list[dict[str, str]]:
    worktrees: list[dict[str, str]] = []
    for block in raw.split(b"\0\0"):
        if not block:
            continue
        record: dict[str, str] = {}
        for encoded_line in block.split(b"\0"):
            if not encoded_line:
                continue
            line = encoded_line.decode("utf-8", errors="strict")
            key, separator, value = line.partition(" ")
            if separator:
                record[key] = value
            else:
                record[key] = "true"
        if "worktree" not in record:
            raise RuntimeError("git worktree porcelain record lacks a path")
        worktrees.append(record)
    return worktrees


def try_git_value(repository: str, arguments: list[str]) -> str:
    completed = subprocess.run(
        ["git", "-C", repository, *arguments],
        env={
            **os.environ,
            "GIT_OPTIONAL_LOCKS": "0",
            "GIT_TERMINAL_PROMPT": "0",
        },
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    if completed.returncode != 0:
        return ""
    return completed.stdout.decode("utf-8", errors="strict").strip()


def parse_status(
    worktree: str, worktree_identity: str
) -> list[dict[str, Any]]:
    raw = run(
        [
            "git",
            "-C",
            worktree,
            "status",
            "--porcelain=v1",
            "-z",
            "--untracked-files=all",
        ],
        binary=True,
    )
    parts = bytes(raw).split(b"\0")
    records: list[dict[str, Any]] = []
    index = 0
    while index < len(parts):
        entry = parts[index]
        index += 1
        if not entry:
            continue
        if len(entry) < 3:
            raise RuntimeError("git status returned a malformed porcelain entry")
        status = entry[:2].decode("ascii", errors="strict")
        path = entry[3:].decode("utf-8", errors="strict")
        original_path = ""
        if status[0] in "RC" or status[1] in "RC":
            if index >= len(parts):
                raise RuntimeError("git status rename entry lacks an origin")
            original_path = parts[index].decode("utf-8", errors="strict")
            index += 1

        worktree_oid = try_git_value(
            worktree, ["hash-object", "--no-filters", "--", path]
        )
        index_oid = try_git_value(worktree, ["rev-parse", "--verify", f":{path}"])

        if status == "??":
            records.append(
                make_record(
                    kind="untracked_path",
                    source="H25-LOCAL",
                    identity_fields=(worktree_identity, path),
                    fingerprint_fields=(
                        worktree_identity,
                        status,
                        path,
                        original_path,
                        worktree_oid,
                    ),
                )
            )
            continue
        if status[0] not in {" ", "?", "!"}:
            records.append(
                make_record(
                    kind="staged_path",
                    source="H25-LOCAL",
                    identity_fields=(
                        worktree_identity,
                        "staged",
                        path,
                        original_path,
                    ),
                    fingerprint_fields=(
                        worktree_identity,
                        status,
                        path,
                        original_path,
                        index_oid,
                    ),
                )
            )
        if status[1] not in {" ", "?", "!"}:
            records.append(
                make_record(
                    kind="unstaged_path",
                    source="H25-LOCAL",
                    identity_fields=(
                        worktree_identity,
                        "unstaged",
                        path,
                        original_path,
                    ),
                    fingerprint_fields=(
                        worktree_identity,
                        status,
                        path,
                        original_path,
                        worktree_oid,
                        index_oid,
                    ),
                )
            )
    return records


def capture_local(repository: str) -> list[dict[str, Any]]:
    records: list[dict[str, Any]] = []
    ref_text = str(
        run(
            [
                "git",
                "-C",
                repository,
                "for-each-ref",
                "--sort=refname",
                "--format=%(refname)%09%(objectname)%09%(objecttype)%09%(*objectname)%09%(upstream)%09%(worktreepath)",
            ]
        )
    )
    for line in ref_text.splitlines():
        fields = (line.split("\t", 5) + ["", "", "", "", "", ""])[:6]
        refname, object_name, object_type, peeled, upstream, worktree_path = fields
        records.append(
            make_record(
                kind="local_ref",
                source="H25-LOCAL",
                identity_fields=(refname,),
                fingerprint_fields=(
                    refname,
                    object_name,
                    object_type,
                    peeled,
                    upstream,
                    normalize_worktree_path(worktree_path),
                ),
            )
        )

    stash_text = str(
        run(
            [
                "git",
                "-C",
                repository,
                "stash",
                "list",
                "--format=%gd%x09%H%x09%P%x09%gs",
            ]
        )
    )
    for line in stash_text.splitlines():
        fields = (line.split("\t", 3) + ["", "", "", ""])[:4]
        selector, object_name, parents, subject = fields
        records.append(
            make_record(
                kind="stash",
                source="H25-LOCAL",
                identity_fields=(selector, object_name),
                fingerprint_fields=(selector, object_name, parents, subject),
            )
        )

    worktree_raw = run(
        ["git", "-C", repository, "worktree", "list", "--porcelain", "-z"],
        binary=True,
    )
    for worktree in parse_worktrees(bytes(worktree_raw)):
        raw_path = worktree["worktree"]
        path = normalize_worktree_path(raw_path)
        identity = field_hash(f"{HASH_DOMAIN}/worktree-path", path)
        status_records = parse_status(raw_path, identity)
        status_state = source_state_hash(
            status_records, f"{HASH_DOMAIN}/worktree-status"
        )
        records.append(
            make_record(
                kind="worktree",
                source="H25-LOCAL",
                identity_fields=(path,),
                fingerprint_fields=(
                    path,
                    worktree.get("HEAD"),
                    worktree.get("branch"),
                    worktree.get("detached"),
                    worktree.get("bare"),
                    worktree.get("locked"),
                    worktree.get("prunable"),
                    status_state,
                ),
            )
        )
        records.extend(status_records)
    return records


def sort_key(record: dict[str, Any]) -> tuple[int, int, str]:
    number = record.get("publicNumber", "")
    public_sort = int(number) if number else 2**63 - 1
    return (
        KIND_ORDER[record["kind"]],
        public_sort,
        record["identitySha256"],
    )


def source_state_hash(records: list[dict[str, Any]], domain: str) -> str:
    values = [
        "|".join(
            (
                record["kind"],
                record["source"],
                record["publicNumber"],
                record["publicSha"],
                record["identitySha256"],
                record["fingerprintSha256"],
            )
        )
        for record in sorted(records, key=sort_key)
    ]
    return field_hash(domain, *values)


def capture_once(
    gh_path: str, repository: str, legacy_repo: str
) -> dict[str, Any]:
    github_records, default_sha = capture_github(gh_path, repository)
    local_records = capture_local(legacy_repo)
    all_records = github_records + local_records
    return {
        "records": all_records,
        "defaultSha": default_sha,
        "githubStateSha256": source_state_hash(
            github_records, f"{HASH_DOMAIN}/github-state"
        ),
        "localStateSha256": source_state_hash(
            local_records, f"{HASH_DOMAIN}/local-state"
        ),
        "sourceStateSha256": source_state_hash(
            all_records, f"{HASH_DOMAIN}/source-state"
        ),
    }


def item_line(item: dict[str, Any]) -> str:
    ordered = {
        "assetId": item["assetId"],
        "kind": item["kind"],
        "source": item["source"],
        "publicNumber": item["publicNumber"],
        "publicSha": item["publicSha"],
        "identitySha256": item["identitySha256"],
        "fingerprintSha256": item["fingerprintSha256"],
        "ownerSemanticUnit": item["ownerSemanticUnit"],
        "ownerIssue": item["ownerIssue"],
        "disposition": "PENDING_M2",
        "evidenceRef": f"AIBOS-ISSUE-{item['ownerIssue']}",
    }
    return json.dumps(ordered, ensure_ascii=True, separators=(",", ":"))


def build_manifest(
    snapshot: dict[str, Any],
    cutoff_id: str,
    captured_at_utc: str,
    aibos_base_sha: str,
) -> tuple[bytes, list[dict[str, Any]], list[str]]:
    records = sorted(snapshot["records"], key=sort_key)
    items: list[dict[str, Any]] = []
    lines: list[str] = []
    for index, record in enumerate(records, 1):
        item = {**record, "assetId": f"M1A-{index:06d}"}
        items.append(item)
        lines.append(item_line(item))
    output = [
        "{",
        f'  "manifestVersion": {MANIFEST_VERSION},',
        f'  "hashDomain": "{HASH_DOMAIN}",',
        f'  "cutoffId": "{cutoff_id}",',
        f'  "capturedAtUtc": "{captured_at_utc}",',
        f'  "aibosBaseSha": "{aibos_base_sha}",',
        f'  "h25DefaultSha": "{snapshot["defaultSha"]}",',
        f'  "sourceStateSha256": "{snapshot["sourceStateSha256"]}",',
        '  "items": [',
    ]
    for index, line in enumerate(lines):
        suffix = "," if index + 1 < len(lines) else ""
        output.append(f"    {line}{suffix}")
    output.extend(["  ]", "}", ""])
    return "\n".join(output).encode("ascii"), items, lines


def grouped_hashes(
    items: list[dict[str, Any]],
    lines: list[str],
    key_name: str,
    expected_keys: Iterable[str],
) -> tuple[dict[str, int], dict[str, str]]:
    grouped: dict[str, list[str]] = {key: [] for key in expected_keys}
    for item, line in zip(items, lines, strict=True):
        grouped.setdefault(str(item[key_name]), []).append(line)
    counts = {key: len(grouped[key]) for key in sorted(grouped)}
    hashes = {
        key: sha256_bytes(
            (
                ("\n".join(grouped[key]) + "\n")
                if grouped[key]
                else ""
            ).encode("ascii")
        )
        for key in sorted(grouped)
    }
    return counts, hashes


def write_capture(
    output_directory: str,
    manifest_bytes: bytes,
    capture: dict[str, Any],
) -> None:
    output = Path(output_directory).resolve()
    output.mkdir(parents=True, exist_ok=True)
    (output / "manifest.json").write_bytes(manifest_bytes)
    (output / "capture.json").write_text(
        json.dumps(capture, ensure_ascii=True, indent=2, sort_keys=True) + "\n",
        encoding="ascii",
        newline="\n",
    )


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("--legacy-repo", required=True)
    parser.add_argument("--github-repository", required=True)
    parser.add_argument("--gh-path", default="gh")
    parser.add_argument("--cutoff-id", required=True)
    parser.add_argument("--captured-at-utc", required=True)
    parser.add_argument("--aibos-base-sha", required=True)
    parser.add_argument("--output-directory", required=True)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if not re.fullmatch(r"[A-Za-z0-9._-]{1,96}", args.cutoff_id):
        raise RuntimeError("cutoff id contains unsupported characters")
    if not re.fullmatch(
        r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z", args.captured_at_utc
    ):
        raise RuntimeError("captured-at must be whole-second UTC")
    aibos_sha = args.aibos_base_sha.lower()
    if not re.fullmatch(r"[0-9a-f]{40}", aibos_sha):
        raise RuntimeError("Aibos base must be a SHA-1")
    legacy_repo = str(Path(args.legacy_repo).resolve())
    if not Path(legacy_repo, ".git").exists():
        git_dir = try_git_value(legacy_repo, ["rev-parse", "--git-dir"])
        if not git_dir:
            raise RuntimeError("legacy repository is not a Git worktree")

    before = capture_once(args.gh_path, args.github_repository, legacy_repo)
    after = capture_once(args.gh_path, args.github_repository, legacy_repo)
    if before["sourceStateSha256"] != after["sourceStateSha256"]:
        raise RuntimeError("H25 source state changed during independent capture")
    if before["defaultSha"] != after["defaultSha"]:
        raise RuntimeError("H25 default SHA changed during independent capture")

    manifest_bytes, items, lines = build_manifest(
        before, args.cutoff_id, args.captured_at_utc, aibos_sha
    )
    category_counts, category_hashes = grouped_hashes(
        items, lines, "kind", KIND_ORDER.keys()
    )
    owner_counts, owner_hashes = grouped_hashes(
        items, lines, "ownerSemanticUnit", OWNER_ISSUES.keys()
    )
    identity_values = [item["identitySha256"] for item in items]
    capture = {
        "aibosBaseSha": aibos_sha,
        "capturedAtUtc": args.captured_at_utc,
        "categoryCounts": category_counts,
        "categorySha256": category_hashes,
        "cutoffId": args.cutoff_id,
        "githubStateSha256": before["githubStateSha256"],
        "h25DefaultSha": before["defaultSha"],
        "hashDomain": HASH_DOMAIN,
        "implementation": "python-standard-library-v1",
        "localStateSha256": before["localStateSha256"],
        "manifestSha256": sha256_bytes(manifest_bytes),
        "manifestVersion": MANIFEST_VERSION,
        "ownershipCounts": owner_counts,
        "ownershipSha256": owner_hashes,
        "recordCount": len(items),
        "sourceSetSha256": field_hash(
            f"{HASH_DOMAIN}/source-set", *identity_values
        ),
        "sourceStateAfterSha256": after["sourceStateSha256"],
        "sourceStateBeforeSha256": before["sourceStateSha256"],
        "sourceStateSha256": before["sourceStateSha256"],
        "sourceUnchanged": True,
    }
    if sum(category_counts.values()) != len(items):
        raise RuntimeError("category counts do not cover the manifest")
    if sum(owner_counts.values()) != len(items):
        raise RuntimeError("ownership counts do not cover the manifest")
    if len({item["identitySha256"] for item in items}) != len(items):
        raise RuntimeError("ledger identities are not unique")
    write_capture(args.output_directory, manifest_bytes, capture)
    print(
        json.dumps(
            {
                "ok": True,
                "implementation": capture["implementation"],
                "recordCount": capture["recordCount"],
                "manifestSha256": capture["manifestSha256"],
                "sourceUnchanged": True,
            },
            separators=(",", ":"),
        )
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"legacy ledger capture failed: {error}", file=sys.stderr)
        raise SystemExit(1)
