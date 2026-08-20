import contextlib
import importlib.util
import io
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).parents[1] / "verify-coverage.py"
SPEC = importlib.util.spec_from_file_location("verify_coverage", SCRIPT)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError("The coverage verifier could not be loaded.")

VERIFY_COVERAGE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFY_COVERAGE)


class VerifyCoverageTests(unittest.TestCase):
    def test_reports_are_merged_conservatively_per_product_line(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            self.write_report(root / "first.cobertura.xml", line_hits=(1, 0), branches=(1, 2))
            self.write_report(root / "second.cobertura.xml", line_hits=(0, 1), branches=(2, 2))

            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                result = VERIFY_COVERAGE.verify(
                    root,
                    [VERIFY_COVERAGE.CoverageThreshold("Product", 1.0, 1.0)],
                )

            self.assertEqual(0, result)
            self.assertIn("lines 2/2 (1.0000), branches 2/2 (1.0000)", output.getvalue())

    def test_missing_assembly_and_threshold_regression_fail(self) -> None:
        with tempfile.TemporaryDirectory() as temporary_directory:
            root = Path(temporary_directory)
            self.write_report(root / "coverage.cobertura.xml", line_hits=(1, 0), branches=(1, 2))

            with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                below_threshold = VERIFY_COVERAGE.verify(
                    root,
                    [VERIFY_COVERAGE.CoverageThreshold("Product", 0.75, 0.75)],
                )
                missing = VERIFY_COVERAGE.verify(
                    root,
                    [VERIFY_COVERAGE.CoverageThreshold("Missing", 0.0, 0.0)],
                )

            self.assertEqual(1, below_threshold)
            self.assertEqual(1, missing)

    @staticmethod
    def write_report(
        path: Path,
        line_hits: tuple[int, int],
        branches: tuple[int, int],
    ) -> None:
        path.write_text(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
            "<coverage><packages><package name=\"Product\"><classes>"
            "<class name=\"Product.Example\" filename=\"/workspace/src/Product/Example.cs\">"
            "<lines>"
            f"<line number=\"10\" hits=\"{line_hits[0]}\" branch=\"true\" "
            f"condition-coverage=\"50% ({branches[0]}/{branches[1]})\" />"
            f"<line number=\"11\" hits=\"{line_hits[1]}\" branch=\"false\" />"
            "</lines></class></classes></package></packages></coverage>",
            encoding="utf-8",
        )


if __name__ == "__main__":
    unittest.main()
