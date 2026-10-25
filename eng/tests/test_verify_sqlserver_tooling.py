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
import shutil
import sys
from pathlib import Path

name = Path(sys.argv[0]).name
args = sys.argv[1:]
if name != "rm" or os.environ.get("MOCK_DIRECTORY_CLEANUP_EXIT"):
    with Path(os.environ["MOCK_RUN_LOG"]).open("a", encoding="utf-8") as output:
        output.write(json.dumps({
            "name": name,
            "args": args,
            "connection": os.environ.get("SAFE_MIGRATIONS_CONNECTION_STRING", ""),
        }) + "\n")

if name == "uname":
    print(os.environ.get("MOCK_ARCHITECTURE", "x86_64"))
elif name == "docker":
    if args[0] == "exec" and ("-Q" in args or "-i" in args):
        if os.environ.get("MOCK_ENFORCE_NATIVE_BUDGETS") == "1":
            if any(option not in args or int(args[args.index(option) + 1]) <= 0 for option in ("-l", "-t")):
                print("Native sqlcmd request has no finite budget", file=sys.stderr)
                sys.exit(61)

    if args[0] == "run":
        print("owned-tooling-container")
    elif args[0] == "rm":
        sys.exit(int(os.environ.get("MOCK_CONTAINER_CLEANUP_EXIT", "0")))
    elif args[0] == "port":
        print("127.0.0.1:51433")
    elif args[0] == "logs":
        print("Mock SQL Server diagnostic output")
        sys.exit(int(os.environ.get("MOCK_DIAGNOSTICS_EXIT", "0")))
    elif args[0] == "exec" and args[2:4] == ["test", "-x"]:
        expected = os.environ.get("MOCK_SQLCMD_PATH", "/opt/mssql-tools18/bin/sqlcmd")
        sys.exit(0 if args[4] == expected else 1)
    elif args[0] == "exec" and "-i" in args:
        # ODBC sqlcmd defaults QUOTED_IDENTIFIER to OFF, unlike SqlClient.
        if "-I" not in args:
            print("QUOTED_IDENTIFIER is OFF for computed-index script execution", file=sys.stderr)
            sys.exit(41)
        sys.exit(int(os.environ.get("MOCK_SCRIPT_EXIT", "0")))
    elif args[0] == "exec" and "-Q" in args:
        query = args[args.index("-Q") + 1]
        if "sys.dm_exec_requests" in query:
            print("Mock SQL Server wait metadata")
            sys.exit(int(os.environ.get("MOCK_DIAGNOSTICS_EXIT", "0")))
        failure = os.environ.get("MOCK_READBACK_FAILURE", "")
        if failure and "doka_tooling_" + failure in query:
            print("Synthetic database defect: " + failure, file=sys.stderr)
            sys.exit(57)
        print("1")
elif name == "dotnet":
    if args[:3] == ["ef", "migrations", "add"]:
        mode = "legacy" if "Legacy" in args[3] else "strict"
        directory = Path(args[args.index("--project") + 1]).parent
        directory /= args[args.index("--output-dir") + 1]
        directory.mkdir(parents=True, exist_ok=True)
        migration = directory / ("20990101000000_" + args[3] + ".cs")
        generated_contracts = (
            "scaffolding_generated_nodes", "HasDescendants", "CASE WHEN", "ck_scaffolding_generated_bounds",
        )
        missing_contract = os.environ.get("MOCK_MISSING_GENERATED_CONTRACT", "")
        migration.write_text(
            'migrationBuilder.EnsureModelManagedDataFromModel(\n'
            'includedColumns: ["DisplayName"]\n'
            + "\n".join(value for value in generated_contracts if value != missing_contract) + "\n" + (
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
    if name != "rm":
        raise AssertionError("Unexpected mocked command: " + name)
    target = Path(args[-1])
    if not target.name.startswith("safemigrations-sqlserver-tooling."):
        raise AssertionError("Unexpected cleanup target: " + str(target))
    shutil.rmtree(target)
    sys.exit(int(os.environ.get("MOCK_DIRECTORY_CLEANUP_EXIT", "0")))
'''


class VerifySqlServerToolingTests(unittest.TestCase):
    """Preserve finite waits, fatal failures and owned diagnostic cleanup."""

    def run_harness(self, *, version: str = "2025", script_source: str | None = None,
                    **settings: str) -> tuple[subprocess.CompletedProcess, list[dict], Path]:
        """Run the actual shell script with isolated executable dependencies."""

        directory = tempfile.TemporaryDirectory(prefix="sm-tooling-contract-")
        self.addCleanup(directory.cleanup)
        root = Path(directory.name)
        source = root / "source" / "eng"
        source.mkdir(parents=True)
        script = source / SCRIPT.name
        script.write_bytes(SCRIPT.read_bytes() if script_source is None else script_source.encode("ascii"))
        binary_directory = root / "bin"
        binary_directory.mkdir()
        mock = binary_directory / "mock"
        mock.write_text(MOCK_COMMAND, encoding="utf-8")
        mock.chmod(0o755)

        for command in ("dotnet", "docker", "uname", "rsync", "rm"):
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
            ["bash", str(script), "mock-sqlserver-image", version],
            env=environment, capture_output=True, text=True, timeout=30,
        )

        invocations = [json.loads(line) for line in log.read_text(encoding="utf-8").splitlines()]

        return result, invocations, root

    def test_all_native_requests_have_finite_connect_and_query_budgets(self) -> None:
        """Readiness, DDL, scripts, readback and diagnostics cannot wait without a native budget."""
        # Arrange
        expected_requests = {"readiness": 1, "script": 6, "create": 4, "readback": 8}

        # Act
        result, invocations, _ = self.run_harness()

        # Assert
        self.assertEqual(0, result.returncode, result.stderr)
        observed = dict.fromkeys(expected_requests, 0)
        for call in invocations:
            args = call["args"]
            if call["name"] != "docker" or args[0] != "exec" or not ("-Q" in args or "-i" in args):
                continue

            self.assertIn("-l", args)
            self.assertIn("-t", args)
            query = args[args.index("-Q") + 1] if "-Q" in args else ""
            request = ("script" if "-i" in args else "readiness" if query == "SELECT 1"
                       else "create" if query.startswith("CREATE DATABASE") else "readback")
            observed[request] += 1
            expected_budget = "5" if request == "readiness" else "120"
            self.assertEqual(expected_budget, args[args.index("-l") + 1])
            self.assertEqual(expected_budget, args[args.index("-t") + 1])

        self.assertEqual(expected_requests, observed)

    def test_removed_native_query_timeout_is_rejected_at_the_process_boundary(self) -> None:
        """The timeout oracle detects an actual command mutation rather than a copied helper."""
        # Arrange
        original = SCRIPT.read_text(encoding="ascii")
        mutated = original.replace("-t 120", "")

        # Act
        result, invocations, root = self.run_harness(
            script_source=mutated, MOCK_ENFORCE_NATIVE_BUDGETS="1")

        # Assert
        self.assertNotEqual(original, mutated)
        self.assertEqual(61, result.returncode, result.stderr)
        self.assertIn("Native sqlcmd request has no finite budget", result.stderr)
        self.assertTrue(any(call["args"][0] == "rm" for call in invocations if call["name"] == "docker"))
        self.assertEqual([], list((root / "work").iterdir()))

    def test_every_transport_checks_generated_metadata_and_behavior(self) -> None:
        """All eight independent targets must observe the complete generated contract after replay."""
        # Arrange
        expected_databases = {f"tooling_{mode}_{transport}" for mode in ("strict", "legacy")
                              for transport in ("cli", "bundle", "normal_script", "idempotent_script")}
        expected_guards = ("seed", "history", "included_index", "generated_columns", "computed_index",
                           "check_constraint", "generated_values", "check_enforcement")

        # Act
        result, invocations, _ = self.run_harness()

        # Assert
        self.assertEqual(0, result.returncode, result.stderr)
        readbacks = [call["args"] for call in invocations if call["name"] == "docker"
                     and "-Q" in call["args"] and "doka_tooling_generated_columns" in " ".join(call["args"])]
        self.assertEqual(8, len(readbacks))
        self.assertEqual(expected_databases, {args[args.index("-d") + 1] for args in readbacks})
        for args in readbacks:
            self.assertIn("-I", args)
            self.assertIn("-b", args)
            query = args[args.index("-Q") + 1]
            for guard in expected_guards:
                self.assertIn("doka_tooling_" + guard, query)

            self.assertIn("is_not_trusted = 0", query)
            self.assertIn("key_ordinal = 3", query)
            self.assertIn("9223372036854775806", query)
            self.assertIn("ERROR_NUMBER() <> 547", query)
            self.assertIn("ROLLBACK TRANSACTION", query)

    def test_generated_readback_defects_are_fatal_and_cleanup_still_runs(self) -> None:
        """Every independent guard rejects its corresponding synthetic database defect."""
        for guard in ("seed", "history", "included_index", "generated_columns", "computed_index",
                      "check_constraint", "generated_values", "check_enforcement"):
            with self.subTest(guard=guard):
                # Arrange
                expected_failure = 57

                # Act
                result, invocations, root = self.run_harness(MOCK_READBACK_FAILURE=guard)

                # Assert
                self.assertEqual(expected_failure, result.returncode, result.stderr)
                self.assertIn("Synthetic database defect: " + guard, result.stderr)
                self.assertEqual("rm", invocations[-1]["args"][0])
                self.assertEqual([], list((root / "work").iterdir()))

    def test_cleanup_only_container_failure_is_fatal(self) -> None:
        """Successful qualification cannot silently leak an owned container."""
        # Arrange
        cleanup_failure = 79

        # Act
        result, invocations, root = self.run_harness(MOCK_CONTAINER_CLEANUP_EXIT=str(cleanup_failure))

        # Assert
        self.assertEqual(cleanup_failure, result.returncode, result.stderr)
        self.assertIn("Failed to remove owned SQL Server tooling container", result.stderr)
        self.assertEqual("rm", invocations[-1]["args"][0])
        self.assertEqual([], list((root / "work").iterdir()))

    def test_cleanup_failures_preserve_original_failure_and_attempt_both_resources(self) -> None:
        """An original update failure survives simultaneous container and directory cleanup failures."""
        # Arrange
        original_failure = 23

        # Act
        result, invocations, root = self.run_harness(
            MOCK_LEGACY_UPDATE_EXIT=str(original_failure), MOCK_CONTAINER_CLEANUP_EXIT="79",
            MOCK_DIRECTORY_CLEANUP_EXIT="31")

        # Assert
        self.assertEqual(original_failure, result.returncode, result.stderr)
        self.assertIn("Failed to remove owned SQL Server tooling container", result.stderr)
        self.assertIn("Failed to remove SQL Server tooling directory", result.stderr)
        self.assertEqual("rm", invocations[-2]["args"][0])
        self.assertEqual("rm", invocations[-1]["name"])
        self.assertEqual([], list((root / "work").iterdir()))

    def test_cleanup_only_directory_failure_is_fatal_after_container_cleanup(self) -> None:
        """A directory cleanup error remains visible after successful container removal."""
        # Arrange
        cleanup_failure = 31

        # Act
        result, invocations, root = self.run_harness(MOCK_DIRECTORY_CLEANUP_EXIT=str(cleanup_failure))

        # Assert
        self.assertEqual(cleanup_failure, result.returncode, result.stderr)
        self.assertIn("Failed to remove SQL Server tooling directory", result.stderr)
        self.assertEqual("rm", invocations[-2]["args"][0])
        self.assertEqual("rm", invocations[-1]["name"])
        self.assertEqual([], list((root / "work").iterdir()))

    def test_every_script_connection_enables_quoted_identifiers(self) -> None:
        """Both clients and all engine profiles execute strict/legacy scripts under the native contract."""
        for version in ("2019", "2022", "2025"):
            for client in ("/opt/mssql-tools18/bin/sqlcmd", "/opt/mssql-tools/bin/sqlcmd"):
                with self.subTest(version=version, client=client):
                    # Arrange
                    expected = {"tooling_strict_normal_script": 1, "tooling_strict_idempotent_script": 2,
                                "tooling_legacy_normal_script": 1, "tooling_legacy_idempotent_script": 2}

                    # Act
                    result, invocations, _ = self.run_harness(version=version, MOCK_SQLCMD_PATH=client)

                    # Assert
                    self.assertEqual(0, result.returncode, result.stderr)
                    executions = [call["args"] for call in invocations
                                  if call["name"] == "docker" and "-i" in call["args"]]
                    self.assertEqual(6, len(executions))
                    observed = {}
                    for args in executions:
                        self.assertEqual(client, args[2])
                        self.assertIn("-I", args)
                        self.assertIn("-b", args)
                        database = args[args.index("-d") + 1]
                        observed[database] = observed.get(database, 0) + 1

                    self.assertEqual(expected, observed)

    def test_removed_quoted_identifier_flag_fails_and_preserves_cleanup(self) -> None:
        """The negative control mutates the real command rather than asserting a copied predicate."""
        # Arrange
        original = SCRIPT.read_text(encoding="ascii")
        mutated = original.replace(" -C -b -I \\\n", " -C -b \\\n")

        # Act
        result, invocations, root = self.run_harness(script_source=mutated)

        # Assert
        self.assertNotEqual(original, mutated)
        self.assertEqual(41, result.returncode, result.stderr)
        self.assertIn("QUOTED_IDENTIFIER is OFF", result.stderr)
        scripts = [call for call in invocations if call["name"] == "docker" and "-i" in call["args"]]
        self.assertEqual(1, len(scripts))
        self.assertNotIn("-I", scripts[0]["args"])
        self.assertEqual("rm", invocations[-1]["args"][0])
        self.assertEqual([], list((root / "work").iterdir()))

    def test_script_error_is_not_masked_by_quoted_identifiers_or_diagnostics(self) -> None:
        """Correct connection options neither bypass a SQL error nor replace its original exit code."""
        # Arrange
        failure_exit = 53

        # Act
        result, invocations, root = self.run_harness(MOCK_SCRIPT_EXIT=str(failure_exit),
                                                    MOCK_DIAGNOSTICS_EXIT="31")

        # Assert
        self.assertEqual(failure_exit, result.returncode, result.stderr)
        scripts = [call for call in invocations if call["name"] == "docker" and "-i" in call["args"]]
        self.assertEqual(1, len(scripts))
        self.assertIn("-I", scripts[0]["args"])
        self.assertIn("-b", scripts[0]["args"])
        self.assertEqual("rm", invocations[-1]["args"][0])
        self.assertEqual([], list((root / "work").iterdir()))

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

    def test_missing_generated_contract_is_rejected_before_database_update(self) -> None:
        # Arrange
        missing_contract = "ck_scaffolding_generated_bounds"

        # Act
        result, invocations, root = self.run_harness(MOCK_MISSING_GENERATED_CONTRACT=missing_contract)

        # Assert
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertIn("missing its generated expression contract: " + missing_contract, result.stderr)
        self.assertFalse(any(
            call["name"] == "dotnet" and call["args"][:3] == ["ef", "database", "update"]
            for call in invocations))
        self.assertEqual("rm", invocations[-1]["args"][0])
        self.assertEqual([], list((root / "work").iterdir()))
