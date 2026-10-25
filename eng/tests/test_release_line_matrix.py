"""Reject partial native-matrix recognition before accepting scheduled evidence."""

import itertools
import re
import unittest
from pathlib import Path

from eng.release import maintenance


ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github/workflows/quality-gates.yml"
NATIVE_JOBS = ("mysql-engine", "postgresql-engine", "sqlserver-engine")


def job_source(source: str, name: str) -> str:
    """Select one real job independently of the production matrix parser."""
    marker = f"  {name}:\n"
    body = source.split(marker, 1)[1]
    boundary = re.search(r"^  [A-Za-z_][A-Za-z0-9_-]*:\n", body, re.MULTILINE)

    return marker + (body[:boundary.start()] if boundary else body)


def first_row(source: str, name: str) -> tuple[str, dict[str, str]]:
    """Read the retained fixture row solely to construct controlled permutations."""
    body = job_source(source, name)
    match = re.search(r"^          - [a-z]+: [^\n]+(?:\n            [a-z]+: [^\n]+)*", body, re.MULTILINE)
    if match is None:
        raise AssertionError("The real fixture must contain one native include row.")

    row = match[0]
    fields = dict(re.findall(r"(?:^          - |^            )([a-z]+): (.+)$", row, re.MULTILINE))

    return row, fields


def mapping_row(fields: dict[str, str], order: tuple[str, ...], *, decorate=False, bare=False) -> str:
    """Produce equivalent block mappings without sharing the parser's recognizer."""
    output = ["          -"] if bare else []
    for index, field in enumerate(order):
        prefix = "            " if bare or index else "          - "
        value = fields[field]
        if decorate:
            if value[0] in "\"'" and value[-1] == value[0]:
                value = value[1:-1]

            value = f"'{value}' # reviewed matrix scalar"

        output.append(f"{prefix}{field}: {value}")
        if decorate and index + 1 < len(order):
            output.extend(["", "            # A mapping key's order does not select a producer."])

    return "\n".join(output)


class NativeMatrixTests(unittest.TestCase):
    """Every declared cell must be represented or rejected, never silently omitted."""

    def test_each_native_row_key_permutation_preserves_complete_inventory(self):
        """MySQL, PostgreSQL and SQL Server remain independent of mapping order."""
        for job in NATIVE_JOBS:
            source = WORKFLOW.read_text(encoding="ascii")
            original, fields = first_row(source, job)
            for order in itertools.permutations(fields):
                with self.subTest(job=job, order=order):
                    # Arrange
                    changed = source.replace(original, mapping_row(fields, order), 1)
                    expected = maintenance.qualification_producers(source)

                    # Act
                    observed = maintenance.qualification_producers(changed)

                    # Assert
                    self.assertEqual(19, len(observed))
                    self.assertEqual(expected, observed)

    def test_reordered_cell_cannot_hide_its_skipped_actual_job(self):
        """The former 19-to-18 loss must fail through the real evidence verifier."""
        # Arrange
        source = WORKFLOW.read_text(encoding="ascii")
        original, fields = first_row(source, "mysql-engine")
        changed = source.replace(original, mapping_row(fields, ("version", "engine", "image")), 1)
        all_names = maintenance.qualification_producers(source)
        expected = maintenance.qualification_producers(changed)
        blocked = f"Full qualification / {fields['engine']} {fields['version']}"
        jobs = [{"id": index + 1, "name": name, "status": "completed",
                 "conclusion": "skipped" if name == blocked else "success"}
                for index, name in enumerate(all_names)]

        class JobsApi:
            """Retain every real observed job, including the deliberately skipped cell."""

            def request(self, route):
                """Return a complete short-page inventory without any network access."""
                return {"total_count": len(jobs), "jobs": jobs}

        # Act / Assert
        with self.assertRaises(maintenance.MaintenanceError):
            maintenance.verify_jobs(JobsApi(), "owner/repository", 1, expected)

    def test_quoted_commented_and_spaced_native_rows_preserve_inventory(self):
        """Image-first mappings can contain ordinary quoted values and comments."""
        for job in NATIVE_JOBS:
            with self.subTest(job=job):
                # Arrange
                source = WORKFLOW.read_text(encoding="ascii")
                original, fields = first_row(source, job)
                order = ("image", *[field for field in fields if field != "image"])
                changed = source.replace(original, mapping_row(fields, order, decorate=True), 1)
                expected = maintenance.qualification_producers(source)

                # Act
                observed = maintenance.qualification_producers(changed)

                # Assert
                self.assertEqual(expected, observed)

    def test_bare_block_mapping_row_preserves_inventory(self):
        """A standalone list dash still introduces exactly one declared native cell."""
        for job in NATIVE_JOBS:
            with self.subTest(job=job):
                # Arrange
                source = WORKFLOW.read_text(encoding="ascii")
                original, fields = first_row(source, job)
                changed = source.replace(original, mapping_row(fields, tuple(reversed(fields)), bare=True), 1)
                expected = maintenance.qualification_producers(source)

                # Act
                observed = maintenance.qualification_producers(changed)

                # Assert
                self.assertEqual(expected, observed)

    def test_partial_unknown_or_duplicate_fields_reject_the_entire_matrix(self):
        """One bad cell cannot be omitted while the remaining cells still qualify."""
        for job in NATIVE_JOBS:
            source = WORKFLOW.read_text(encoding="ascii")
            original, fields = first_row(source, job)
            variants = {"missing-" + key: mapping_row(fields, tuple(name for name in fields if name != key))
                        for key in fields}
            variants.update({"unknown": original + "\n            unexpected: value",
                             "duplicate-version": original + f"\n            version: {fields['version']}",
                             "null-version": original.replace("version: " + fields["version"], "version: null"),
                             "empty-image": original.replace("image: " + fields["image"], "image:"),
                             "null-image": original.replace("image: " + fields["image"], "image: null"),
                             "boolean-image": original.replace("image: " + fields["image"], "image: true"),
                             "alias-image": original.replace("image: " + fields["image"], "image: *shared")})
            if "engine" in fields:
                variants["unknown-engine"] = original.replace("engine: mysql", "engine: unrecognized")

            for name, replacement in variants.items():
                with self.subTest(job=job, variant=name):
                    # Arrange
                    changed = source.replace(original, replacement, 1)

                    # Act / Assert
                    with self.assertRaises(maintenance.MaintenanceError):
                        maintenance.qualification_producers(changed)

    def test_unsupported_matrix_composition_is_rejected(self):
        """Additional axes, excludes, flow values and aliases need explicit reader support."""
        source = WORKFLOW.read_text(encoding="ascii")
        original, _ = first_row(source, "mysql-engine")
        body = job_source(source, "mysql-engine")
        variants = {
            "axis": body.replace("        include:\n", "        version: [8.4.13]\n        include:\n", 1),
            "exclude": body.replace("        include:\n", "        exclude: []\n        include:\n", 1),
            "duplicate-include": body.replace("        include:\n", "        include: []\n        include:\n", 1),
            "flow-row": body.replace(original, "          - {engine: mysql, version: 8.4.13, image: mysql}", 1),
            "row-alias": body.replace(original, "          - *shared", 1),
            "merged-row": body.replace(original, "          - <<: *shared", 1),
            "duplicate-matrix": body.replace("      matrix:\n", "      matrix: {}\n      matrix:\n", 1),
            "duplicate-strategy": body.replace("    strategy:\n", "    strategy: {}\n    strategy:\n", 1),
        }
        for name, replacement in variants.items():
            with self.subTest(variant=name):
                # Arrange
                changed = source.replace(body, replacement, 1)

                # Act / Assert
                with self.assertRaises(maintenance.MaintenanceError):
                    maintenance.qualification_producers(changed)

    def test_duplicate_jobs_and_unrecognized_job_ids_cannot_be_lost(self):
        """Dictionary construction must not overwrite or regex-filter job declarations."""
        source = WORKFLOW.read_text(encoding="ascii")
        variants = {"duplicate": source + "\n" + job_source(source, "mysql-engine"),
                    "unrecognized": source + "\n  unexpected_job:\n    name: Extra\n    runs-on: ubuntu-24.04\n"}
        for name, changed in variants.items():
            with self.subTest(variant=name):
                # Arrange
                expected_error = maintenance.MaintenanceError

                # Act / Assert
                with self.assertRaises(expected_error):
                    maintenance.qualification_producers(changed)

    def test_unreviewed_matrix_on_fixed_name_job_is_rejected(self):
        """A fixed-name producer must not silently stand for additional matrix jobs."""
        # Arrange
        source = WORKFLOW.read_text(encoding="ascii")
        original = job_source(source, "package-and-core")
        modified = original.replace("    runs-on: ubuntu-24.04\n", "    runs-on: ubuntu-24.04\n"
                                    "    strategy:\n      matrix:\n        include:\n          - version: 10\n", 1)
        changed = source.replace(original, modified, 1)

        # Act / Assert
        with self.assertRaises(maintenance.MaintenanceError):
            maintenance.qualification_producers(changed)


if __name__ == "__main__":
    unittest.main()
