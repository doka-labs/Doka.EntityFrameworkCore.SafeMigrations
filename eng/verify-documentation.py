#!/usr/bin/env python3
"""Check offline Markdown integrity and the OpenSSF preparation inventory."""

import argparse
import datetime
import html
import json
from pathlib import Path
import re
import subprocess
import sys
from urllib.parse import unquote, urlsplit


REQUIRED = (
    "README.md", "CONTRIBUTING.md", "SUPPORT.md", "CODE_OF_CONDUCT.md",
    "GOVERNANCE.md", "ROADMAP.md", "SECURITY.md", "docs/README.md",
    "docs/api-reference.md", "docs/openssf-best-practices.md",
    "docs/operations/release-publication.md",
    "docs/security/security-design.md", "docs/security/secure-development.md",
    "docs/security/release-verification.md", "docs/runbooks/observability.md",
    "docs/runbooks/repository-settings.md", "docs/decisions/README.md",
    "docs/decisions/MADR-PROFILE.md", "docs/decisions/adr-template.md",
)
INLINE_LINK = re.compile(
    r"!?\[(?:[^\[\]\n]|\[[^\[\]\n]*\])*\]"
    r"\(\s*(<[^>\n]+>|[^\s)]+)"
    r"""(?:\s+(?:"[^"\n]*"|'[^'\n]*'|\([^()\n]*\)))?\s*\)"""
)
REFERENCE = re.compile(r"^ {0,3}\[([^]\n]+)\]:\s*(<[^>\n]+>|\S+)", re.MULTILINE)
REFERENCE_LINK = re.compile(r"!?\[([^]\n]+)\]\[([^]\n]*)\]")


def without_fences(text):
    """Keep line positions while excluding examples from link/heading checks."""
    result = []
    fence = None
    for line in text.splitlines():
        marker = re.match(r"^ {0,3}(`{3,}|~{3,})(.*)$", line)
        if fence is None and marker:
            fence = marker.group(1)
            result.append("")
        elif fence is not None:
            if (marker and marker.group(1)[0] == fence[0]
                    and len(marker.group(1)) >= len(fence) and not marker.group(2).strip()):
                fence = None
            result.append("")
        else:
            result.append(line)
    if fence is not None:
        raise ValueError("unclosed fenced code block")
    return "\n".join(result)


def heading_ids(text):
    """Model GitHub's ASCII heading slugs and explicit HTML anchors."""
    body = without_fences(text)
    anchors = set(re.findall(r'<a\s+(?:id|name)=["\']([^"\']+)["\']', body))
    used = set()
    for match in re.finditer(r"^ {0,3}#{1,6}\s+(.+?)(?:\s+#+)?$", body, re.MULTILINE):
        title = html.unescape(re.sub(r"<[^>]+>", "", match.group(1)))
        slug = re.sub(r"[^\w\- ]", "", title.lower()).replace(" ", "-")
        candidate = slug
        suffix = 0
        while candidate in used:
            suffix += 1
            candidate = f"{slug}-{suffix}"
        used.add(candidate)
        anchors.add(candidate)
    return anchors


def link_targets(text):
    body = without_fences(text)
    body = re.sub(r"(`+).*?\1", "", body)
    definitions = {
        " ".join(match.group(1).lower().split()): match.group(2).strip("<>")
        for match in REFERENCE.finditer(body)
    }
    targets = list(definitions.values())
    body = REFERENCE.sub("", body)
    targets.extend(m.group(1).strip("<>") for m in INLINE_LINK.finditer(body))
    body = INLINE_LINK.sub("", body)
    for match in REFERENCE_LINK.finditer(body):
        key = " ".join((match.group(2) or match.group(1)).lower().split())
        if key not in definitions:
            raise ValueError(f"undefined reference link: {key}")
        targets.append(definitions[key])
    targets.extend(re.findall(r'<(?:a|img)\s+[^>]*(?:href|src)=["\']([^"\']+)["\']', body))
    return targets


def resolve_link(root, source, target):
    parsed = urlsplit(target)
    if parsed.scheme:
        if parsed.scheme not in ("https", "http", "mailto"):
            raise ValueError(f"unsupported link scheme: {target}")
        if parsed.scheme in ("https", "http") and not parsed.netloc:
            raise ValueError(f"invalid external URL: {target}")
        return None
    if parsed.netloc or parsed.path.startswith("/") or "\\" in target:
        raise ValueError(f"non-portable repository link: {target}")
    path = source.parent / unquote(parsed.path) if parsed.path else source
    resolved = path.resolve()
    if not resolved.is_relative_to(root.resolve()):
        raise ValueError(f"link escapes repository: {target}")
    if not resolved.exists():
        raise ValueError(f"missing link destination: {target}")
    # macOS can resolve the wrong case; Linux/GitHub cannot.
    lexical = Path(*path.parts)
    while lexical != root and lexical.is_relative_to(root):
        if (lexical.name not in (".", "..")
                and lexical.name not in {item.name for item in lexical.parent.iterdir()}):
            raise ValueError(f"link path case mismatch: {target}")
        lexical = lexical.parent
    if parsed.fragment and resolved.suffix.lower() == ".md":
        anchors = heading_ids(resolved.read_text(encoding="utf-8"))
        if unquote(parsed.fragment) not in anchors:
            raise ValueError(f"missing Markdown anchor: {target}")
    return resolved


def section(text, heading, level):
    pattern = rf"^{'#' * level} {re.escape(heading)}\n(.*?)(?=^#{{1,{level}}} |\Z)"
    match = re.search(pattern, text, re.MULTILINE | re.DOTALL)
    if match is None or not match.group(1).strip():
        raise ValueError(f"missing or empty section: {heading}")
    return match.group(1)


def verify_openssf(text, catalog):
    states = {"Prepared", "Evidence", "External", "People", "Open", "Assess"}
    if set(catalog["levels"]) != {"Passing", "Silver", "Gold"}:
        raise ValueError("criterion snapshot must contain all three badge levels")
    if not re.fullmatch(r"[0-9a-f]{64}", catalog["sourceSha256"]):
        raise ValueError("criterion snapshot needs its upstream content digest")
    datetime.date.fromisoformat(catalog["retrieved"])
    for level, categories in catalog["levels"].items():
        expected = {}
        if set(categories) != {"MUST", "SHOULD", "SUGGESTED"}:
            raise ValueError(f"{level}: invalid snapshot requirement classes")
        for category, identifiers in categories.items():
            for identifier in identifiers:
                if not re.fullmatch(r"[a-zA-Z][a-zA-Z0-9_]*", identifier) or identifier in expected:
                    raise ValueError(f"{level}: invalid or duplicate snapshot ID")
                expected[identifier] = category
        actual = {}
        for line in section(without_fences(text), level, 2).splitlines():
            line = line.strip()
            if not line.startswith("|"):
                continue
            cells = [cell.strip() for cell in line.strip("|").split("|")]
            if cells == [
                "Criterion", "Class", "Readiness", "Repository evidence",
                "Remaining assessment evidence", "Owner",
            ] or all(re.fullmatch(r":?-+:?", cell) for cell in cells):
                continue
            if len(cells) != 6 or not all(cells) or not line.endswith("|"):
                raise ValueError(f"{level}: incomplete criterion row")
            match = re.fullmatch(r"`([a-zA-Z][a-zA-Z0-9_]*)`", cells[0])
            if match is None:
                raise ValueError(f"{level}: invalid criterion ID cell")
            identifier = match.group(1)
            if identifier in actual or cells[2] not in states or cells[5] not in ("M", "R", "C"):
                raise ValueError(f"{level}: duplicate criterion or invalid readiness/owner")
            actual[identifier] = cells[1]
        if actual != expected:
            missing = sorted(expected.keys() - actual.keys())
            extra = sorted(actual.keys() - expected.keys())
            wrong = sorted(key for key in actual.keys() & expected.keys() if actual[key] != expected[key])
            raise ValueError(
                f"{level}: criterion mismatch; missing={missing}, extra={extra}, wrong_class={wrong}"
            )


def read_markdown(path):
    """Validate original bytes before newline conversion can hide CRLF or CR."""
    text = path.read_bytes().decode("utf-8")
    if not text.isascii() or "\r" in text or not text.endswith("\n"):
        raise ValueError("document must be ASCII with LF and a final newline")
    return text


def verify_repository(root):
    root = root.resolve()
    errors = []
    inventory = subprocess.run(
        ["git", "-C", str(root), "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
        check=True, capture_output=True, text=True,
    )
    paths = sorted({
        root / name for name in inventory.stdout.split("\0")
        if name.endswith(".md") and (root / name).is_file()
    })
    for name in REQUIRED:
        if not (root / name).is_file():
            errors.append(f"missing required document: {name}")
    texts = {}
    graph = {}
    for path in paths:
        if path.name == "SECURITY.md" and path != root / "SECURITY.md":
            errors.append(
                f"security policy must be maintained only at repository root: {path.relative_to(root)}"
            )
        try:
            if not path.resolve().is_relative_to(root) or path.stat().st_size > 1024 * 1024:
                raise ValueError("document escapes repository or exceeds 1 MiB")
            text = read_markdown(path)
            texts[path] = text
            graph[path] = set()
            for target in link_targets(text):
                try:
                    destination = resolve_link(root, path, target)
                    if destination is not None:
                        graph[path].add(destination)
                except (ValueError, OSError) as error:
                    errors.append(f"{path.relative_to(root)}: {error}")
        except (ValueError, OSError) as error:
            errors.append(f"{path.relative_to(root)}: {error}")
    adr_paths = [
        path for path in paths
        if path.parent == root / "docs/decisions" and path.name.startswith("D-")
    ]
    try:
        catalog = json.loads((root / "eng/openssf-criteria.json").read_text(encoding="utf-8"))
        verify_openssf(texts[root / "docs/openssf-best-practices.md"], catalog)
    except (ValueError, KeyError, OSError, TypeError) as error:
        errors.append(f"OpenSSF preparation: {error}")
    reachable = set()
    pending = [root / "README.md"]
    while pending:
        path = pending.pop()
        if path not in reachable:
            reachable.add(path)
            pending.extend(graph.get(path, set()) - reachable)
    for path in paths:
        if path.is_relative_to(root / "docs") and path not in reachable:
            errors.append(f"document is not reachable from README: {path.relative_to(root)}")
    api = texts.get(root / "docs/api-reference.md", "")
    features = root / "src/Doka.EntityFrameworkCore.SafeMigrations/Features"
    for path in features.rglob("SafeMigrationBuilderExtensions.*.cs"):
        methods = re.findall(
            r"public static (?:OperationBuilder<SafeMigrationOperation>|MigrationBuilder) (\w+)",
            path.read_text(encoding="utf-8"),
        )
        for method in methods:
            if not re.search(rf"\b{re.escape(method)}\b", api):
                errors.append(f"API guide omits builder method: {method}")
    return errors, len(paths), len(adr_paths)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent)
    arguments = parser.parse_args()
    try:
        errors, documents, decisions = verify_repository(arguments.root)
    except (OSError, subprocess.CalledProcessError) as error:
        print(f"Documentation verification failed: {error}", file=sys.stderr)
        return 1
    if errors:
        for error in errors:
            print(error, file=sys.stderr)
        return 1
    print(
        f"Documentation verified: {documents} Markdown files "
        f"(including {decisions} ADRs as Markdown), all OpenSSF snapshot criteria."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
