---
id: D-016
status: proposed
date: 2026-10-14
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Structural error identity, readable reports and owned provider rejection transport"
supersedes: []
superseded-by: []
amends: []
amended-by: []
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-016 -- Share actionable diagnostics and operation identity

## Context and Problem Statement

A rejection category alone does not identify the affected table or explain what
evidence rejected an operation. An assessment containing only a column named Id
is ambiguous across owners. Native duplicate-key assertions are even harder to
read without an explicit preflight report. Operators need contextual diagnostics
without extra data scans, invented causes, leaked values or changed decisions.

The maintainer authorized implementation of this feature. This decision text is
proposed for review; implementation presence does not imply approval of this
record or native hosted qualification.

## Decision Drivers

- Identify authored table, member, FK direction and rename destination.
- Distinguish unavailable context from an intentionally unqualified schema.
- Keep machine categories, policies, fingerprints and execution safety intact.
- Render only established causes with an actionable, non-bypass next step.
- Bound output and allocations without retaining application row payloads.
- Preserve native failures, cancellation and uncertain resource cleanup.

## Considered Options

- Shared structural context with provider-owned failure transport
- Run automatic preflight after every native migration error
- Expose native category tokens and require callers to reconstruct context

## Decision Outcome

Chosen option: "Shared structural context with provider-owned failure transport",
because authored identity is already available and can accompany proven error
evidence without another observation or a new permission boundary.

Core owns immutable identifier-only identity, reason selection and bounded text.
The runner captures known migration and policy context without changing ordinary
provider-owned operations into certified safe operations. New JSON schemas
version the added fields; historical schemas remain packaged. Explicit report
rendering shares selection semantics and discloses omitted details.

Adapters keep their existing decision and resource scopes. PostgreSQL and SQL
Server put essential context into native guard messages; SQLite enriches managed
failures without reading row identifiers. MySQL/MariaDB associates only exact
command-local owned assertions or exactly identified staging CHECK errors after
native cleanup. Its managed outer exception intentionally changes for those
owned failures, preserving the exact native cause. Script decision failures emit
a diagnostic result before the assertion; native DDL errors remain native.

### Consequences

- Good, because runtime and preflight diagnostics identify the owner and honest
  reason without extra catalog/row requests or automatic logging.
- Good, because native error categories, unchanged decisions and original causes
  remain available for machine handling and recovery.
- Bad, because JSON validators and callers catching only MySqlException for
  owned guards must adopt the new documented contracts.
- Bad, because scripts need a client that displays result sets preceding errors,
  and unknown/native causes cannot always be translated safely.

### Confirmation

- Build the full solution in Release with warnings treated as errors and verify
  public API baselines, style and imports independently of whitespace formatting.
- Run Core report/formatter/identity/error tests, including Unicode/control names,
  unknown owners, duplicate member names, bounded output and payload exclusion.
- Run native MySQL/MariaDB and PostgreSQL diagnostics tests and SQLite execution
  tests; run SQL Server offline tests locally and native tests on supported x64.
- Verify lookalike native errors and uncertain cleanup remain native, exact guard
  categories remain stable, and decorating a generated native command adds no
  dispatch or cause query. Diagnostic bytes still participate in the unchanged
  transport/grouping bounds; do not infer identical groups at every byte boundary.
- Qualify packages and assert both new and historical schema files are present.
  These are acceptance procedures, not claims that their runs have completed.

## Pros and Cons of the Options

### Shared structural context with provider-owned failure transport

- Good, because one Core explanation contract serves every provider and report.
- Bad, because native transport limits require distinct adapter mechanisms and
  careful attribution rather than a universal exception wrapper.

### Run automatic preflight after every native migration error

- Good, because fresh catalog evidence can assist a deliberate operator inquiry.
- Bad, because another query can fail, observe partially committed state or hide
  the original failure, and adds latency and permissions to failure handling.

### Expose native category tokens and require callers to reconstruct context

- Good, because existing machine tokens remain small and compatible.
- Bad, because callers cannot reliably infer owners or proven causes from names,
  and would duplicate provider-specific error parsing and disclosure policy.

## More Information

This complements D-004's execution/recovery ownership, D-005's evidence privacy,
D-011's typed differences and D-012's report views. It does not amend their
decision semantics or authorize a safety bypass, dependency or external service.

### Re-evaluation Triggers

- A qualified provider changes native error grammar or message limits.
- A new operation cannot express its structural identity without private values.
- Native transport becomes able to carry complete structured context portably.
- Measurements reveal avoidable success-path allocation or dispatch overhead.

### Decision History

- 2026-10-14: Decision recorded with status proposed.

### Implementation References

- [Core diagnostics](../../src/Doka.EntityFrameworkCore.SafeMigrations/Diagnostics)
- [Core report serialization](../../src/Doka.EntityFrameworkCore.SafeMigrations/Analysis/SafeMigrationReportJson.cs)
- [Diagnostic guide](../diagnostics.md)
- [Runtime failure runbook](../runbooks/failure-codes.md#runtime-database-error-identity)
- [Core diagnostic regression tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Features/Lifecycle/SafeMigrationReportDiagnosticsTests.cs)

### Sources

- No external sources; repository evidence only.
