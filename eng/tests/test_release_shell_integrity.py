"""Exercise the real qualification shell blocks without builds or remote writes."""

from __future__ import annotations

import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
SBOM_SCRIPT = REPOSITORY_ROOT / "eng" / "generate-sbom.sh"
TOOLING_SCRIPT = REPOSITORY_ROOT / "eng" / "verify-ef-tooling.sh"


class ReleaseShellIntegrityTests(unittest.TestCase):
    """Check success inventories and fail-fast copy/hash boundaries from source."""

    def test_output_guards_distinguish_empty_from_nonempty_or_unreadable(self) -> None:
        scripts = (("generate-sbom.sh", '\ncase "$(uname'), ("qualify-packages.sh", '\ntemporary_root='))
        for file_name, boundary in scripts:
            source = (REPOSITORY_ROOT / "eng" / file_name).read_text(encoding="utf-8")
            start = source.index('mkdir -p "$output_dir"\n')
            block = source[start:source.index(boundary, start)] + "\nprintf 'next gate\\n'\n"
            for state in ("empty", "nonempty", "enumeration-failed"):
                with self.subTest(script=file_name, state=state), tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    output = root / "existing evidence"
                    output.mkdir()
                    sentinel = output / "sentinel"
                    if state != "empty":
                        sentinel.write_bytes(b"retained evidence")
                    environment = self.environment(root)
                    environment["output_dir"] = str(output)
                    if state == "enumeration-failed":
                        self.stub(root, "find", "echo 'injected enumeration failure' >&2\nexit 7\n")
                    result = self.run_block(block, root, environment)

                    if state == "empty":
                        self.assertEqual(result.returncode, 0, result.stderr)
                        self.assertIn("next gate", result.stdout)
                    else:
                        self.assertNotEqual(result.returncode, 0, result.stdout)
                        self.assertNotIn("next gate", result.stdout)
                        self.assertEqual(sentinel.read_bytes(), b"retained evidence")

    def test_sbom_drop_copy_propagates_failure(self) -> None:
        source = SBOM_SCRIPT.read_text(encoding="utf-8")
        block = source.split('drop_dir="$work_dir/drop"\n', 1)[1].split('\ncomponent_root=', 1)[0]
        block = 'drop_dir="$work_dir/drop"\n' + block
        for fail in (False, True):
            with self.subTest(fail=fail), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                packages = root / "qualified packages"
                packages.mkdir()
                expected = [f"package-{number}.{suffix}" for number in range(3) for suffix in ("nupkg", "snupkg")]
                expected.append("SHA256SUMS")
                for name in (*expected, "SYMBOLS.json"):
                    (packages / name).write_text(name, encoding="ascii")
                environment = self.environment(root)
                environment.update({"package_dir": str(packages), "work_dir": str(root)})
                if fail:
                    self.stub(root, "cp", "echo 'injected copy failure' >&2\nexit 7\n")
                result = self.run_block(block, root, environment)

                if fail:
                    self.assertNotEqual(result.returncode, 0, result.stdout)
                    self.assertIn("injected copy failure", result.stderr)
                else:
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertEqual(sorted(path.name for path in (root / "drop").iterdir()), sorted(expected))
                    for name in expected:
                        self.assertEqual((root / "drop" / name).read_bytes(), (packages / name).read_bytes())

    def test_sbom_checksum_failure_cannot_publish_a_partial_manifest(self) -> None:
        source = SBOM_SCRIPT.read_text(encoding="utf-8")
        block = source.split('\n(\n    cd "$output_dir"\n', 1)[1].split('\n)\n', 1)[0]
        block = '(\n    cd "$output_dir"\n' + block + '\n)\n'
        for fail in (False, True):
            with self.subTest(fail=fail), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                output = root / "sbom"
                manifest = output / "_manifest/spdx_2.2/manifest.spdx.json"
                manifest.parent.mkdir(parents=True)
                manifest.write_text("manifest", encoding="ascii")
                (output / "validation.json").write_text("validation", encoding="ascii")
                environment = self.environment(root)
                environment.update({"output_dir": str(output), "work_dir": str(root)})
                if fail:
                    self.stub(root, "shasum", (
                        'for argument in "$@"; do\n'
                        '  if [[ "$argument" == *manifest.spdx.json ]]; then\n'
                        "    echo 'injected SBOM hash failure' >&2\n"
                        "    exit 7\n"
                        "  fi\n"
                        "done\n"
                        'exec "$REAL_SHASUM" "$@"\n'
                    ))
                result = self.run_block(block, root, environment)

                if fail:
                    self.assertNotEqual(result.returncode, 0, result.stdout)
                    self.assertIn("injected SBOM hash failure", result.stderr)
                    self.assertFalse((output / "SHA256SUMS").exists())
                else:
                    self.assertEqual(result.returncode, 0, result.stderr)
                    lines = (output / "SHA256SUMS").read_text().splitlines()
                    self.assertEqual(len(lines), 2)
                    self.assertTrue(any(line.endswith("./_manifest/spdx_2.2/manifest.spdx.json") for line in lines))
                    self.assertTrue(any(line.endswith("./validation.json") for line in lines))

    def test_source_lock_inventory_propagates_hash_failure_and_keeps_exclusions(self) -> None:
        source = TOOLING_SCRIPT.read_text(encoding="utf-8")
        match = re.search(r"(?ms)^hash_source_lockfiles\(\) \{\n.*?^\}", source)
        self.assertIsNotNone(match)
        assert match is not None
        block = match.group(0) + '\nhash_source_lockfiles "$output_file"\n'

        for fail in (False, True):
            with self.subTest(fail=fail), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                source_root = root / "source with spaces"
                included = ("a/packages.lock.json", "b/packages.lock.json")
                excluded = tuple(
                    f"{name}/packages.lock.json" for name in (".git", ".fastembed_cache", "artifacts", "bin", "obj")
                )
                for relative in (*included, *excluded):
                    path = source_root / relative
                    path.parent.mkdir(parents=True, exist_ok=True)
                    path.write_text(relative, encoding="ascii")
                output = root / "lock-hashes"
                environment = self.environment(root)
                environment.update({"source_root": str(source_root), "output_file": str(output)})
                if fail:
                    self.stub(root, "shasum", '"$REAL_SHASUM" "$@"\necho "injected lock hash failure" >&2\nexit 7\n')
                result = self.run_block(block, root, environment)

                if fail:
                    self.assertNotEqual(result.returncode, 0, result.stdout)
                    self.assertIn("injected lock hash failure", result.stderr)
                else:
                    self.assertEqual(result.returncode, 0, result.stderr)
                    lines = output.read_text().splitlines()
                    self.assertEqual(len(lines), 2)
                    for relative in included:
                        self.assertTrue(any(line.endswith(str(source_root / relative)) for line in lines))

    def test_tooling_hash_failure_stops_before_copy_or_docker_and_cleans_temporary_files(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            environment = self.environment(root)
            environment["TMPDIR"] = str(root)
            self.stub(root, "shasum", "echo 'injected lock hash failure' >&2\nexit 7\n")
            for command in ("rsync", "docker"):
                self.stub(root, command, 'printf invoked >> "$TMPDIR/later-command"\nexit 23\n')
            result = subprocess.run(
                ["bash", str(TOOLING_SCRIPT), "postgres", "unused-fixture-image", "unused-fixture-version"],
                cwd=root, env=environment, capture_output=True, text=True, check=False, timeout=10,
            )

            self.assertNotEqual(result.returncode, 0, result.stdout)
            self.assertIn("injected lock hash failure", result.stderr)
            self.assertFalse((root / "later-command").exists())
            self.assertEqual(list(root.glob("safemigrations-tooling.*")), [])

    @staticmethod
    def environment(root: Path) -> dict[str, str]:
        environment = os.environ.copy()
        real_shasum = shutil.which("shasum")
        assert real_shasum is not None
        (root / "bin").mkdir()
        environment.update({
            "PATH": f"{root / 'bin'}{os.pathsep}{environment['PATH']}",
            "REAL_SHASUM": real_shasum,
        })
        return environment

    @staticmethod
    def stub(root: Path, command: str, body: str) -> None:
        path = root / "bin" / command
        path.write_text("#!/usr/bin/env bash\n" + body, encoding="ascii")
        path.chmod(0o755)

    @staticmethod
    def run_block(block: str, root: Path, environment: dict[str, str]) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            ["bash", "--noprofile", "--norc", "-e", "-u", "-o", "pipefail", "-c", block],
            cwd=root, env=environment, capture_output=True, text=True, check=False, timeout=10,
        )


if __name__ == "__main__":
    unittest.main()
