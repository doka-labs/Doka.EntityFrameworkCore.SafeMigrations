"""Exercise manifest-only dependency staging without restore, tokens or network."""

import hashlib
import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
STAGER = REPOSITORY_ROOT / "eng/prepare-dependency-snapshot-inputs.py"


def load_stager():
    """Load the production helper for finite-size and mutation adversarial probes."""
    spec = importlib.util.spec_from_file_location("snapshot_stager", STAGER)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)

    return module


def create_project(root: Path, name: str = "Example", packages: bool = True,
                   artifact_name: str | None = None, assets_version: int = 3) -> Path:
    """Create one self-consistent synthetic restored project and return its path."""
    project = root / "src" / name / f"{name}.csproj"
    project.parent.mkdir(parents=True, exist_ok=True)
    project.write_text('<Project Sdk="Microsoft.NET.Sdk" />\n', encoding="ascii")
    assets = (root / "artifacts/obj" / artifact_name / "project.assets.json" if artifact_name
              else project.parent / "obj/project.assets.json")
    assets.parent.mkdir(parents=True)
    package = {"Sample/1.0.0": {"type": "package", "sha512": "fixture-only"}} if packages else {}
    payload = {
        "version": assets_version,
        "targets": {"net10.0": {"Sample/1.0.0": {"type": "package"}} if packages else {}},
        "libraries": package,
        "project": {
            "restore": {
                "projectPath": str(project),
                "projectUniqueName": str(project),
                "outputPath": str(assets.parent),
            },
        },
    }
    assets.write_text(json.dumps(payload), encoding="ascii")

    return project


def write_solution(root: Path, projects: list[Path]) -> None:
    """Write only the selected solution projects, preserving their path spellings."""
    items = "".join(f'<Project Path="{project.relative_to(root).as_posix()}" />' for project in projects)
    (root / "Example.slnx").write_text(f"<Solution>{items}</Solution>", encoding="ascii")


def stage(source: Path, destination: Path) -> subprocess.CompletedProcess[str]:
    """Execute the real CLI so argument parsing and failure boundaries are covered."""
    return subprocess.run(
        [sys.executable, str(STAGER), str(source), str(destination)],
        capture_output=True,
        text=True,
        check=False,
    )

def write_lock(project: Path) -> None:
    """Create a lock agreeing with the fixture's package identity and content hash."""
    value = {
        "version": 2,
        "dependencies": {
            "net10.0": {
                "Sample": {"type": "Direct", "resolved": "1.0.0", "contentHash": "fixture-only"},
            },
        },
    }
    (project.parent / "packages.lock.json").write_text(json.dumps(value), encoding="ascii")


def create_bundle(root: Path, locked: bool = True, artifact_name: str | None = None) -> tuple:
    """Use real producer output while binding committed manifests independently."""
    helper = load_stager()
    source = root / "source"
    project = create_project(source, artifact_name=artifact_name)
    write_solution(source, [project])
    if locked:
        write_lock(project)

    staged = root / "producer-output"
    helper.stage(source, staged)
    entries = {path.relative_to(staged).as_posix(): path.read_bytes()
               for path in staged.rglob("*") if path.is_file()}
    blobs = []
    for relative, content in entries.items():
        if Path(relative).name in (".snapshot-root", "project.assets.json"):
            continue

        digest = hashlib.sha1(b"blob " + str(len(content)).encode("ascii") + b"\0" + content).hexdigest()
        blobs.append({"path": relative, "mode": "100644", "type": "blob", "sha": digest})

    return helper, source, root / "received", {"truncated": False, "tree": blobs}, entries


def write_payload(path: Path, entries: dict) -> None:
    """Materialize the already staged data payload without archive interpretation."""
    path.mkdir()
    for name, content in entries.items():
        target = path / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(content)


def changed_assets(entries: dict, mutate) -> None:
    """Mutate only restored data; committed source identities remain authoritative."""
    path = next(name for name in entries if Path(name).name == "project.assets.json")
    value = json.loads(entries[path])
    mutate(value)
    entries[path] = json.dumps(value).encode("ascii")


class DependencySnapshotInputTests(unittest.TestCase):
    """Keep resolved input graphs complete and isolated from executable content."""

    def test_stages_exact_data_for_all_solution_projects(self) -> None:
        """Staging retains exact bytes but excludes arbitrary scripts and binaries."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            source = Path(temporary_root).resolve() / "source"
            first = create_project(source)
            second = create_project(source, "FrameworkOnly", packages=False)
            write_solution(source, [first, second])
            write_lock(first)
            (first.parent / "hook.sh").write_text("exit 99\n", encoding="ascii")
            (first.parent / "payload.dll").write_bytes(b"not a real binary")
            destination = Path(temporary_root).resolve() / "output"

            # Act
            result = stage(source, destination)

            # Assert
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("2 projects", result.stdout)
            self.assertEqual(str(source.resolve()) + "\n", (destination / ".snapshot-root").read_text())
            expected = {
                ".snapshot-root",
                "Example.slnx",
                "src/Example/Example.csproj",
                "src/Example/obj/project.assets.json",
                "src/Example/packages.lock.json",
                "src/FrameworkOnly/FrameworkOnly.csproj",
                "src/FrameworkOnly/obj/project.assets.json",
            }
            self.assertEqual(expected, {file.relative_to(destination).as_posix()
                                       for file in destination.rglob("*") if file.is_file()})
            for relative in expected - {".snapshot-root"}:
                self.assertEqual((source / relative).read_bytes(), (destination / relative).read_bytes())

    def test_stages_central_artifacts_and_source_design_profiles(self) -> None:
        """Actual restore owner metadata, not a guessed output name, identifies each project."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            source = Path(temporary_root).resolve() / "source"
            first = create_project(source, artifact_name="Example", assets_version=4)
            second = create_project(source, "Consumer", artifact_name="Consumer.Source.Design", assets_version=4)
            write_solution(source, [first, second])
            destination = Path(temporary_root).resolve() / "output"

            # Act
            result = stage(source, destination)

            # Assert
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("2 projects", result.stdout)
            self.assertEqual(
                {"artifacts/obj/Example/project.assets.json",
                 "artifacts/obj/Consumer.Source.Design/project.assets.json"},
                {path.relative_to(destination).as_posix()
                 for path in destination.rglob("project.assets.json")})
            for relative in ("artifacts/obj/Example/project.assets.json",
                             "artifacts/obj/Consumer.Source.Design/project.assets.json"):
                self.assertEqual((source / relative).read_bytes(), (destination / relative).read_bytes())

    def test_rejects_two_restores_for_one_project(self) -> None:
        """Alternative output profiles must not silently win by enumeration order."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            source = Path(temporary_root).resolve() / "source"
            project = create_project(source)
            write_solution(source, [project])
            duplicate = source / "artifacts/obj/Example.Source.Design/project.assets.json"
            duplicate.parent.mkdir(parents=True)
            payload = json.loads((project.parent / "obj/project.assets.json").read_text())
            payload["project"]["restore"]["outputPath"] = str(duplicate.parent)
            duplicate.write_text(json.dumps(payload), encoding="ascii")
            destination = Path(temporary_root).resolve() / "output"

            # Act
            result = stage(source, destination)

            # Assert
            self.assertNotEqual(0, result.returncode)
            self.assertEqual("could not validate dependency snapshot inputs\n", result.stderr)
            self.assertFalse(destination.exists())

    def test_rejects_central_assets_for_a_project_outside_the_solution(self) -> None:
        """A fresh snapshot cannot borrow an unrelated project's restored graph."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            source = Path(temporary_root).resolve() / "source"
            project = create_project(source)
            create_project(source, "Unlisted", artifact_name="Unlisted.Source.Design")
            write_solution(source, [project])
            destination = Path(temporary_root).resolve() / "output"

            # Act
            result = stage(source, destination)

            # Assert
            self.assertNotEqual(0, result.returncode)
            self.assertEqual("could not validate dependency snapshot inputs\n", result.stderr)
            self.assertFalse(destination.exists())

    def test_rejects_assets_with_a_different_declared_output_directory(self) -> None:
        """An assets filename alone cannot certify the directory that NuGet actually restored."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            source = Path(temporary_root).resolve() / "source"
            project = create_project(source, artifact_name="Example.Source.Design")
            write_solution(source, [project])
            assets = source / "artifacts/obj/Example.Source.Design/project.assets.json"
            payload = json.loads(assets.read_text())
            payload["project"]["restore"]["outputPath"] = str(source / "artifacts/obj/Fake")
            assets.write_text(json.dumps(payload), encoding="ascii")
            destination = Path(temporary_root).resolve() / "output"

            # Act
            result = stage(source, destination)

            # Assert
            self.assertNotEqual(0, result.returncode)
            self.assertEqual("could not validate dependency snapshot inputs\n", result.stderr)
            self.assertFalse(destination.exists())

    def test_rejects_unknown_assets_schema(self) -> None:
        """A later SDK format needs review instead of being assumed shape-compatible."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            source = Path(temporary_root).resolve() / "source"
            project = create_project(source, artifact_name="Example", assets_version=5)
            write_solution(source, [project])
            destination = Path(temporary_root).resolve() / "output"

            # Act
            result = stage(source, destination)

            # Assert
            self.assertNotEqual(0, result.returncode)
            self.assertEqual("could not validate dependency snapshot inputs\n", result.stderr)
            self.assertFalse(destination.exists())

    def test_missing_assets_fail_before_any_file_is_staged(self) -> None:
        """A half-restored solution cannot be published as a complete graph."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            source = Path(temporary_root).resolve() / "source"
            first = create_project(source)
            second = create_project(source, "Missing")
            write_solution(source, [first, second])
            (second.parent / "obj/project.assets.json").unlink()
            destination = Path(temporary_root).resolve() / "output"

            # Act
            result = stage(source, destination)

            # Assert
            self.assertNotEqual(0, result.returncode)
            self.assertEqual("could not validate dependency snapshot inputs\n", result.stderr)
            self.assertFalse(destination.exists())

    def test_rejects_inconsistent_restore_project_identity(self) -> None:
        """The stored absolute identity must refer to the exact selected source project."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            source = Path(temporary_root).resolve() / "source"
            project = create_project(source)
            write_solution(source, [project])
            assets = project.parent / "obj/project.assets.json"
            payload = json.loads(assets.read_text())
            payload["project"]["restore"]["projectUniqueName"] = str(Path(temporary_root).resolve() / "other.csproj")
            assets.write_text(json.dumps(payload), encoding="ascii")
            destination = Path(temporary_root).resolve() / "output"

            # Act
            result = stage(source, destination)

            # Assert
            self.assertNotEqual(0, result.returncode)
            self.assertEqual("could not validate dependency snapshot inputs\n", result.stderr)
            self.assertFalse(destination.exists())

    def test_rejects_symlinked_assets(self) -> None:
        """An untrusted restore cannot redirect the privileged scanner outside its input tree."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            source = Path(temporary_root).resolve() / "source"
            project = create_project(source)
            write_solution(source, [project])
            assets = project.parent / "obj/project.assets.json"
            outside = Path(temporary_root).resolve() / "outside.json"
            assets.rename(outside)
            assets.symlink_to(outside)
            destination = Path(temporary_root).resolve() / "output"

            # Act
            result = stage(source, destination)

            # Assert
            self.assertNotEqual(0, result.returncode)
            self.assertEqual("could not validate dependency snapshot inputs\n", result.stderr)
            self.assertFalse(destination.exists())

    def test_rejects_duplicate_json_properties(self) -> None:
        """Different parser interpretations must not certify different dependency graphs."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            source = Path(temporary_root).resolve() / "source"
            project = create_project(source)
            write_solution(source, [project])
            assets = project.parent / "obj/project.assets.json"
            assets.write_text('{"version":3,"version":3}', encoding="ascii")
            destination = Path(temporary_root).resolve() / "output"

            # Act
            result = stage(source, destination)

            # Assert
            self.assertNotEqual(0, result.returncode)
            self.assertEqual("could not validate dependency snapshot inputs\n", result.stderr)
            self.assertFalse(destination.exists())

    def test_rejects_zero_overall_package_inventory(self) -> None:
        """A successful restore command alone must not authorize an empty submitted graph."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            source = Path(temporary_root).resolve() / "source"
            project = create_project(source, packages=False)
            write_solution(source, [project])
            destination = Path(temporary_root).resolve() / "output"

            # Act
            result = stage(source, destination)

            # Assert
            self.assertNotEqual(0, result.returncode)
            self.assertEqual("could not validate dependency snapshot inputs\n", result.stderr)
            self.assertFalse(destination.exists())

    def test_rejects_nonempty_destination_without_touching_existing_data(self) -> None:
        """Staging never overwrites a previous revision or unrelated user content."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            source = Path(temporary_root).resolve() / "source"
            project = create_project(source)
            write_solution(source, [project])
            destination = Path(temporary_root).resolve() / "output"
            destination.mkdir()
            sentinel = destination / "keep.txt"
            sentinel.write_text("keep", encoding="ascii")

            # Act
            result = stage(source, destination)

            # Assert
            self.assertNotEqual(0, result.returncode)
            self.assertEqual("could not validate dependency snapshot inputs\n", result.stderr)
            self.assertEqual("keep", sentinel.read_text())
            self.assertEqual([sentinel], list(destination.iterdir()))

    def test_rejects_solution_paths_outside_source(self) -> None:
        """Relative traversal cannot add content from a sibling checkout or credential directory."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            source = Path(temporary_root).resolve() / "source"
            create_project(source)
            (source / "Example.slnx").write_text(
                '<Solution><Project Path="../outside.csproj" /></Solution>', encoding="ascii")
            destination = Path(temporary_root).resolve() / "output"

            # Act
            result = stage(source, destination)

            # Assert
            self.assertNotEqual(0, result.returncode)
            self.assertEqual("could not validate dependency snapshot inputs\n", result.stderr)
            self.assertFalse(destination.exists())

    def test_rejects_orphan_resolved_libraries(self) -> None:
        """A nonempty library dictionary is not evidence of a resolved target graph."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            source = Path(temporary_root).resolve() / "source"
            project = create_project(source)
            write_solution(source, [project])
            assets = project.parent / "obj/project.assets.json"
            payload = json.loads(assets.read_text())
            payload["targets"]["net10.0"] = {}
            assets.write_text(json.dumps(payload), encoding="ascii")
            destination = Path(temporary_root).resolve() / "output"

            # Act
            result = stage(source, destination)

            # Assert
            self.assertNotEqual(0, result.returncode)
            self.assertEqual("could not validate dependency snapshot inputs\n", result.stderr)
            self.assertFalse(destination.exists())

    def test_rejects_duplicate_solution_projects(self) -> None:
        """One project cannot be counted twice to disguise an incomplete solution."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            source = Path(temporary_root).resolve() / "source"
            project = create_project(source)
            write_solution(source, [project, project])
            destination = Path(temporary_root).resolve() / "output"

            # Act
            result = stage(source, destination)

            # Assert
            self.assertNotEqual(0, result.returncode)
            self.assertEqual("could not validate dependency snapshot inputs\n", result.stderr)
            self.assertFalse(destination.exists())

    def test_rejects_malformed_json_without_staging(self) -> None:
        """A truncated restore output must never become a successful empty inventory."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary_root:
            source = Path(temporary_root).resolve() / "source"
            project = create_project(source)
            write_solution(source, [project])
            (project.parent / "obj/project.assets.json").write_text('{"version":', encoding="ascii")
            destination = Path(temporary_root).resolve() / "output"

            # Act
            result = stage(source, destination)

            # Assert
            self.assertNotEqual(0, result.returncode)
            self.assertEqual("could not validate dependency snapshot inputs\n", result.stderr)
            self.assertFalse(destination.exists())

    def test_rejects_source_that_changes_between_validation_and_copy(self) -> None:
        """A concurrent restore cannot mix two independently valid revisions into one submission."""
        # Arrange
        helper = load_stager()
        with tempfile.TemporaryDirectory() as temporary_root:
            source = Path(temporary_root).resolve() / "source"
            project = create_project(source)
            write_solution(source, [project])
            destination = Path(temporary_root).resolve() / "output"
            original = helper.read_file
            reads = 0

            def changed_read(root, relative):
                nonlocal reads
                content = original(root, relative)
                if relative.suffix == ".csproj":
                    reads += 1
                    if reads == 2:
                        return content + b"\n"

                return content

            # Act
            with mock.patch.object(helper, "read_file", side_effect=changed_read):
                with self.assertRaisesRegex(ValueError, "source changed during snapshot staging"):
                    helper.stage(source, destination)

            # Assert
            self.assertFalse((destination / ".snapshot-root").exists())
            self.assertFalse((destination / project.relative_to(source)).exists())

    def test_rejects_oversized_input_before_staging(self) -> None:
        """Limits apply before reading the full payload or creating the output tree."""
        # Arrange
        helper = load_stager()
        with tempfile.TemporaryDirectory() as temporary_root:
            source = Path(temporary_root).resolve() / "source"
            project = create_project(source)
            write_solution(source, [project])
            destination = Path(temporary_root).resolve() / "output"

            # Act
            with mock.patch.object(helper, "MAX_FILE_BYTES", 16):
                with self.assertRaisesRegex(ValueError, "bounded regular file"):
                    helper.stage(source, destination)

            # Assert
            self.assertFalse(destination.exists())


class DependencySnapshotReceiverTests(unittest.TestCase):
    """Revalidate staged directory inputs before the isolated publisher consumes them."""

    def test_valid_receiver_preserves_every_original_byte_and_manifest_identity(self) -> None:
        """A complete data-only graph is accepted without relocating restore ownership."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            helper, source, destination, tree, entries = create_bundle(root)
            payload = root / "payload"
            write_payload(payload, entries)

            # Act
            result = helper.receive(payload, destination, source, tree)

            # Assert
            self.assertEqual((1, 1), result)
            self.assertEqual(entries, {path.relative_to(destination).as_posix(): path.read_bytes()
                                       for path in destination.rglob("*") if path.is_file()})
            assets = json.loads((destination / "src/Example/obj/project.assets.json").read_bytes())
            self.assertEqual(str(source / "src/Example/Example.csproj"),
                             assets["project"]["restore"]["projectUniqueName"])

    def test_valid_receiver_accepts_central_source_design_restore_profiles(self) -> None:
        """Sanitization preserves the repository's real central artifacts layout."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            helper, source, destination, tree, entries = create_bundle(root, artifact_name="Example.Source.Design")
            payload = root / "payload"
            write_payload(payload, entries)

            # Act
            result = helper.receive(payload, destination, source, tree)

            # Assert
            self.assertEqual((1, 1), result)
            assets_path = "artifacts/obj/Example.Source.Design/project.assets.json"
            self.assertEqual(entries[assets_path], (destination / assets_path).read_bytes())
            self.assertFalse((destination / "src/Example/obj").exists())

    def test_valid_receiver_accepts_projects_without_committed_locks(self) -> None:
        """A source project without a committed lock still needs its complete restored graph."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            helper, source, destination, tree, entries = create_bundle(root, locked=False)
            payload = root / "payload"
            write_payload(payload, entries)

            # Act
            result = helper.receive(payload, destination, source, tree)

            # Assert
            self.assertEqual((1, 1), result)
            self.assertFalse((destination / "src/Example/packages.lock.json").exists())
            self.assertTrue((destination / "src/Example/obj/project.assets.json").is_file())

    def test_valid_receiver_ignores_unrelated_source_symlink_blobs(self) -> None:
        """Only selected metadata needs ordinary-file source identity, not the whole repository."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            helper, source, destination, tree, entries = create_bundle(root)
            tree["tree"].append({"path": "unrelated-link", "mode": "120000", "type": "blob", "sha": "f" * 40})
            payload = root / "payload"
            write_payload(payload, entries)

            # Act
            result = helper.receive(payload, destination, source, tree)

            # Assert
            self.assertEqual((1, 1), result)
            self.assertFalse((destination / "unrelated-link").exists())

    def test_receiver_rejects_forged_committed_manifests(self) -> None:
        """Transported data is not sufficient authority to replace committed source."""
        for path in ("Example.slnx", "src/Example/Example.csproj", "src/Example/packages.lock.json"):
            with self.subTest(path=path), tempfile.TemporaryDirectory() as temporary:
                # Arrange
                root = Path(temporary).resolve()
                helper, source, destination, tree, entries = create_bundle(root)
                entries[path] += b"\n"
                payload = root / "payload"
                write_payload(payload, entries)

                # Act
                with self.assertRaisesRegex(ValueError, "differs from the committed source"):
                    helper.receive(payload, destination, source, tree)

                # Assert
                self.assertFalse(destination.exists())

    def test_receiver_rejects_missing_declared_inputs(self) -> None:
        """Deleting source, restore or committed lock inputs cannot hide dependencies."""
        for path in ("Example.slnx", "src/Example/Example.csproj", "src/Example/obj/project.assets.json",
                     "src/Example/packages.lock.json", ".snapshot-root"):
            with self.subTest(path=path), tempfile.TemporaryDirectory() as temporary:
                # Arrange
                root = Path(temporary).resolve()
                helper, source, destination, tree, entries = create_bundle(root)
                del entries[path]
                payload = root / "payload"
                write_payload(payload, entries)

                # Act
                with self.assertRaises(ValueError):
                    helper.receive(payload, destination, source, tree)

                # Assert
                self.assertFalse(destination.exists())

    def test_receiver_rejects_incomplete_or_ambiguous_source_trees(self) -> None:
        """The source API must return a complete and unambiguous Git inventory."""
        changes = {
            "truncated": lambda tree: tree.update(truncated=True),
            "absent marker": lambda tree: tree.pop("truncated"),
            "duplicate blob": lambda tree: tree["tree"].append(dict(tree["tree"][0])),
            "invalid entry": lambda tree: tree["tree"].append({"path": None}),
            "symlink manifest": lambda tree: tree["tree"][0].update(mode="120000"),
        }
        for name, mutate in changes.items():
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temporary:
                # Arrange
                root = Path(temporary).resolve()
                helper, source, destination, tree, entries = create_bundle(root)
                mutate(tree)
                payload = root / "payload"
                write_payload(payload, entries)

                # Act
                with self.assertRaises(ValueError):
                    helper.receive(payload, destination, source, tree)

                # Assert
                self.assertFalse(destination.exists())

    def test_receiver_rejects_configuration_scripts_and_unsafe_directory_names(self) -> None:
        """Payload content cannot configure the Action or introduce executable scripts."""
        names = (".env", "NuGet.config", "eng/build.py", "hooks/post-checkout", "C:evil.csproj",
                 "src\\\\evil.csproj", "a/" * 17 + "x.csproj")
        for name in names:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temporary:
                # Arrange
                root = Path(temporary).resolve()
                helper, source, destination, tree, entries = create_bundle(root)
                entries[name] = b"untrusted"
                payload = root / "payload"
                write_payload(payload, entries)

                # Act
                with self.assertRaises(ValueError):
                    helper.receive(payload, destination, source, tree)

                # Assert
                self.assertFalse(destination.exists())

    def test_receiver_rejects_symlink_files_and_directories(self) -> None:
        """A staged path cannot redirect publisher input outside its data tree."""
        for link_directory in (False, True):
            with self.subTest(link_directory=link_directory), tempfile.TemporaryDirectory() as temporary:
                # Arrange
                root = Path(temporary).resolve()
                helper, source, destination, tree, entries = create_bundle(root)
                payload = root / "payload"
                write_payload(payload, entries)
                if link_directory:
                    outside = root / "outside"
                    outside.mkdir()
                    (payload / "redirect").symlink_to(outside, target_is_directory=True)
                else:
                    project = payload / "src/Example/Example.csproj"
                    outside = root / "outside.csproj"
                    project.rename(outside)
                    project.symlink_to(outside)

                # Act
                with self.assertRaises(ValueError):
                    helper.receive(payload, destination, source, tree)

                # Assert
                self.assertFalse(destination.exists())
                self.assertTrue(outside.exists())

    def test_receiver_rejects_executable_data_files(self) -> None:
        """Executable file permissions are not valid data-only publisher input."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            helper, source, destination, tree, entries = create_bundle(root)
            payload = root / "payload"
            write_payload(payload, entries)
            (payload / "src/Example/Example.csproj").chmod(0o700)

            # Act
            with self.assertRaises(ValueError):
                helper.receive(payload, destination, source, tree)

            # Assert
            self.assertFalse(destination.exists())

    @unittest.skipUnless(hasattr(os, "mkfifo"), "Named pipes require a Unix filesystem.")
    def test_receiver_rejects_nonregular_files_without_opening_them(self) -> None:
        """A named pipe must be rejected by metadata rather than blocking a payload read."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            helper, source, destination, tree, entries = create_bundle(root)
            payload = root / "payload"
            write_payload(payload, entries)
            project = payload / "src/Example/Example.csproj"
            project.unlink()
            os.mkfifo(project)

            # Act
            with self.assertRaises(ValueError):
                helper.receive(payload, destination, source, tree)

            # Assert
            self.assertFalse(destination.exists())

    def test_receiver_rejects_undeclared_metadata_files(self) -> None:
        """Syntactically allowed metadata is still restricted to the declared solution graph."""
        for path in ("artifacts/obj/hidden/project.assets.json", "src/Example/obj/extra/packages.lock.json"):
            with self.subTest(path=path), tempfile.TemporaryDirectory() as temporary:
                # Arrange
                root = Path(temporary).resolve()
                helper, source, destination, tree, entries = create_bundle(root)
                entries[path] = b"{}"
                payload = root / "payload"
                write_payload(payload, entries)

                # Act
                with self.assertRaises(ValueError):
                    helper.receive(payload, destination, source, tree)

                # Assert
                self.assertFalse(destination.exists())

    def test_receiver_rejects_root_and_restore_identity_changes(self) -> None:
        """Staged paths and restore owner identities must retain their original absolute meaning."""
        changes = {
            "marker": lambda entries: entries.update({".snapshot-root": b"/different/root\n"}),
            "unique owner": lambda entries: changed_assets(entries, lambda data: data["project"]["restore"].update(
                projectUniqueName="/different/root/Example.csproj")),
            "output": lambda entries: changed_assets(entries, lambda data: data["project"]["restore"].update(
                outputPath="/different/root/obj")),
        }
        for name, mutate in changes.items():
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temporary:
                # Arrange
                root = Path(temporary).resolve()
                helper, source, destination, tree, entries = create_bundle(root)
                mutate(entries)
                payload = root / "payload"
                write_payload(payload, entries)

                # Act
                with self.assertRaises(ValueError):
                    helper.receive(payload, destination, source, tree)

                # Assert
                self.assertFalse(destination.exists())

    def test_receiver_rejects_inconsistent_or_incomplete_restored_graphs(self) -> None:
        """The publisher independently rejects inconsistent or incomplete restored data."""
        changes = {
            "schema": lambda data: data.update(version=5),
            "target type": lambda data: data["targets"]["net10.0"]["Sample/1.0.0"].update(type="project"),
            "dangling edge": lambda data: data["targets"]["net10.0"]["Sample/1.0.0"].update(
                dependencies={"Missing": "1.0.0"}),
            "orphan": lambda data: data["targets"].update({"net10.0": {}}),
            "unknown target library": lambda data: data["targets"]["net10.0"].update(
                {"Missing/1.0.0": {"type": "package"}}),
            "lock hash": lambda data: data["libraries"]["Sample/1.0.0"].update(sha512="wrong"),
            "lock target": lambda data: data["targets"].update({"net11.0": data["targets"]["net10.0"]}),
        }
        for name, mutate in changes.items():
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temporary:
                # Arrange
                root = Path(temporary).resolve()
                helper, source, destination, tree, entries = create_bundle(root)
                changed_assets(entries, mutate)
                payload = root / "payload"
                write_payload(payload, entries)

                # Act
                with self.assertRaises(ValueError):
                    helper.receive(payload, destination, source, tree)

                # Assert
                self.assertFalse(destination.exists())

    def test_receiver_enforces_file_and_count_bounds(self) -> None:
        """Resource bounds reject before any validated output is materialized."""
        for boundary in ("file", "count"):
            with self.subTest(boundary=boundary), tempfile.TemporaryDirectory() as temporary:
                # Arrange
                root = Path(temporary).resolve()
                helper, source, destination, tree, entries = create_bundle(root)
                payload = root / "payload"
                write_payload(payload, entries)
                attribute, limit = {
                    "file": ("MAX_FILE_BYTES", max(map(len, entries.values())) - 1),
                    "count": ("MAX_PROJECTS", 0),
                }[boundary]

                # Act
                with mock.patch.object(helper, attribute, limit), self.assertRaises(ValueError):
                    helper.receive(payload, destination, source, tree)

                # Assert
                self.assertFalse(destination.exists())

    def test_receiver_enforces_total_size_bound_across_individually_valid_files(self) -> None:
        """Multiple bounded regular files cannot bypass the aggregate payload limit."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            helper, source, destination, tree, entries = create_bundle(root)
            changed_assets(entries, lambda data: data.update(fixture_padding="x" * 5000))
            payload = root / "payload"
            write_payload(payload, entries)
            limit = sum(map(len, entries.values())) - 1
            self.assertLess(max(map(len, entries.values())), limit)

            # Act
            with mock.patch.object(helper, "MAX_TOTAL_BYTES", limit), self.assertRaises(ValueError):
                helper.receive(payload, destination, source, tree)

            # Assert
            self.assertFalse(destination.exists())

    def test_receiver_cli_does_not_echo_attacker_payloads(self) -> None:
        """Rejected metadata cannot emit forged workflow commands through error messages."""
        # Arrange
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            _, source, destination, tree, entries = create_bundle(root)
            entries["::error::forged.csproj"] = b"untrusted"
            payload = root / "payload"
            write_payload(payload, entries)
            tree_path = root / "tree.json"
            tree_path.write_text(json.dumps(tree), encoding="ascii")

            # Act
            result = subprocess.run(
                [sys.executable, str(STAGER), "receive", str(payload), str(destination), str(source), str(tree_path)],
                capture_output=True, text=True, check=False)

            # Assert
            self.assertEqual(1, result.returncode)
            self.assertEqual("could not validate dependency snapshot inputs\n", result.stderr)
            self.assertNotIn("::error::", result.stdout + result.stderr)
            self.assertFalse(destination.exists())


class DependencySnapshotLockTests(unittest.TestCase):
    """Keep the real lock/restore contract distinct from directory handoff validation."""

    def test_lock_accepts_matching_case_insensitive_package_and_hash(self) -> None:
        """NuGet package names are case-insensitive while resolved versions and hashes are exact."""
        # Arrange
        helper = load_stager()
        assets = {
            "targets": {"net10.0": {"Sample/1.0.0": {"type": "package"}}},
            "libraries": {"Sample/1.0.0": {"type": "package", "sha512": "same-content"}},
        }
        lock = {
            "version": 2,
            "dependencies": {"net10.0": {
                "sAMPLE": {"type": "Direct", "resolved": "1.0.0", "contentHash": "same-content"},
                "ProjectReference": {"type": "Project"},
            }},
        }

        # Act
        result = helper.verify_lock(lock, assets)

        # Assert
        self.assertIsNone(result)

    def test_lock_rejects_target_package_version_or_hash_drift(self) -> None:
        """Any changed locked identity or content hash invalidates the submitted restore."""
        variants = (
            {"net11.0": {"Sample": {"resolved": "1.0.0", "contentHash": "same-content"}}},
            {"net10.0": {"Other": {"resolved": "1.0.0", "contentHash": "same-content"}}},
            {"net10.0": {"Sample": {"resolved": "2.0.0", "contentHash": "same-content"}}},
            {"net10.0": {"Sample": {"resolved": "1.0.0", "contentHash": "other-content"}}},
            {"net10.0": {"Sample": {"resolved": "1.0.0"}}},
            {"net10.0": {}},
        )
        for dependencies in variants:
            with self.subTest(dependencies=dependencies):
                # Arrange
                helper = load_stager()
                assets = {
                    "targets": {"net10.0": {"Sample/1.0.0": {"type": "package"}}},
                    "libraries": {"Sample/1.0.0": {"type": "package", "sha512": "same-content"}},
                }
                lock = {"version": 2, "dependencies": dependencies}

                # Act
                with self.assertRaises(ValueError):
                    helper.verify_lock(lock, assets)

                # Assert
                self.assertEqual("same-content", assets["libraries"]["Sample/1.0.0"]["sha512"])


if __name__ == "__main__":
    unittest.main()
