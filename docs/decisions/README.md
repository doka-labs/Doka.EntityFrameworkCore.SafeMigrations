# Architecture decisions

These records use [Doka MADR Enterprise Profile 1.0](MADR-PROFILE.md), based on
MADR 4.0.0, with `Dominic Kalkbrenner` as the named decision maker. The
[template](adr-template.md) preserves Doka's metadata, headings, trade-offs,
confirmation, relationships, history, and source-provenance format.

Each record's front matter is the sole authority for its current status and
relationships. Its Decision History owns recording, approval and update dates;
implementation alone does not establish acceptance. This manual index provides
navigation without duplicating that metadata.

| Record | Decision |
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
| [D-010](D-010-excluded-table-model-managed-data-ownership.md) | Explicit model-managed-data ownership for excluded tables in independent lineages |
| [D-011](D-011-lossless-column-repair-and-actionable-diagnostics.md) | Provider-proven lossless column repair and bounded actionable preflight evidence |
| [D-012](D-012-self-describing-report-views.md) | Bounded report views for operator triage |
| [D-013](D-013-sqlite-provider-and-atomic-rebuilds.md) | SQLite provider composition and atomic model-owned rebuilds |
| [D-014](D-014-mariadb-catalog-snapshot-for-batched-classification.md) | Optional MariaDB session-local catalog snapshots for bounded analysis |
| [D-015](D-015-owned-atomic-required-column-additions.md) | Owned atomic MySQL/MariaDB required-column staging and forward recovery |
| [D-016](D-016-actionable-diagnostics-and-operation-identity.md) | Shared structural diagnostic identity and provider-owned error transport |
| [D-017](D-017-generated-expression-and-dependency-contracts.md) | Bounded generated expressions, native identity and ordered source dependencies |
| [D-018](D-018-release-lines-and-monotonic-publication.md) | Exact release lines, current publication authority and monotonic Latest |
| [D-019](D-019-explicit-table-storage-contracts.md) | Immutable provider-certified explicit table storage contracts |
| [D-020](D-020-atomic-retiring-table-foreign-keys.md) | Exact-window foreign-key retirement on atomically removed SQLite tables |

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
