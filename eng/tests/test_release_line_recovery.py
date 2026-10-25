"""Guard real runner skip semantics before minting replacement admission."""

import copy
import tempfile
import unittest
from pathlib import Path

from eng.release import publication
from eng.tests.test_release_publication import (
    LINE_HASH,
    PROVENANCE_NAME,
    QUALIFIED_NAME,
    FakeGitHub,
    candidate,
    environment,
    frozen,
)


def skipped_steps():
    """Match the runner's completed/skipped steps, including precondition timestamps."""
    return [{"name": name, "status": "completed", "conclusion": "skipped",
             "started_at": "2026-10-25T03:00:00Z", "completed_at": "2026-10-25T03:00:00Z"}
            for name in sorted(publication.WRITE_STEPS)]


class ReleaseLineRecoveryTests(unittest.TestCase):
    """A skipped conclusion permits retry; an executed or ambiguous write never does."""

    def test_timestamped_skipped_write_steps_permit_the_actual_admission_retry(self):
        # Arrange
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            candidate(root)
            api = FakeGitHub()
            api.jobs[0].update(conclusion="failure", steps=skipped_steps())

            # Act
            receipt, restored = publication.admit(
                frozen(), LINE_HASH, root, QUALIFIED_NAME, PROVENANCE_NAME, api, environment("2"))

            # Assert
            self.assertFalse(restored)
            self.assertEqual("1", receipt["identity"]["qualificationAttempt"])
            self.assertEqual("2", receipt["identity"]["originalPublicationAttempt"])
            self.assertEqual(13, len(publication.candidate_manifest(root, receipt["identity"]["version"])))

    def test_skipped_step_timestamps_do_not_change_the_safe_history_verdict(self):
        for stamp in (None, "2026-10-25T03:00:00Z"):
            with self.subTest(stamp=stamp):
                # Arrange
                api = FakeGitHub()
                steps = skipped_steps()
                for step in steps:
                    step["started_at"] = stamp

                api.jobs[0].update(conclusion="failure", steps=steps)

                # Act
                publication.prior_publication_is_unstarted(frozen(), "2", api)

                # Assert
                self.assertEqual("jobs", api.calls[-1].split("/")[-1])

    def test_non_skipped_write_is_rejected_even_without_a_start_timestamp(self):
        for conclusion in ("success", "failure", "cancelled", "neutral", "unknown", None):
            with self.subTest(conclusion=conclusion):
                # Arrange
                api = FakeGitHub()
                steps = skipped_steps()
                steps[0].update(conclusion=conclusion, started_at=None)
                api.jobs[0].update(conclusion="failure", steps=steps)

                # Act / Assert
                with self.assertRaises(publication.PublicationAdmissionError):
                    publication.prior_publication_is_unstarted(frozen(), "2", api)

    def test_duplicate_skipped_name_cannot_hide_an_executed_write(self):
        # Arrange
        api = FakeGitHub()
        steps = skipped_steps()
        for step in steps:
            step["started_at"] = None

        executed = copy.deepcopy(steps[0])
        executed["conclusion"] = "success"
        api.jobs[0].update(conclusion="failure", steps=[executed, *steps])

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            publication.prior_publication_is_unstarted(frozen(), "2", api)

    def test_duplicate_name_remains_ambiguous_even_when_every_write_is_skipped(self):
        # Arrange
        api = FakeGitHub()
        steps = skipped_steps()
        for step in steps:
            step["started_at"] = None

        api.jobs[0].update(conclusion="failure", steps=[*steps, copy.deepcopy(steps[0])])

        # Act / Assert
        with self.assertRaises(publication.PublicationAdmissionError):
            publication.prior_publication_is_unstarted(frozen(), "2", api)

    def test_missing_write_and_unfinished_or_missing_publisher_remain_blocked(self):
        for mutation in ("missing-write", "running-publisher", "missing-publisher"):
            with self.subTest(mutation=mutation):
                # Arrange
                api = FakeGitHub()
                api.jobs[0].update(conclusion="failure", steps=skipped_steps())
                if mutation == "missing-write":
                    api.jobs[0]["steps"].pop()
                elif mutation == "running-publisher":
                    api.jobs[0]["status"] = "in_progress"
                else:
                    api.jobs = []

                # Act / Assert
                with self.assertRaises(publication.PublicationAdmissionError):
                    publication.prior_publication_is_unstarted(frozen(), "2", api)


if __name__ == "__main__":
    unittest.main()
