---
id: D-011
status: implemented
date: 2026-09-05
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Provider-proven lossless column repair and bounded actionable preflight evidence"
supersedes: []
superseded-by: []
amends: [D-002, D-004, D-005]
amended-by: []
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-011 -- Repair only proven lossless column transitions

## Context and Problem Statement

Consolidating legacy schemas exposes ordinary length and Boolean representation
drift that SafeMigrations previously rejected even when every live value could
be preserved. The existing report then exposed only stable result codes, so an
operator needed debugger or catalog queries to identify the differing facet.

The decision is which additional transitions `RepairIfSafe` may accept and how
preflight explains them without turning SafeMigrations into schema sync,
promising online DDL, or disclosing database values.

## Decision Drivers

- Every automatic repair must preserve all current values and the complete
  modeled target definition.
- Narrowing must never rely on silent truncation or a stale preflight alone.
- Provider families need independent catalog, DDL, locking, and live evidence.
- Dependent indexes and constraints must remain explicit and compatible.
- Reports need stable typed evidence without high-cardinality telemetry.
- Large migrations need bounded query, payload, allocation, and cancellation
  behavior.

## Considered Options

- Add a narrow provider-proven repair allowlist and report schema version 2
- Keep every type or length difference blocked
- Accept general provider-convertible type changes
- Emit values or raw catalog SQL in exceptions

## Decision Outcome

Chosen option: "Add a narrow provider-proven repair allowlist and report schema
version 2", because it admits only transitions with explicit value-domain and
provider proofs while making every rejection actionable.

MySQL, MariaDB, and PostgreSQL independently support ordinary `VARCHAR`
widening and data-verified narrowing. Narrowing uses character length, groups
and deduplicates candidates per table, returns only bounded Boolean evidence,
and repeats the proof immediately before DDL. The server conversion and final
postcondition remain the race boundary. Overlength data classifies as
`DataBlocked`; no value is returned or logged.

PostgreSQL reads the declared character limit from the documented
`information_schema.columns.character_maximum_length` contract. A null limit
means unbounded `character varying` and is therefore a narrowing candidate for
every bounded target; it receives the same live-data proof as a larger bounded
source. SafeMigrations does not decode PostgreSQL's type-specific `atttypmod`
representation.

MySQL/MariaDB additionally support only the exact Boolean transition from
`BIT(1)` to `TINYINT(1)` for compatible CLR Boolean, nullability, defaults,
metadata, and dependencies. Reverse or wider bit transitions remain blocked.
Only absent, null, false, and true literal defaults are eligible. Expression
defaults and foreign-key dependencies remain blocked because SafeMigrations
cannot prove those behavioral and coupled-type contracts from one column
operation.

The allowlist continues to reject character-family or collation changes,
unrecognized provider metadata, generated/identity/row-version semantics, and
unproven dependency shapes. Accepted operations report
`TableRewritePossible`; data safety does not claim metadata-only or online DDL.

Report schema version 2 retains the backward-compatible aggregate `Code` and
adds `AnalysisCode`, `DecisionCode`, bounded typed `Differences`, and
`OperationalImpact`. Detailed evidence is report-only. A typed blocked
preflight exception carries the immutable report and a deterministic bounded
first-conflict summary.

### Consequences

- Good, because common lossless legacy transitions no longer require manual
  catalog repair.
- Good, because safe narrowing is installation-specific, race-guarded, and
  incapable of truncating an overlength value silently.
- Good, because reports distinguish store type, length, collation, FK action,
  index shape, and protected model-managed mismatch categories.
- Bad, because a successful narrowing proof may scan an entire table.
- Bad, because accepted DDL may copy or rebuild a table and require an
  operational maintenance window.
- Bad, because neighboring conversions remain deliberately unsupported until
  they have an equally complete proof.

### Confirmation

- Run Core planner, projection, serialization, privacy, bounds, and exception
  tests.
- Run positive/negative widening, narrowing, Boolean, dependency, race,
  cancellation, timeout, postflight, and replay tests on every supported server.
- Run 50,000- and 100,000-operation live mixed workloads with real repair and
  rejection paths, not only matching no-ops.
- Run package, public API, EF tooling, engineering, and documentation gates.

## Pros and Cons of the Options

### Add a narrow provider-proven repair allowlist and report schema version 2

- Good, because every accepted transition has named invariants and live tests.
- Bad, because provider-specific proof and operator documentation expand with
  each future allowlisted transition.

### Keep every type or length difference blocked

- Good, because the runtime remains maximally conservative.
- Bad, because proven lossless legacy drift still requires repetitive manual
  intervention outside the canonical migration.

### Accept general provider-convertible type changes

- Good, because more schemas would converge automatically.
- Bad, because provider convertibility does not prove losslessness, semantic
  identity, dependency compatibility, or acceptable operational impact.

### Emit values or raw catalog SQL in exceptions

- Good, because a local operator might see more immediate detail.
- Bad, because secrets, managed values, and high-cardinality data could escape
  through logs, telemetry, or shared incident artifacts.

## More Information

D-002 remains authoritative for provider-neutral policy and complete-definition
comparison. D-004 remains authoritative for preflight, execution, races, and
recovery. D-005 remains authoritative for evidence bounds and privacy.

### Re-evaluation Triggers

- A supported server changes conversion, strict-mode, locking, or online-DDL
  behavior.
- A measured live workload invalidates query, payload, allocation, or timeout
  bounds.
- A neighboring transition obtains a complete provider and value-domain proof.
- Report consumers require a new wire contract beyond schema version 2.

### Decision History

- 2026-09-05: Decision recorded with status proposed.
- 2026-09-05: Dominic Kalkbrenner selected the provider-proven allowlist and
  bounded typed diagnostics; status changed from proposed to accepted.
- 2026-09-05: Implemented provider proofs, grouped runtime guards, ordered
  projection, report schema version 2, live tests, scale qualification, public
  APIs, and operator documentation; status changed from accepted to implemented.

### Implementation References

- [MySQL/MariaDB column analysis](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Features/Columns/MySqlSafeMigrationCatalogSqlBuilder.Columns.cs)
- [PostgreSQL column analysis](../../src/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/Features/Columns/PostgreSqlSafeMigrationCatalogSqlBuilder.Columns.cs)
- [Diagnostic evidence](../../src/Doka.EntityFrameworkCore.SafeMigrations/Analysis/SafeMigrationDiagnosticsEvidence.cs)
- [Report schema version 2](../../schemas/safe-migration-run-report-v2.schema.json)
- [Failure-code runbook](../runbooks/failure-codes.md)

### Sources

- [MySQL 8.4 online DDL operations](https://dev.mysql.com/doc/refman/8.4/en/innodb-online-ddl-operations.html) (primary source; retrieved 2026-09-05)
- [MySQL 8.4 string functions](https://dev.mysql.com/doc/refman/8.4/en/string-functions.html) (primary source; retrieved 2026-09-05)
- [MySQL 8.4 BIT type](https://dev.mysql.com/doc/refman/8.4/en/bit-type.html) (primary source; retrieved 2026-09-05)
- [MySQL 8.4 numeric type syntax](https://dev.mysql.com/doc/refman/8.4/en/numeric-type-syntax.html) (primary source; retrieved 2026-09-05)
- [MariaDB VARCHAR](https://mariadb.com/docs/server/reference/data-types/string-data-types/varchar) (primary source; retrieved 2026-09-05)
- [MariaDB SQL mode](https://mariadb.com/docs/server/server-management/variables-and-modes/sql-mode) (primary source; retrieved 2026-09-05)
- [PostgreSQL 18 character types](https://www.postgresql.org/docs/18/datatype-character.html) (primary source; retrieved 2026-09-05)
- [PostgreSQL 18 information-schema columns](https://www.postgresql.org/docs/18/infoschema-columns.html) (primary source; retrieved 2026-09-05)
- [PostgreSQL 18 ALTER TABLE](https://www.postgresql.org/docs/18/sql-altertable.html) (primary source; retrieved 2026-09-05)
