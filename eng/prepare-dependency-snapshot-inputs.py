"""Stage complete, data-only NuGet restore inputs for isolated submission."""

import hashlib
import json
import os
import stat
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


MAX_FILE_BYTES = 64 * 1024 * 1024
MAX_TOTAL_BYTES = 512 * 1024 * 1024
MAX_PROJECTS = 512
MAX_DISCOVERY_ENTRIES = 32768
MAX_DISCOVERY_DEPTH = 16
EXCLUDED_PARTS = {"artifacts", "bin", "obj", ".git"}


def safe_file(root: Path, relative: Path) -> Path:
    """Resolve a plain source file without accepting traversal or symlink components."""
    if relative.is_absolute() or not relative.parts or ".." in relative.parts:
        raise ValueError(f"expected a relative project path: {relative}")

    candidate = root
    for component in relative.parts:
        candidate /= component
        if candidate.is_symlink():
            raise ValueError(f"symlink input is not allowed: {relative}")

    if not candidate.is_file() or not candidate.resolve().is_relative_to(root):
        raise ValueError(f"missing or outside-root input: {relative}")

    return candidate


def read_file(root: Path, relative: Path) -> bytes:
    """Read one regular input with finite size and no leaf-symlink following."""
    path = safe_file(root, relative)
    descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0))
    with os.fdopen(descriptor, "rb") as source:
        metadata = os.fstat(source.fileno())
        if not stat.S_ISREG(metadata.st_mode) or metadata.st_size > MAX_FILE_BYTES:
            raise ValueError(f"input is not a bounded regular file: {relative}")

        content = source.read(MAX_FILE_BYTES + 1)

    if len(content) > MAX_FILE_BYTES:
        raise ValueError(f"input exceeds size limit: {relative}")

    return content


def unique_properties(pairs: list[tuple[str, object]]) -> dict:
    """Reject duplicate JSON keys instead of relying on a parser-specific winner."""
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON property: {key}")

        result[key] = value

    return result


def read_json(content: bytes) -> dict:
    """Require a duplicate-free JSON object, not a partial or scalar response."""
    value = json.loads(content, object_pairs_hook=unique_properties)
    if not isinstance(value, dict):
        raise ValueError("expected a JSON object")

    return value


def read_xml(content: bytes) -> ET.Element:
    """Parse data-only project XML without accepting entity or DTD declarations."""
    text = content.decode("utf-8-sig")
    if "<!DOCTYPE" in text.upper() or "<!ENTITY" in text.upper():
        raise ValueError("XML declarations that can redefine content are not allowed")

    return ET.fromstring(text)


def restored_packages(assets: dict, project: Path, source_root: Path, assets_path: Path) -> int:
    """Validate the exact restored owner and return its resolved package count."""
    if type(assets.get("version")) is not int or assets["version"] not in (3, 4):
        raise ValueError(f"unsupported project.assets.json version: {project.name}")

    project_data = assets.get("project")
    restore = project_data.get("restore") if isinstance(project_data, dict) else None
    if not isinstance(restore, dict):
        raise ValueError(f"missing restore identity: {project.name}")

    for field in ("projectPath", "projectUniqueName"):
        value = restore.get(field)
        if not isinstance(value, str) or not Path(value).is_absolute() or Path(value) != project:
            raise ValueError(f"inconsistent restore identity {field}: {project.name}")

    output = restore.get("outputPath")
    if not isinstance(output, str) or not Path(output).is_absolute():
        raise ValueError(f"invalid restore outputPath: {project.name}")

    if not Path(output).resolve().is_relative_to(source_root):
        raise ValueError(f"restore outputPath escapes source root: {project.name}")

    if Path(output) != (source_root / assets_path).parent:
        raise ValueError(f"restore outputPath differs from the assets directory: {project.name}")

    libraries = assets.get("libraries")
    targets = assets.get("targets")
    if not isinstance(libraries, dict) or not isinstance(targets, dict) or not targets:
        raise ValueError(f"missing resolved NuGet inventory: {project.name}")

    count = 0
    for identity, library in libraries.items():
        if not isinstance(identity, str) or not identity or not isinstance(library, dict):
            raise ValueError(f"invalid resolved library: {project.name}")

        if library.get("type") not in ("package", "project"):
            raise ValueError(f"invalid resolved library type: {project.name}")

        if library["type"] == "package":
            if identity.count("/") != 1 or not all(identity.split("/")):
                raise ValueError(f"invalid resolved package identity: {project.name}")

            count += 1

    referenced = set()
    for framework, target in targets.items():
        if not isinstance(framework, str) or not framework or not isinstance(target, dict):
            raise ValueError(f"invalid resolved target: {project.name}")

        names = {identity.rsplit("/", 1)[0].lower() for identity in target}
        if len(names) != len(target):
            raise ValueError("target contains ambiguous package identities")

        for identity, dependency in target.items():
            if identity not in libraries or not isinstance(dependency, dict):
                raise ValueError(f"target references an unknown resolved library: {project.name}")

            if dependency.get("type") != libraries[identity]["type"]:
                raise ValueError(f"target library type differs from resolved inventory: {project.name}")

            dependencies = dependency.get("dependencies", {})
            if not isinstance(dependencies, dict) or any(
                    not isinstance(name, str) or name.lower() not in names or not isinstance(version, str)
                    for name, version in dependencies.items()):
                raise ValueError("resolved target has an incomplete dependency edge")

            referenced.add(identity)

    if referenced != set(libraries):
        raise ValueError(f"resolved libraries are absent from every target: {project.name}")

    return count


def verify_lock(lock: dict, assets: dict) -> None:
    """Require committed locked package identities and hashes in every target."""
    if lock.get("version") not in (1, 2) or not isinstance(lock.get("dependencies"), dict):
        raise ValueError("unsupported package lock")

    locked_targets = lock["dependencies"]
    if set(locked_targets) != set(assets["targets"]):
        raise ValueError("package lock and assets target inventories differ")

    for framework, locked in locked_targets.items():
        if not isinstance(locked, dict):
            raise ValueError("invalid locked target")

        expected = {}
        for name, value in locked.items():
            if not isinstance(value, dict) or not isinstance(name, str):
                raise ValueError("invalid locked package")

            if value.get("type", "").lower() == "project":
                continue

            identity = (name + "/" + value.get("resolved", "")).lower()
            if identity in expected or not isinstance(value.get("contentHash"), str):
                raise ValueError("invalid locked package identity")

            expected[identity] = value["contentHash"]

        actual = {identity.lower(): assets["libraries"][identity].get("sha512")
                  for identity, value in assets["targets"][framework].items() if value["type"] == "package"}
        if expected != actual:
            raise ValueError("package lock and assets package inventories differ")


def find_assets(source_root: Path, projects: list[Path]):
    """Inspect only conventional and repository-owned central restore locations."""
    roots = [Path("artifacts/obj"), *(project.parent / "obj" for project in projects)]
    seen = set()
    entries_seen = 0
    for relative in roots:
        directory = source_root / relative
        if directory.is_symlink():
            raise ValueError(f"symlink restore directory is not allowed: {relative}")

        if not directory.exists():
            continue

        pending = [(directory, 0)]
        while pending:
            directory, depth = pending.pop()
            if directory in seen:
                continue

            seen.add(directory)
            if depth > MAX_DISCOVERY_DEPTH:
                raise ValueError("restore directory discovery exceeds its depth bound")

            with os.scandir(directory) as entries:
                for entry in entries:
                    entries_seen += 1
                    if entries_seen > MAX_DISCOVERY_ENTRIES:
                        raise ValueError("restore directory discovery exceeds its entry bound")

                    path = Path(entry.path)
                    if entry.is_symlink():
                        raise ValueError(f"symlink restore input is not allowed: {path.relative_to(source_root)}")

                    if entry.is_dir(follow_symlinks=False):
                        pending.append((path, depth + 1))
                    elif entry.name == "project.assets.json":
                        yield path.relative_to(source_root)


def inventory_assets(source_root: Path, projects: list[Path], identity_root: Path | None = None) -> dict:
    """Bind each discovered restore to exactly one declared physical project owner."""
    identity_root = identity_root or source_root
    owners = {identity_root / relative: relative for relative in projects}
    result = {}
    total_bytes = 0
    for relative in find_assets(source_root, projects):
        content = read_file(source_root, relative)
        total_bytes += len(content)
        if total_bytes > MAX_TOTAL_BYTES:
            raise ValueError("resolved snapshot inputs exceed the total size bound")

        assets = read_json(content)
        project_data = assets.get("project")
        restore = project_data.get("restore") if isinstance(project_data, dict) else None
        owner = restore.get("projectPath") if isinstance(restore, dict) else None
        if not isinstance(owner, str) or not Path(owner).is_absolute() or Path(owner) not in owners:
            raise ValueError(f"restore identity is outside the solution: {relative}")

        project = owners[Path(owner)]
        if project in result:
            raise ValueError(f"duplicate restore identity for solution project: {project}")

        packages = restored_packages(assets, identity_root / project, identity_root, relative)
        result[project] = (relative, hashlib.sha256(content).digest(), len(content), packages)

    for project in projects:
        if project not in result:
            raise ValueError(f"missing project.assets.json for solution project: {project}")

    return result


def stage(source_root: Path, destination: Path, identity_root: Path | None = None,
          expected_files: set[str] | None = None) -> tuple[int, int]:
    """Copy a validated solution graph without carrying executable repository content."""
    if source_root.is_symlink() or not source_root.is_dir():
        raise ValueError("source root must be a real directory")

    source_root = source_root.resolve()
    identity_root = identity_root or source_root
    destination = destination.absolute()
    if any(part.is_symlink() for part in (destination, *destination.parents)):
        raise ValueError("destination must not contain symlinks")

    if destination.is_relative_to(source_root) or source_root.is_relative_to(destination):
        raise ValueError("source and destination must be separate sibling trees")

    if destination.exists() and (not destination.is_dir() or any(destination.iterdir())):
        raise ValueError("destination must be a new or empty directory")

    solutions = list(source_root.glob("*.slnx"))
    if len(solutions) != 1:
        raise ValueError("expected exactly one root .slnx solution")

    solution = read_xml(read_file(source_root, solutions[0].relative_to(source_root)))
    projects = []
    for element in solution.iter("Project"):
        raw = element.get("Path", "")
        relative = Path(raw)
        if not raw or "\\" in raw or relative.suffix != ".csproj" or EXCLUDED_PARTS.intersection(relative.parts):
            raise ValueError(f"invalid relative project path: {raw}")

        safe_file(source_root, relative)
        if relative in projects:
            raise ValueError(f"duplicate solution project: {relative}")

        projects.append(relative)

    if not projects or len(projects) > MAX_PROJECTS:
        raise ValueError("solution project inventory is empty or exceeds its bound")

    # WHY: UseArtifactsOutput and package-consumer profiles intentionally move
    # assets outside project/obj. The exact NuGet restore identity, not a guessed
    # ArtifactsProjectName, binds those central outputs back to the solution.
    assets_by_project = inventory_assets(source_root, projects, identity_root)
    solution_path = solutions[0].relative_to(source_root)
    solution_bytes = read_file(source_root, solution_path)
    inputs = [(solution_path, hashlib.sha256(solution_bytes).digest())]
    total_bytes = len(solution_bytes)
    total_packages = 0
    for relative in projects:
        content = read_file(source_root, relative)
        if read_xml(content).tag.rsplit("}", 1)[-1] != "Project":
            raise ValueError(f"expected project XML: {relative}")

        total_bytes += len(content)
        inputs.append((relative, hashlib.sha256(content).digest()))
        assets_path, digest, size, packages = assets_by_project[relative]
        total_bytes += size
        total_packages += packages
        inputs.append((assets_path, digest))

        lock = relative.parent / "packages.lock.json"
        if (source_root / lock).exists() or (source_root / lock).is_symlink():
            content = read_file(source_root, lock)
            verify_lock(read_json(content), read_json(read_file(source_root, assets_path)))
            total_bytes += len(content)
            inputs.append((lock, hashlib.sha256(content).digest()))

        if total_bytes > MAX_TOTAL_BYTES:
            raise ValueError("resolved snapshot inputs exceed the total size bound")

    if total_packages == 0:
        raise ValueError("solution contains no resolved NuGet packages")

    if expected_files is not None and expected_files != {".snapshot-root", *(path.as_posix() for path, _ in inputs)}:
        raise ValueError("archive contains undeclared project or restore inputs")

    destination.mkdir(parents=True, exist_ok=True)
    # WHY: Validate the complete graph first, then compare fresh bytes while
    # copying. This avoids retaining every assets file in memory and catches a
    # concurrent restore rather than submitting a mixed source snapshot.
    for relative, digest in inputs:
        content = read_file(source_root, relative)
        if hashlib.sha256(content).digest() != digest:
            raise ValueError(f"source changed during snapshot staging: {relative}")

        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        with target.open("xb") as output:
            output.write(content)

    # WHY: NuGet's detector retains absolute projectPath values. A separate
    # submission job must restore this exact root instead of silently rebasing
    # a manifest to a different checkout identity.
    (destination / ".snapshot-root").write_text(str(identity_root) + "\n", encoding="utf-8")

    return len(projects), total_packages


def source_blobs(tree: dict) -> dict[str, str]:
    """Read a complete Git tree fetched from the exact API-validated head SHA."""
    if tree.get("truncated") is not False or not isinstance(tree.get("tree"), list):
        raise ValueError("source tree is incomplete")

    result = {}
    for entry in tree["tree"]:
        if not isinstance(entry, dict) or not isinstance(entry.get("path"), str):
            raise ValueError("invalid source tree entry")

        path = entry["path"]
        if path in result:
            raise ValueError("duplicate source tree path")

        if entry.get("type") == "blob":
            metadata = path.endswith((".csproj", ".slnx", "/packages.lock.json"))
            if metadata and entry.get("mode") != "100644":
                raise ValueError("source metadata must not use symlinks")

            result[path] = entry.get("sha")

    return result


def verify_source_files(root: Path, blobs: dict[str, str]) -> None:
    """Bind declared solution, projects and committed locks to the real head tree."""
    for path in root.rglob("*"):
        if not path.is_file() or path.name in ("project.assets.json", ".snapshot-root"):
            continue

        relative = path.relative_to(root)
        content = read_file(root, relative)
        # WHY: A valid artifact digest proves transport, not that a fork supplied
        # the committed manifest. Git's blob identity closes that substitution.
        digest = hashlib.sha1(b"blob " + str(len(content)).encode("ascii") + b"\0" + content).hexdigest()
        if blobs.get(relative.as_posix()) != digest:
            raise ValueError("snapshot manifest differs from the committed source")

    solutions = [name for name in blobs if "/" not in name and name.endswith(".slnx")]
    if len(solutions) != 1 or not (root / solutions[0]).is_file():
        raise ValueError("snapshot omitted the committed root solution")

    solution = read_xml(read_file(root, Path(solutions[0])))
    for element in solution.iter("Project"):
        project = Path(element.get("Path", ""))
        lock = project.parent / "packages.lock.json"
        if lock.as_posix() in blobs and not (root / lock).is_file():
            raise ValueError("snapshot omitted a committed package lock")


def receive(source_root: Path, destination: Path, identity_root: Path, tree: dict) -> tuple[int, int]:
    """Revalidate data from the receiver's read-only restore before snapshot writes."""
    if not identity_root.is_absolute() or identity_root != identity_root.resolve():
        raise ValueError("expected a canonical absolute manifest root")

    if not source_root.is_dir() or any(part.is_symlink() for part in (source_root, *source_root.parents)):
        raise ValueError("expected a real data directory")

    source_root = source_root.resolve()
    names = set()
    size = 0
    entries_seen = 0
    pending = [source_root]
    while pending:
        directory = pending.pop()
        with os.scandir(directory) as entries:
            for entry in entries:
                entries_seen += 1
                relative = Path(entry.path).relative_to(source_root)
                if (entry.is_symlink() or len(relative.parts) > MAX_DISCOVERY_DEPTH
                        or entries_seen > MAX_DISCOVERY_ENTRIES):
                    raise ValueError("unsafe or unbounded data directory")

                if entry.is_dir(follow_symlinks=False):
                    pending.append(Path(entry.path))
                    continue

                name = relative.as_posix()
                allowed = (name == ".snapshot-root" or name.endswith(".csproj")
                           or ("/" not in name and name.endswith(".slnx"))
                           or relative.name in ("project.assets.json", "packages.lock.json"))
                metadata = entry.stat(follow_symlinks=False)
                if (not allowed or "\\" in name or ":" in name or not stat.S_ISREG(metadata.st_mode)
                        or metadata.st_mode & 0o111 or metadata.st_size > MAX_FILE_BYTES):
                    raise ValueError("undeclared, executable or oversized data input")

                names.add(name)
                size += metadata.st_size
                if size > MAX_TOTAL_BYTES or len(names) > MAX_PROJECTS * 4 + 2:
                    raise ValueError("data input exceeds its total size or count bound")

    if read_file(source_root, Path(".snapshot-root")) != (str(identity_root) + "\n").encode("utf-8"):
        raise ValueError("data manifest root differs from its original identity")

    verify_source_files(source_root, source_blobs(tree))

    return stage(source_root, destination, identity_root, names)


def main() -> int:
    """Validate command arguments and fail closed without printing input payloads."""
    if len(sys.argv) not in (3, 6):
        print("usage: prepare-dependency-snapshot-inputs.py <source> <output>"
              " | receive <data-directory> <output> <original-root> <source-tree.json>", file=sys.stderr)

        return 2

    try:
        if len(sys.argv) == 6 and sys.argv[1] == "receive":
            tree_path = Path(sys.argv[5]).absolute()
            tree = read_json(read_file(tree_path.parent, Path(tree_path.name)))
            projects, packages = receive(Path(sys.argv[2]), Path(sys.argv[3]), Path(sys.argv[4]), tree)
        elif len(sys.argv) == 3:
            projects, packages = stage(Path(sys.argv[1]), Path(sys.argv[2]))
        else:
            raise ValueError("invalid command arguments")

    except (OSError, ValueError, TypeError, AttributeError, KeyError, ET.ParseError):
        # WHY: Inputs are untrusted in the receiver. Never echo arbitrary XML,
        # JSON values or filenames into Actions command/log processing.
        print("could not validate dependency snapshot inputs", file=sys.stderr)

        return 1

    print(f"Staged {projects} projects and {packages} resolved NuGet package occurrences as data-only inputs.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
