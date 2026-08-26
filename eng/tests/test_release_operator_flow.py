"""Execute the runbook's actual commands with real, isolated Git repositories."""

from __future__ import annotations

import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest
from unittest import mock

import test_release_tag_contract as tag_contract


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
RUNBOOK = REPOSITORY_ROOT / "docs" / "operations" / "release-publication.md"
RELEASE_WORKFLOW = REPOSITORY_ROOT / ".github" / "workflows" / "release-candidate.yml"
VERSION = "27.4.2-rc.73"


def normal_blocks() -> list[str]:
    normal = RUNBOOK.read_text(encoding="ascii").split("## One-time setup", 1)[0]
    blocks = re.findall(r"(?ms)^```bash\n(.*?)^```$", normal)
    if len(blocks) != 3:
        raise AssertionError("The normal runbook must contain preparation, pre-tag check and tag commands.")

    return blocks


class ReleaseOperatorFlowTests(unittest.TestCase):
    """Keep the Markdown recipe executable without duplicating it in a fixture."""

    def test_normal_recipe_contains_independent_commands_without_a_fail_fast_wrapper(self) -> None:
        for block in normal_blocks():
            self.assertNotIn("&&", block)
            self.assertNotIn("||", block)
            self.assertNotIn("set -", block)

    def test_recipes_cannot_write_to_hosted_file_commands_or_load_operator_startup_files(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            sink = root / "hosted-output"
            sink.write_text("Existing runner state.\n", encoding="ascii")
            startup = root / "startup.sh"
            startup.write_text("exit 97\n", encoding="ascii")
            (root / ".zshenv").write_text("exit 97\n", encoding="ascii")
            inherited = {
                name: str(sink) for name in ("GITHUB_OUTPUT", "GITHUB_ENV", "GITHUB_PATH", "GITHUB_STEP_SUMMARY")
            }
            inherited.update({"BASH_ENV": str(startup), "ENV": str(startup), "ZDOTDIR": str(root)})

            with mock.patch.dict(os.environ, inherited):
                for shell in ["bash"] + (["zsh"] if shutil.which("zsh") else []):
                    with self.subTest(shell=shell), RunbookRepository() as repository:
                        result = repository.execute("\n".join(normal_blocks()), shell=shell)

                        self.assertEqual(result.returncode, 0, result.stderr)
                        self.assertIn("ready for untagged qualification", result.stdout)
                        self.assertEqual(sink.read_text(encoding="ascii"), "Existing runner state.\n")
                        self.assertFalse(any(key.startswith("GITHUB_") for key in repository.environment))

                with tag_contract.FakeReleaseEnvironment({}) as environment:
                    self.assertFalse(any(key.startswith("GITHUB_") for key in environment))
                    self.assertNotIn("BASH_ENV", environment)

    def test_complete_rc_and_stable_recipes_keep_the_captured_commit_and_push_only_one_tag(self) -> None:
        shells = ["bash"] + (["zsh"] if shutil.which("zsh") else [])
        for version in (VERSION, "38.9.12"):
            for shell in shells:
                with self.subTest(version=version, shell=shell), RunbookRepository(version) as repository:
                    repository.git("config", "push.followTags", "true")
                    repository.git("tag", "--no-sign", "-a", "unrelated", "-m", "Must stay local")

                    result = repository.execute("\n".join(normal_blocks()), shell=shell)

                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertIn("ready for untagged qualification", result.stdout)
                    self.assertIn(f"Verified authorized release tag v{version}", result.stdout)
                    self.assertEqual(repository.git("rev-list", "-n", "1", f"v{version}"), repository.candidate)
                    self.assertEqual(
                        repository.git("rev-list", "-n", "1", f"v{version}", remote=True), repository.candidate,
                    )
                    self.assertNotIn("refs/tags/unrelated", repository.refs(remote=True))
                    self.assertEqual(
                        repository.git("for-each-ref", "--format=%(contents:subject)", f"refs/tags/v{version}"),
                        f"Doka.EntityFrameworkCore.SafeMigrations {version}",
                    )
                    self.assertNotIn("actions/runs/", Path(repository.environment["FAKE_GH_CALLS"]).read_text())

    def test_preparation_rejects_a_local_commit_ahead_of_origin(self) -> None:
        with RunbookRepository() as repository:
            repository.git("-c", "commit.gpgSign=false", "commit", "--allow-empty", "-m", "Unpublished change")

            result = repository.execute(normal_blocks()[0])

            self.assertNotEqual(result.returncode, 0, result.stdout)
            self.assert_no_release_tag(repository)

    def test_tag_recipe_preserves_the_candidate_when_protected_main_advances(self) -> None:
        with RunbookRepository() as repository:
            new_main = repository.advance_main()

            result = repository.tag()

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(repository.git("rev-parse", "origin/main"), new_main)
            self.assertEqual(repository.git("rev-list", "-n", "1", f"v{VERSION}", remote=True), repository.candidate)

    def test_changed_checkout_or_rewritten_main_rejects_tag_creation(self) -> None:
        for case in ("changed checkout", "rewritten main"):
            with self.subTest(case=case), RunbookRepository() as repository:
                if case == "changed checkout":
                    repository.git("-c", "commit.gpgSign=false", "commit", "--allow-empty", "-m", "Other checkout")
                else:
                    repository.advance_main(rewrite=True)

                result = repository.tag()

                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assert_no_release_tag(repository)

    def test_dirty_tracked_and_untracked_files_reject_tag_creation(self) -> None:
        for name in ("CHANGELOG.md", "untracked.txt"):
            with self.subTest(name=name), RunbookRepository() as repository:
                (repository.checkout / name).write_text("Local work must not be discarded.\n", encoding="ascii")

                result = repository.tag()

                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assert_no_release_tag(repository)
                self.assertTrue((repository.checkout / name).exists())

    def test_missing_or_empty_captured_commit_cannot_fall_back_to_head(self) -> None:
        for defined in (False, True):
            with self.subTest(defined=defined), RunbookRepository() as repository:
                if defined:
                    repository.environment["release_commit"] = ""

                result = repository.execute(normal_blocks()[2])

                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assertIn("Usage:", result.stderr)
                self.assert_no_release_tag(repository)

    def test_invalid_version_or_missing_changelog_entry_rejects_tag_creation(self) -> None:
        for version in ("<release_version>", "v" + VERSION, "27.4.2-rc.01", "28.0.0", "27.4.2-rc.74"):
            with self.subTest(version=version), RunbookRepository() as repository:
                result = repository.tag(version=version)

                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assertFalse(any(name.startswith("refs/tags/") for name in repository.refs()))
                self.assertFalse(any(name.startswith("refs/tags/") for name in repository.refs(remote=True)))

    def test_existing_local_tag_is_not_replaced_or_pushed(self) -> None:
        with RunbookRepository() as repository:
            repository.git("tag", "--no-sign", f"v{VERSION}")
            before = repository.refs()

            result = repository.tag()

            self.assertNotEqual(result.returncode, 0, result.stdout)
            self.assertEqual(repository.refs(), before)
            self.assertNotIn(f"refs/tags/v{VERSION}", repository.refs(remote=True))

    def test_existing_remote_tag_is_not_overwritten(self) -> None:
        with RunbookRepository() as repository:
            repository.git("update-ref", f"refs/tags/v{VERSION}", repository.candidate, remote=True)
            before = repository.refs(remote=True)

            result = repository.tag()

            self.assertNotEqual(result.returncode, 0, result.stdout)
            self.assertEqual(repository.refs(remote=True), before)
            self.assertIn(f"refs/tags/v{VERSION}", repository.refs())

    def test_unauthorized_signing_key_is_rejected_before_push(self) -> None:
        with RunbookRepository() as repository:
            unauthorized = repository.create_key("unauthorized")
            repository.git("config", "user.signingkey", str(unauthorized))

            result = repository.tag()

            self.assertNotEqual(result.returncode, 0, result.stdout)
            self.assertIn(f"refs/tags/v{VERSION}", repository.refs())
            self.assertNotIn(f"refs/tags/v{VERSION}", repository.refs(remote=True))

    def test_operator_stop_policy_prevents_later_tag_actions_after_a_failure(self) -> None:
        for command in ("status", "fetch", "tag", "verify-tag", "push"):
            with self.subTest(command=command), RunbookRepository() as repository:
                repository.inject_git_failure(command)

                result = repository.tag()
                calls = [json.loads(line)[0] for line in repository.call_log.read_text().splitlines()]

                self.assertEqual(result.returncode, 17, result.stderr)
                self.assertEqual(calls[-1], command)
                self.assertNotIn(f"refs/tags/v{VERSION}", repository.refs(remote=True))
                self.assertEqual(f"refs/tags/v{VERSION}" in repository.refs(), command in ("verify-tag", "push"))

    def test_operator_stop_policy_prevents_later_preparation_commands_after_a_failure(self) -> None:
        for command in ("fetch", "switch", "merge", "status", "rev-parse"):
            with self.subTest(command=command), RunbookRepository() as repository:
                repository.inject_git_failure(command)

                result = repository.execute(normal_blocks()[0])
                calls = [json.loads(line)[0] for line in repository.call_log.read_text().splitlines()]

                self.assertEqual(result.returncode, 17, result.stderr)
                self.assertEqual(calls[-1], command)
                self.assert_no_release_tag(repository)

    def test_root_wrapper_retains_the_explicit_diagnostic_and_error_status(self) -> None:
        cases = (
            (("--run-id", tag_contract.RUN_ID, "--version", tag_contract.VERSION,
              "--commit", tag_contract.COMMIT), 0, "ready for signed tag"),
            (("--version", tag_contract.VERSION), 2, "Usage:"),
        )
        for arguments, expected_exit, expected_output in cases:
            with self.subTest(arguments=arguments), tag_contract.FakeReleaseEnvironment({}) as environment:
                result = subprocess.run(
                    ["bash", str(REPOSITORY_ROOT / "eng" / "pre-tag-check.sh"), *arguments],
                    cwd=REPOSITORY_ROOT, env=environment, check=False, capture_output=True, text=True,
                )

                self.assertEqual(result.returncode, expected_exit, result.stderr)
                self.assertIn(expected_output, result.stdout + result.stderr)

    def test_workflow_keeps_one_manual_input_and_the_same_protected_publication_job(self) -> None:
        source = RELEASE_WORKFLOW.read_text(encoding="ascii")
        dispatch = self.section(source, r"(?ms)^on:\n(.*?)(?=^\S)")
        self.assertEqual(re.findall(r"(?m)^  ([\w-]+):", dispatch), ["workflow_dispatch"])
        self.assertEqual(re.findall(r"(?m)^      ([\w-]+):", dispatch), ["version"])
        self.assertIn("        required: true\n        type: string", dispatch)
        self.assertIn("run-name: Release ${{ inputs.version }}", source)
        self.assertNotIn("write-tag-instructions", source)
        self.assertFalse((REPOSITORY_ROOT / "eng" / "release" / "write-tag-instructions.sh").exists())
        publish = self.section(source, r"(?ms)^  publish:\n(.*?)(?=^  \S|\Z)")
        self.assertIn("needs:\n      - preflight\n      - quality-gates\n      - attest\n", publish)
        self.assertIn("environment:\n      name: nuget\n", publish)

    def section(self, source: str, pattern: str) -> str:
        match = re.search(pattern, source)
        self.assertIsNotNone(match, pattern)
        assert match is not None

        return match.group(1)

    def assert_no_release_tag(self, repository: RunbookRepository) -> None:
        self.assertNotIn(f"refs/tags/v{repository.version}", repository.refs())
        self.assertNotIn(f"refs/tags/v{repository.version}", repository.refs(remote=True))


class RunbookRepository(tag_contract.RealSourceRepository):
    """Sign/push only to a temporary local origin, never to a hosted repository."""

    def __init__(self, version: str = VERSION) -> None:
        self.version = version

    def __enter__(self) -> RunbookRepository:
        super().__enter__()
        for name in ("release_commit", "release_version", "release_tag"):
            self.environment.pop(name, None)
        shutil.copy2(REPOSITORY_ROOT / "eng" / "pre-tag-check.sh", self.checkout / "eng" / "pre-tag-check.sh")
        shutil.copy2(tag_contract.VERIFY_TAG, self.checkout / "eng" / "release" / "verify-tag.sh")
        (self.checkout / "src" / "Directory.Build.props").write_text(
            f"<Project><PropertyGroup><VersionPrefix>{self.version.split('-')[0]}</VersionPrefix>"
            "</PropertyGroup></Project>\n", encoding="ascii",
        )
        (self.checkout / "CHANGELOG.md").write_text(f"## [{self.version}] - 2026-08-26\n", encoding="ascii")
        public_key = self.create_key("authorized")
        key_text = public_key.read_text(encoding="ascii").strip()
        (self.checkout / "eng" / "release" / "allowed-signers").write_text(
            f"release@example.invalid {key_text}\n", encoding="ascii",
        )
        self.git("config", "user.email", "release@example.invalid")
        self.git("config", "user.signingkey", str(public_key))
        self.git("add", "--all")
        self.git("-c", "commit.gpgSign=false", "commit", "--quiet", "-m", "Prepare isolated release fixture")
        self.git("push", "--quiet", "origin", "main")
        self.candidate = self.git("rev-parse", "HEAD")
        self.environment["FAKE_SIGNING_KEYS_JSON"] = json.dumps([{"key": " ".join(key_text.split()[:2])}])

        return self

    def create_key(self, name: str) -> Path:
        key = self.root / name
        subprocess.run(
            ["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", str(key)],
            env=self.environment, check=True, capture_output=True, text=True,
        )

        return key.with_suffix(".pub")

    def execute(
        self, block: str, *, shell: str = "bash", version: str | None = None,
    ) -> subprocess.CompletedProcess[str]:
        # User startup files can replace PATH and bypass the isolated GitHub stub.
        shell_options = ["-f"] if shell == "zsh" else []
        command = block.replace("<release_version>", self.version if version is None else version)

        # Only the harness enforces the operator's documented stop-on-error policy.
        # The runbook commands themselves remain independently executable.
        return subprocess.run(
            [shell, *shell_options, "-e", "-c", command],
            cwd=self.checkout, env=self.environment, check=False, capture_output=True, text=True,
        )

    def tag(self, *, version: str | None = None) -> subprocess.CompletedProcess[str]:
        self.environment["release_commit"] = self.candidate

        return self.execute(normal_blocks()[2], version=version)

    def inject_git_failure(self, command: str) -> None:
        self.call_log = self.root / "git-calls.jsonl"
        self.environment.update({
            "RUNBOOK_REAL_GIT": shutil.which("git") or "git",
            "RUNBOOK_FAIL_COMMAND": command,
            "RUNBOOK_CALL_LOG": str(self.call_log),
        })
        tag_contract.FakeReleaseEnvironment.write_executable(self.root / "bin" / "git", '''#!/usr/bin/env python3
import json
import os
import sys

arguments = sys.argv[1:]
normalized = arguments.copy()
while normalized and normalized[0] in ("-C", "-c"):
    normalized = normalized[2:]
with open(os.environ["RUNBOOK_CALL_LOG"], "a", encoding="ascii") as output:
    output.write(json.dumps(normalized) + "\\n")
if normalized and normalized[0] == os.environ["RUNBOOK_FAIL_COMMAND"]:
    print("Injected Git command failure", file=sys.stderr)
    raise SystemExit(17)
os.execv(os.environ["RUNBOOK_REAL_GIT"], [os.environ["RUNBOOK_REAL_GIT"], *arguments])
''')


if __name__ == "__main__":
    unittest.main()
