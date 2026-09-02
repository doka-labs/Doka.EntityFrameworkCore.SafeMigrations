# Architecture decisions

These records use [Doka MADR Enterprise Profile 1.0](MADR-PROFILE.md), based on
MADR 4.0.0, with `Dominic Kalkbrenner` as the named decision maker. The
[template](adr-template.md) preserves Doka's metadata, headings, trade-offs,
confirmation, relationships, history, and source-provenance format.

The initial corpus was recorded on 2026-08-26. D-001 through D-006 describe
existing implementation retrospectively; they do not invent earlier approval
dates. Dominic Kalkbrenner has confirmed their recorded rationale and existing
implementation. D-007 records the maintainer's explicit choice of documentation
format and tooling boundary. D-008 records the implemented automatic
scaffolding contract selected on 2026-08-27. D-009 records the implemented
source-frozen model-managed-data contract selected on 2026-09-02. The dated
transitions are retained in each record's Decision History.

| Record | Bounded decision |
| --- | --- |
| [D-001](D-001-package-and-slice-boundaries.md) | Provider-neutral Core, independent adapters, and hybrid vertical slices |
| [D-002](D-002-explicit-convergence-contract.md) | Granular convergence from heterogeneous databases to one canonical Core model |
| [D-003](D-003-public-provider-integration.md) | Public Doka SPI and explicit Npgsql composition with fail-closed ownership |
| [D-004](D-004-analysis-and-execution-lifecycle.md) | Read-only analysis, guarded execution, connection ownership, and recovery |
| [D-005](D-005-bounded-evidence-and-privacy.md) | Bounded catalog work, immutable evidence, fingerprints, and telemetry privacy |
| [D-006](D-006-package-qualification-and-release.md) | Untagged qualification, protected approval, and exact-byte publication |
| [D-007](D-007-documentation-and-madr.md) | Shared Doka document standard without a repository-specific ADR toolchain |
| [D-008](D-008-automatic-safe-migration-scaffolding.md) | Source-frozen automatic strict and legacy-convergence scaffolding through EF design-time services |
| [D-009](D-009-model-managed-data-convergence.md) | Automatic guarded convergence of source-frozen EF model-managed data |

## Authoring and review

Use a new ADR for a material architectural choice, not every routine fix.
Start with the template and verify every required section against the profile.
Update this navigation table when adding a record. Current status and
relationships remain authoritative only in the ADR metadata.

Reviewers check the rationale against real code and tests, validate both
directions of changed relationships, and verify external claims against dated
primary sources. Generated or imported records require the same review; an
import is not approval.

Review changed records for links, encoding, navigation, profile structure, and
semantic agreement with implementation. No ADR-specific build tool, JSON
index, or MCP/CLI dependency is required.
