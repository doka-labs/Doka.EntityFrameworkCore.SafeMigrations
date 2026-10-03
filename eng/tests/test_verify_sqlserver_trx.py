"""Positive and negative SQL Server live-test evidence checks."""

import importlib.util
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "verify-sqlserver-trx.py"
SPEC = importlib.util.spec_from_file_location("verify_sqlserver_trx", SCRIPT)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class VerifySqlServerTrxTests(unittest.TestCase):
    """Keep skipped x86-64 integration runs from satisfying the release gate."""

    def write_report(self, total: int, outcomes: list[str]) -> Path:
        """Create a minimal TRX report for one verification case."""

        directory = Path(tempfile.mkdtemp())
        results = "".join(
            f'<UnitTestResult outcome="{outcome}" />' for outcome in outcomes
        )
        report = directory / "results.trx"
        report.write_text(
            f'<TestRun><Results>{results}</Results><ResultSummary>'
            f'<Counters total="{total}" /></ResultSummary></TestRun>',
            encoding="utf-8",
        )

        self.addCleanup(directory.rmdir)
        self.addCleanup(report.unlink)

        return report

    def test_all_passed_results_are_accepted(self) -> None:
        report = self.write_report(2, ["Passed", "Passed"])

        result = MODULE.verify(report)

        self.assertIsNone(result)

    def test_skipped_result_is_rejected(self) -> None:
        report = self.write_report(2, ["Passed", "NotExecuted"])

        with self.assertRaisesRegex(ValueError, "skipped or failed"):
            MODULE.verify(report)

    def test_zero_results_are_rejected(self) -> None:
        report = self.write_report(0, [])

        with self.assertRaisesRegex(ValueError, "no test results"):
            MODULE.verify(report)

    def test_incomplete_results_are_rejected(self) -> None:
        report = self.write_report(2, ["Passed"])

        with self.assertRaisesRegex(ValueError, "incomplete"):
            MODULE.verify(report)

    def test_missing_counters_are_rejected(self) -> None:
        directory = Path(tempfile.mkdtemp())
        report = directory / "results.trx"
        report.write_text('<TestRun><Results /></TestRun>', encoding="utf-8")
        self.addCleanup(directory.rmdir)
        self.addCleanup(report.unlink)

        with self.assertRaisesRegex(ValueError, "no TRX counters"):
            MODULE.verify(report)
