"""Exercise SQL Server tooling control flow without requiring an x64 engine."""

import json
import os
import subprocess
import tempfile
import unittest
from pathlib import Path


SCRIPT = Path(__file__).resolve().parents[1] / "verify-sqlserver-ef-tooling.sh"
MOCK_COMMAND = r'''#!/usr/bin/env python3
import json
import os
import sys
from pathlib import Path

name = Path(sys.argv[0]).name
args = sys.argv[1:]
with Path(os.environ["MOCK_RUN_LOG"]).open("a", encoding="utf-8") as output:
    output.write(json.dumps({
        "name": name,
        "args": args,
        "connection": os.environ.get("SAFE_MIGRATIONS_CONNECTION_STRING", ""),
    }) + "\n")

if name == "uname":
    print(os.environ.get("MOCK_ARCHITECTURE", "x86_64"))
elif name == "docker":
    if args[0] == "run":
        print("owned-tooling-container")
    elif args[0] == "port":
        print("127.0.0.1:51433")
    elif args[0] == "logs":
        print("Mock SQL Server diagnostic output")
        sys.exit(int(os.environ.get("MOCK_DIAGNOSTICS_EXIT", "0")))
    elif args[0] == "exec" and "-Q" in args:
        query = args[args.index("-Q") + 1]
        if "sys.dm_exec_requests" in query:
            print("Mock SQL Server wait metadata")
            sys.exit(int(os.environ.get("MOCK_DIAGNOSTICS_EXIT", "0")))
        print("1")
elif name == "dotnet":
    if args[:3] == ["ef", "migrations", "add"]:
        mode = "legacy" if "Legacy" in args[3] else "strict"
        directory = Path(args[args.index("--project") + 1]).parent
        directory /= args[args.index("--output-dir") + 1]
        directory.mkdir(parents=True, exist_ok=True)
        migration = directory / ("20990101000000_" + args[3] + ".cs")
        migration.write_text(
            'migrationBuilder.EnsureModelManagedDataFromModel(\n'
            'includedColumns: ["DisplayName"]\n' + (
                'migrationBuilder.CreateTableIfNotExists(\n'
                'migrationBuilder.DropTableIfExists(\n' if mode == "strict" else
                'migrationBuilder.ConvergeTableFromModel(\n'
                'SafeMigrationPolicy.RepairIfSafe\n'),
            encoding="utf-8",
        )
    elif args[:3] == ["ef", "migrations", "script"]:
        Path(args[args.index("--output") + 1]).write_text(
            "IF NOT EXISTS (SELECT 1);\n", encoding="utf-8")
    elif args[:3] == ["ef", "migrations", "bundle"]:
        Path(args[args.index("--output") + 1]).symlink_to(Path(sys.argv[0]).resolve())
    elif args[:3] == ["ef", "database", "update"]:
        if "Legacy" in args[args.index("--context") + 1]:
            sys.exit(int(os.environ.get("MOCK_LEGACY_UPDATE_EXIT", "0")))
elif name != "rsync" and not name.startswith("sqlserver-"):
    raise AssertionError("Unexpected mocked command: " + name)
'''


class VerifySqlServerToolingTests(unittest.TestCase):
    """Preserve finite waits, fatal failures and owned diagnostic cleanup."""

    def run_harness(self, **settings: str) -> tuple[subprocess.CompletedProcess, list[dict], Path]:
        """Run the actual shell script with isolated executable dependencies."""

        directory = tempfile.TemporaryDirectory(prefix="sm-tooling-contract-")
        self.addCleanup(directory.cleanup)
        root = Path(directory.name)
        source = root / "source" / "eng"
        source.mkdir(parents=True)
        script = source / SCRIPT.name
        script.write_bytes(SCRIPT.read_bytes())
        binary_directory = root / "bin"
        binary_directory.mkdir()
        mock = binary_directory / "mock"
        mock.write_text(MOCK_COMMAND, encoding="utf-8")
        mock.chmod(0o755)

        for command in ("dotnet", "docker", "uname", "rsync"):
            (binary_directory / command).symlink_to(mock)

        work = root / "work"
        work.mkdir()
        log = root / "invocations.jsonl"
        environment = dict(os.environ)
        environment.update(settings)
        environment.update({
            "PATH": str(binary_directory) + os.pathsep + environment["PATH"],
            "TMPDIR": str(work),
            "MOCK_RUN_LOG": str(log),
            "PYTHONDONTWRITEBYTECODE": "1",
        })

        # WHY: Execute the real control flow, not substring assertions that can
        # pass while an actual CLI or bundle connection loses its timeout.
        result = subprocess.run(
            ["bash", str(script), "mock-sqlserver-image", "2025"],
            env=environment, capture_output=True, text=True, timeout=30,
        )

        invocations = [json.loads(line) for line in log.read_text(encoding="utf-8").splitlines()]

        return result, invocations, root

    def test_every_cli_and_bundle_connection_has_both_finite_budgets(self) -> None:
        # Arrange
        expected_databases = {
            "tooling_strict_cli", "tooling_legacy_cli",
            "tooling_strict_bundle", "tooling_legacy_bundle",
        }

        # Act
        result, invocations, root = self.run_harness()

        # Assert
        self.assertEqual(0, result.returncode, result.stderr)
        connections = []

        for invocation in invocations:
            if invocation["name"] == "dotnet" and invocation["args"][0] == "ef":
                connections.append(invocation["connection"])
            elif invocation["name"].startswith("sqlserver-"):
                arguments = invocation["args"]
                connections.append(arguments[arguments.index("--connection") + 1])

        self.assertTrue(connections)
        observed_databases = set()

        for connection in connections:
            values = dict(part.split("=", 1) for part in connection.split(";") if part)
            observed_databases.add(values["Database"])
            self.assertEqual("120", values.get("Connect Timeout"), connection)
            self.assertEqual("120", values.get("Command Timeout"), connection)

        self.assertEqual(expected_databases, observed_databases)
        self.assertFalse(any(
            call["args"][0] == "logs" for call in invocations if call["name"] == "docker"))
        self.assertEqual([], list((root / "work").iterdir()))

    def test_failed_update_retains_exit_code_and_collects_bounded_diagnostics(self) -> None:
        # Arrange
        failure_exit = 23

        # Act
        result, invocations, root = self.run_harness(MOCK_LEGACY_UPDATE_EXIT=str(failure_exit))

        # Assert
        self.assertEqual(failure_exit, result.returncode, result.stderr)
        artifacts = root / "source" / "artifacts" / "ef-tooling" / "sqlserver" / "2025"
        self.assertIn("diagnostic output", (artifacts / "server.log").read_text(encoding="utf-8"))
        self.assertIn("wait metadata", (artifacts / "server-waits.log").read_text(encoding="utf-8"))
        diagnostics = [
            call for call in invocations if "sys.dm_exec_requests" in " ".join(call["args"])
        ]

        self.assertEqual(1, len(diagnostics))
        self.assertIn("-l", diagnostics[0]["args"])
        self.assertIn("-t", diagnostics[0]["args"])
        self.assertEqual("5", diagnostics[0]["args"][diagnostics[0]["args"].index("-l") + 1])
        self.assertEqual("5", diagnostics[0]["args"][diagnostics[0]["args"].index("-t") + 1])
        self.assertEqual("rm", invocations[-1]["args"][0])
        self.assertEqual([], list((root / "work").iterdir()))
        legacy_updates = [
            call for call in invocations if call["name"] == "dotnet"
            and call["args"][:3] == ["ef", "database", "update"]
            and "Legacy" in " ".join(call["args"])
        ]

        self.assertEqual(1, len(legacy_updates))

    def test_diagnostic_failures_do_not_replace_original_failure_or_prevent_cleanup(self) -> None:
        # Arrange
        failure_exit = 23

        # Act
        result, invocations, root = self.run_harness(
            MOCK_LEGACY_UPDATE_EXIT=str(failure_exit), MOCK_DIAGNOSTICS_EXIT="31")

        # Assert
        self.assertEqual(failure_exit, result.returncode, result.stderr)
        self.assertTrue(any(
            call["args"][0] == "logs" for call in invocations if call["name"] == "docker"))
        self.assertEqual("rm", invocations[-1]["args"][0])
        self.assertEqual([], list((root / "work").iterdir()))

    def test_unsupported_host_is_rejected_before_container_start(self) -> None:
        # Arrange
        architecture = "arm64"

        # Act
        result, invocations, _ = self.run_harness(MOCK_ARCHITECTURE=architecture)

        # Assert
        self.assertEqual(1, result.returncode)
        self.assertIn("supported x86-64 host", result.stderr)
        self.assertEqual(["uname"], [call["name"] for call in invocations])
