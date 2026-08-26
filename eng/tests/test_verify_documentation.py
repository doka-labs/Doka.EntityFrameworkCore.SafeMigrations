"""Positive and negative controls for the dependency-free documentation gate."""

import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("verify_documentation", ROOT / "eng/verify-documentation.py")
documentation = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(documentation)


class MarkdownTests(unittest.TestCase):
    def test_fenced_examples_are_not_links_or_headings(self):
        text = "# Real\n\n```markdown\n# Fake\n[bad](absent.md)\n```\n\n[good](real.md)\n"

        self.assertEqual({"real"}, documentation.heading_ids(text))
        self.assertEqual(["real.md"], documentation.link_targets(text))

    def test_long_fence_does_not_end_at_shorter_marker(self):
        text = "````\n```\n[bad](absent.md)\n````\n"

        self.assertEqual([], documentation.link_targets(text))

    def test_unclosed_fence_rejects_instead_of_hiding_remaining_document(self):
        with self.assertRaisesRegex(ValueError, "unclosed"):
            documentation.link_targets("```\n[hidden](absent.md)\n")

    def test_headings_preserve_underscores_and_duplicate_suffixes(self):
        text = '# A `name`\n\n## A name\n\n<a id="failure_code"></a>\n'

        self.assertEqual({"a-name", "a-name-1", "failure_code"}, documentation.heading_ids(text))

    def test_reference_links_and_inline_code(self):
        text = "[good][ref]\n`[ignored](no.md)`\n\n[ref]: target.md#here\n"

        self.assertEqual(["target.md#here", "target.md#here"], documentation.link_targets(text))

    def test_undefined_reference_rejects(self):
        with self.assertRaisesRegex(ValueError, "undefined reference"):
            documentation.link_targets("[bad][unknown]\n")

    def test_all_inline_link_title_delimiters_preserve_target(self):
        titles = ('"double quoted"', "'single quoted'", "(parenthesized)")
        for title in titles:
            with self.subTest(title=title):
                text = f"[label](Target.md#heading {title})\n"
                angle_text = f"[label](<Target.md#heading> {title})\n"

                self.assertEqual(["Target.md#heading"], documentation.link_targets(text))
                self.assertEqual(["Target.md#heading"], documentation.link_targets(angle_text))

    def test_titled_missing_targets_are_not_skipped(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            for title in ('"title"', "'title'", "(title)"):
                with self.subTest(title=title):
                    targets = documentation.link_targets(f"[broken](missing.md {title})\n")

                    self.assertEqual(["missing.md"], targets)
                    with self.assertRaisesRegex(ValueError, "missing link destination"):
                        documentation.resolve_link(root, root / "README.md", targets[0])

    def test_local_link_and_anchor_boundaries(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            source = root / "README.md"
            target = root / "Target.md"
            source.write_text("# Start\n", encoding="utf-8")
            target.write_text('# Heading\n\n<a id="failure_code"></a>\n', encoding="utf-8")

            self.assertEqual(target, documentation.resolve_link(root, source, "Target.md#heading"))
            self.assertEqual(target, documentation.resolve_link(root, source, "Target.md#failure_code"))
            links = (
                "Missing.md", "Target.md#missing", "../outside.md", "%2e%2e/outside.md",
                "/etc/passwd", "file:///etc/passwd", "javascript:alert(1)", "target.md",
            )
            for link in links:
                with self.subTest(link=link), self.assertRaises(ValueError):
                    documentation.resolve_link(root, source, link)

    def test_repository_local_symlink_cannot_escape(self):
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary).resolve()
            root = base / "repo"
            root.mkdir()
            outside = base / "outside.md"
            outside.write_text("# Outside\n", encoding="utf-8")
            (root / "linked.md").symlink_to(outside)

            with self.assertRaisesRegex(ValueError, "escapes repository"):
                documentation.resolve_link(root, root / "README.md", "linked.md")

    def test_original_markdown_bytes_enforce_ascii_lf_and_final_newline(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "document.md"
            path.write_bytes(b"# Valid\n\nText.\n")

            self.assertEqual("# Valid\n\nText.\n", documentation.read_markdown(path))

            invalid = (
                b"# CRLF\r\n", b"# CR\r", b"# Mixed\r\nText\n",
                b"# Missing final newline", b"# Non-ASCII \xc3\xa4\n",
                b"\xef\xbb\xbf# BOM\n", b"# Invalid UTF-8 \xff\n",
            )
            for payload in invalid:
                with self.subTest(payload=payload):
                    path.write_bytes(payload)

                    with self.assertRaises(ValueError):
                        documentation.read_markdown(path)


class OpenSsfTests(unittest.TestCase):
    def setUp(self):
        # Synthetic fixtures isolate parser tests from maintained readiness prose.
        self.catalog = {
            "sourceSha256": "0" * 64,
            "retrieved": "2026-08-26",
            "levels": {
                "Passing": {
                    "MUST": ["description_good"],
                    "SHOULD": ["contribution_requirements"],
                    "SUGGESTED": [],
                },
                "Silver": {
                    "MUST": ["contribution_requirements"],
                    "SHOULD": [],
                    "SUGGESTED": [],
                },
                "Gold": {
                    "MUST": ["require_2FA"],
                    "SHOULD": ["secure_2FA"],
                    "SUGGESTED": [],
                },
            },
        }
        self.row = "| `description_good` | MUST | Prepared | Guide | Public evidence | M |"
        self.text = """# Preparation

## Passing

| Criterion | Class | Readiness | Repository evidence | Remaining assessment evidence | Owner |
| --- | --- | --- | --- | --- | --- |
| `description_good` | MUST | Prepared | Guide | Public evidence | M |
| `contribution_requirements` | SHOULD | Prepared | Guide | Review | C |

## Silver

| Criterion | Class | Readiness | Repository evidence | Remaining assessment evidence | Owner |
| --- | --- | --- | --- | --- | --- |
| `contribution_requirements` | MUST | Prepared | Guide | Review | C |

## Gold

| Criterion | Class | Readiness | Repository evidence | Remaining assessment evidence | Owner |
| --- | --- | --- | --- | --- | --- |
| `require_2FA` | MUST | External | Settings | Readback | M |
| `secure_2FA` | SHOULD | External | Settings | Readback | M |
"""

    def test_complete_synthetic_map_is_valid(self):
        documentation.verify_openssf(self.text, self.catalog)

    def test_missing_extra_duplicate_wrong_class_and_invented_achievement_reject(self):
        mutations = {
            "missing": self.text.replace(self.row + "\n", "", 1),
            "extra": self.text.replace(
                self.row, self.row + "\n" + self.row.replace("description_good", "invented_id"), 1,
            ),
            "duplicate": self.text.replace(self.row, self.row + "\n" + self.row, 1),
            "class": self.text.replace(self.row, self.row.replace("MUST", "SHOULD"), 1),
            "achievement": self.text.replace(self.row, self.row.replace("Prepared", "Met"), 1),
            "owner": self.text.replace(self.row, self.row.removesuffix("M |") + " |", 1),
            "unquoted_id": self.text.replace("`description_good`", "description_good", 1),
        }
        for name, text in mutations.items():
            with self.subTest(name=name), self.assertRaises(ValueError):
                documentation.verify_openssf(text, self.catalog)

    def test_indented_criterion_rows_are_checked(self):
        for indent in (" ", "  ", "   "):
            with self.subTest(indent=repr(indent)):
                valid = self.text.replace(self.row, indent + self.row, 1)
                documentation.verify_openssf(valid, self.catalog)
                invalid_rows = (
                    self.row,
                    self.row.replace("description_good", "invented_id"),
                    self.row.replace("Prepared", "Met"),
                    self.row.replace("MUST", "SHOULD"),
                )
                for invalid in invalid_rows:
                    text = self.text.replace(self.row, self.row + "\n" + indent + invalid, 1)

                    with self.subTest(row=invalid), self.assertRaises(ValueError):
                        documentation.verify_openssf(text, self.catalog)

    def test_same_identifier_across_levels_keeps_its_own_requirement(self):
        self.assertIn("contribution_requirements", self.catalog["levels"]["Passing"]["SHOULD"])
        self.assertIn("contribution_requirements", self.catalog["levels"]["Silver"]["MUST"])

        documentation.verify_openssf(self.text, self.catalog)

    def test_official_mixed_case_identifiers_are_not_normalized(self):
        with self.assertRaisesRegex(ValueError, "criterion mismatch"):
            documentation.verify_openssf(self.text.replace("`require_2FA`", "`require_2fa`"), self.catalog)

    def test_readiness_updates_do_not_change_criterion_identity(self):
        for state in ("Evidence", "External", "People", "Open", "Assess"):
            with self.subTest(state=state):
                documentation.verify_openssf(self.text.replace("Prepared", state), self.catalog)

    def test_corrupt_snapshot_rejects(self):
        catalog = copy.deepcopy(self.catalog)
        catalog["levels"]["Passing"]["MUST"].append("description_good")

        with self.assertRaisesRegex(ValueError, "duplicate snapshot"):
            documentation.verify_openssf(self.text, catalog)


class RequiredDocumentTests(unittest.TestCase):
    SECONDARY_POLICIES = (".github/SECURITY.md", "docs/SECURITY.md", "src/Component/SECURITY.md")
    PUBLICATION_RUNBOOK = "docs/operations/release-publication.md"

    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        subprocess.run(
            ["git", "init", "--quiet", str(self.root)],
            check=True, capture_output=True, text=True,
        )
        documents = (set(documentation.REQUIRED) - set(self.SECONDARY_POLICIES)) | {
            "SECURITY.md", self.PUBLICATION_RUNBOOK,
        }
        for name in documents:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("# Synthetic documentation\n", encoding="utf-8")

        # Root-policy presence must be checked even without a navigation link.
        self.navigation = "# Documentation\n\n" + "".join(
            f"- [{name}]({name})\n" for name in sorted(documents - {"SECURITY.md", "README.md"})
        )
        (self.root / "README.md").write_text(self.navigation, encoding="utf-8")
        catalog = {
            "sourceSha256": "0" * 64,
            "retrieved": "2026-08-26",
            "levels": {
                level: {"MUST": [], "SHOULD": [], "SUGGESTED": []}
                for level in ("Passing", "Silver", "Gold")
            },
        }
        (self.root / "eng").mkdir()
        (self.root / "eng/openssf-criteria.json").write_text(json.dumps(catalog), encoding="utf-8")
        (self.root / "docs/openssf-best-practices.md").write_text(
            "# Synthetic preparation\n\n" + "".join(
                f"## {level}\n\nEmpty synthetic level.\n\n" for level in catalog["levels"]
            ),
            encoding="utf-8",
        )

    def test_complete_repository_with_only_root_policy_passes(self):
        errors, _, _ = documentation.verify_repository(self.root)

        self.assertEqual([], errors, "\n".join(errors))

    def test_missing_root_policy_is_rejected(self):
        (self.root / "SECURITY.md").unlink()

        errors, _, _ = documentation.verify_repository(self.root)

        self.assertEqual(["missing required document: SECURITY.md"], errors)

    def test_missing_publication_runbook_is_rejected_without_navigation_link(self):
        (self.root / self.PUBLICATION_RUNBOOK).unlink()
        navigation = "\n".join(
            line for line in self.navigation.splitlines()
            if self.PUBLICATION_RUNBOOK not in line
        ) + "\n"
        (self.root / "README.md").write_text(navigation, encoding="utf-8")

        errors, _, _ = documentation.verify_repository(self.root)

        self.assertEqual([f"missing required document: {self.PUBLICATION_RUNBOOK}"], errors)

    def test_other_locations_do_not_replace_missing_root_policy(self):
        (self.root / "SECURITY.md").unlink()
        for name in self.SECONDARY_POLICIES:
            with self.subTest(path=name):
                policy = self.add_secondary_policy(name)
                try:
                    errors, _, _ = documentation.verify_repository(self.root)

                    self.assertIn("missing required document: SECURITY.md", errors)
                finally:
                    policy.unlink()
                    (self.root / "README.md").write_text(self.navigation, encoding="utf-8")

    def test_additional_policy_is_rejected_even_when_linked(self):
        for name in self.SECONDARY_POLICIES:
            with self.subTest(path=name):
                policy = self.add_secondary_policy(name)
                try:
                    errors, _, _ = documentation.verify_repository(self.root)

                    self.assertEqual(
                        [f"security policy must be maintained only at repository root: {name}"], errors,
                    )
                finally:
                    policy.unlink()
                    (self.root / "README.md").write_text(self.navigation, encoding="utf-8")

    def add_secondary_policy(self, name):
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("# Additional policy\n", encoding="utf-8")
        (self.root / "README.md").write_text(
            self.navigation + f"- [Additional policy]({name})\n", encoding="utf-8",
        )
        return path


class RepositoryTests(unittest.TestCase):
    def test_current_repository_contract(self):
        errors, documents, decisions = documentation.verify_repository(ROOT)

        self.assertEqual([], errors, "\n".join(errors))
        self.assertGreater(documents, 20)
        self.assertGreaterEqual(decisions, 7)

    def test_current_openssf_map_matches_all_official_snapshot_levels(self):
        text = (ROOT / "docs/openssf-best-practices.md").read_text(encoding="utf-8")
        catalog = json.loads((ROOT / "eng/openssf-criteria.json").read_text(encoding="utf-8"))
        documentation.verify_openssf(text, catalog)
        counts = [sum(len(values) for values in level.values()) for level in catalog["levels"].values()]

        self.assertEqual([67, 55, 23], counts)


if __name__ == "__main__":
    unittest.main()
