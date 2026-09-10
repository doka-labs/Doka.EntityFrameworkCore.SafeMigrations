---
id: D-012
status: implemented
date: 2026-09-10
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Self-describing, allocation-bounded report views for operator triage"
supersedes: []
superseded-by: []
amends: [D-004, D-005, D-011]
amended-by: []
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-012 -- Serialize selected report entries as an explicit view

## Context and Problem Statement

A complete SafeMigrations report is the authoritative deployment artifact, but
large migrations can produce tens of thousands of assessments. Operators often
need only blockers or non-converged entries. Requiring every consumer to parse,
filter, and rewrite the JSON duplicates contract logic and can produce an
artifact that is indistinguishable from an incomplete canonical report.

The decision is how Core can provide focused output without mutating reports,
weakening blocker semantics, expanding sensitive data, or allocating a second
assessment collection.

## Decision Drivers

- Filter semantics must be identical across every consumer and provider.
- A selected document must identify itself and its complete source report.
- Existing schema-version-2 serialization must remain byte-compatible.
- Blocking output must fail closed when a future contract is not understood.
- Time and allocation must scale predictably to large report cardinalities.

## Considered Options

- Add a distinct report-view serializer with closed selection modes
- Add optional filtering fields to the canonical report schema
- Return filtered report objects for consumers to serialize
- Leave filtering entirely to each report consumer

## Decision Outcome

Chosen option: "Add a distinct report-view serializer with closed selection
modes", because it keeps the canonical audit artifact unchanged while making
operator-focused output explicit and uniform.

`SafeMigrationReportSelection` defines `Complete`, `NonMatching`, and
`BlockingOnly`; zero is invalid. A view carries its own schema version and
document kind, the selection, source identity and totals, included totals, and
the selected arrays. It preserves source order and ordinals.

`NonMatching` excludes only a safe assessment whose state is `Matching`, action
is `NoOp`, and postcondition is satisfied. It retains provider-owned operations
and unexpected objects. `BlockingOnly` uses the runner's phase semantics:
preflight includes the four reject actions; postflight includes failed safe
postconditions. It excludes unexpected objects. A non-blocked source can
produce an empty blocker view. A blocked source with none fails closed.

The serializer makes a count pass and a write pass over the immutable source.
This permits accurate included counts and a selected-size initial buffer without
creating an intermediate array, DTO graph, or mutable report copy.

### Consequences

- Good, because a small operator artifact retains verifiable source identity.
- Good, because blocker semantics are implemented once in Core.
- Good, because existing complete report readers and bytes remain unchanged.
- Bad, because a selected view cannot prove the contents of omitted entries.
- Bad, because selection requires two linear scans before serialization ends.

### Confirmation

- Verify every selection against preflight and postflight positive cases.
- Reject zero, undefined selections, and inconsistent blocked reports before
  writing output.
- Validate serialized views against the packaged closed JSON Schema.
- Gate 50,000-assessment blocker selection with duration and allocation budgets.
- Run public API, package-content, documentation, and existing report-v2 tests.

## Pros and Cons of the Options

### Add a distinct report-view serializer with closed selection modes

- Good, because document identity prevents a filtered view from impersonating
  a canonical complete report.
- Bad, because consumers must choose the matching packaged schema explicitly.

### Add optional filtering fields to the canonical report schema

- Good, because consumers would recognize one document shape.
- Bad, because completeness would become conditional and existing readers could
  interpret omitted evidence as absent evidence.

### Return filtered report objects for consumers to serialize

- Good, because application code could inspect a smaller object graph.
- Bad, because it duplicates immutable entries and exposes construction of
  reports whose aggregate status may no longer match their contents.

### Leave filtering entirely to each report consumer

- Good, because Core would add no API surface.
- Bad, because selection semantics, document identity, and performance would
  diverge between hosts.

## More Information

D-004 remains authoritative for phase-specific blocking semantics. D-005
remains authoritative for protected report storage and bounded evidence. D-011
remains authoritative for schema-version-2 facet evidence. A view is a derived
operator artifact and does not replace a canonical report required by audit or
recovery policy.

### Re-evaluation Triggers

- A new report mode, status, or action changes phase-specific blocking rules.
- A consumer needs a selection not expressible by the three closed modes.
- Measured cardinality invalidates the two-pass or allocation contract.
- The canonical report advances beyond schema version 2.

### Decision History

- 2026-09-10: Decision recorded with status proposed.
- 2026-09-10: Dominic Kalkbrenner selected a distinct view schema and closed
  selection modes; status changed from proposed to accepted.
- 2026-09-10: Implemented selection, streaming serialization, closed schema,
  tests, performance gate, and operator documentation; status changed from
  accepted to implemented.

### Implementation References

- [Report serializer](../../src/Doka.EntityFrameworkCore.SafeMigrations/Analysis/SafeMigrationReportJson.cs)
- [Selection API](../../src/Doka.EntityFrameworkCore.SafeMigrations/Analysis/SafeMigrationReportSelection.cs)
- [Report-view schema](../../schemas/safe-migration-report-view-v1.schema.json)
- [API reference](../api-reference.md#reports-serialization-and-failure)
- [Deployment runbook](../runbooks/deployment-and-recovery.md#preflight)

### Sources

- [JSON Schema Core 2020-12](https://json-schema.org/draft/2020-12/json-schema-core)
  (primary source; retrieved 2026-09-10)
- [.NET 10 Utf8JsonWriter](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.utf8jsonwriter?view=net-10.0)
  (primary source; retrieved 2026-09-10)
- [.NET 10 ArrayBufferWriter][array-buffer-writer]
  (primary source; retrieved 2026-09-10)

[array-buffer-writer]: https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraybufferwriter-1?view=net-10.0
