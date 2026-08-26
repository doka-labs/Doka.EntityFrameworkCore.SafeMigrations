## Summary

<!-- Describe the consumer problem, root cause, and observable result. Link the
issue or reviewed design. Keep this PR cohesive and target main. Do not disclose
vulnerabilities here: follow SECURITY.md and use private coordination. -->

## Type of change

- [ ] Bug fix
- [ ] Operation, capability, or public API change
- [ ] Refactor
- [ ] Dependency, build, CI, or release change
- [ ] Documentation or community guidance

## Scope and compatibility

<!-- Identify Core, MySQL/MariaDB, PostgreSQL, tooling, or documentation impact.
Explain API/SQL/history/report compatibility, data integrity, recovery, provider
capability differences, and performance/allocation effects. State not applicable
with a reason for unaffected areas. Link primary sources for external contracts. -->

## Verification evidence

<!-- Replace the example row with exact commands, runtime/server versions or image
digests, results, and artifact/check links. Mark checks not run honestly with a
reason. A local default MariaDB run is not evidence for MySQL or every MariaDB
version. A successful build is not integration or package qualification. -->

| Check | Exact command or CI job | Result and evidence |
|---|---|---|
| Relevant local checks | Replace with the command | Passed, failed, or not run with reason |

The required CI matrix remains blocking. A local not-applicable explanation does
not waive required checks in `.github/workflows/quality-gates.yml`.

## Review checklist

<!-- Check an item only after verifying it. For a genuinely unaffected item,
record the reason in Scope and compatibility rather than claiming a test ran. -->

- [ ] The change follows `CONTRIBUTING.md`, `.editorconfig`, ASCII-only content, and the Code of Conduct.
- [ ] Relevant locked restore, warning-free Release build, Roslyn style/import checks, and structural gates passed.
- [ ] Public XML documentation, API baselines, user documentation, and `CHANGELOG.md` reflect the contract change.
- [ ] No unapproved dependency, warning suppression, dead configuration, or cross-provider dependency was introduced.
- [ ] Reports, SQL, logs, fixtures, and attachments contain no credentials or confidential production data.

For operation, policy, catalog, or execution changes, verify and record:

- definition validation, planner decisions, SQL shape, and live provider behavior;
- missing, matching, different, unsupported, data-blocked, and prerequisite states;
- MySQL and MariaDB independently, plus PostgreSQL parity or explicit fail-closed rejection;
- initial application, idempotent rerun, partial-failure recovery, cancellation, and relevant concurrency cases;
- preflight/postflight, normal EF operations, migration history, and derived-context boundaries;
- affected EF CLI/script/bundle, package-consumer, dependency-profile, coverage, and performance/allocation gates.

For release or workflow changes, record positive and negative engineering tests,
Action/schema compatibility, and local-versus-hosted evidence separately. Do not
create tags, publish packages, or bypass the protected release environment to
validate a pull request.
