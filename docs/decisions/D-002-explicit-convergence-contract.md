---
id: D-002
status: implemented
date: 2026-08-26
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Convergence of heterogeneous legacy schemas into one canonical Core migration path"
supersedes: []
superseded-by: []
amends: []
amended-by: []
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-002 -- Converge explicit owned objects to one canonical Core model

## Context and Problem Statement

Legacy application installations can contain different subsets of module
tables and columns. Empty tables were copied between databases, and several
DbContexts are being consolidated. Neither a common historical schema nor a
trustworthy universal old migration sequence is available.

A table can exist while required columns or constraints are missing.
Create-if-absent at table level therefore cannot establish the intended
application contract. Generating a new target model from each database would
preserve the divergence instead of providing the required shared path.

The decision is how to express one forward migration sequence that converges
different starting states without guessing ownership, deleting unknown data,
or claiming that unrelated historical migrations actually ran.

## Decision Drivers

- One canonical CoreDbContext model and ordered Core migration path for all
  installations, despite different observed starting states.
- Explicit ownership and definition checks at the object level.
- Unknown additive legacy objects survive convergence and remain visible.
- Missing, incompatible, unsupported, and data-blocked states must not be
  collapsed into a generic already-exists success.
- Repairs require proven, allowlisted semantics rather than inferred intent.
- Instance-specific runtime context classes must not redefine the shared
  Core schema or silently select a different migration history.

## Considered Options

- Explicit granular convergence against one canonical Core model
- Table-existence checks with a synthetic baseline history
- Per-installation schema inference and generated migration paths
- Rebuild every installation into a fresh database

## Decision Outcome

Chosen option: "Explicit granular convergence against one canonical Core model",
because it fixes the shared destination while making each installation's
observed state an input to explicit, fail-closed operations.

A convergence migration describes owned tables, columns, indexes, and
constraints using immutable expected definitions. `ConvergeTable` emits a
container-mode table operation followed by individual child operations using
the selected policy, which defaults to ThrowIfDifferent. A missing table is
created from its complete definition; an existing partial table is kept and
each declared child is checked separately. Choosing ExistenceOnly explicitly
relaxes those child checks; table existence alone never skips emitting them.

Strict table comparison remains a different, explicit choice: it requires the
complete owned shape and rejects conflicting members. Convergence containers
preserve unowned additions; the inventory reports them without deleting them.

The pure planner combines operation family, observed state, policy, and
provider-proven repair capability. `ExistenceOnly` is an explicit relaxation
for the relevant ensure cases, not a hidden default. `ThrowIfDifferent`
rejects incompatible definitions. `RepairIfSafe` can authorize only the
adapter's demonstrated allowlist; it is not general schema synchronization.
Unsupported expressions and violated data prerequisites remain blocked.

The canonical context, migration assembly, snapshot, and Core history are
shared across installations. This canonical snapshot describes the new
target, not a claim that all legacy databases once matched it. A derived
runtime context is supported when it is assignable to the configured canonical
context and preserves its relational model. The model guard uses EF's model
differ; an expected fingerprint can additionally bind deployment evidence.

Schema-bearing instance extensions use a separate context and history.
An installation-specific derived type is not permission to alter Core columns
or substitute an instance-specific Core migration sequence.

EF records successful execution of the new sequence through normal migration
history. SafeMigrations does not fabricate the installation's missing past.
A heterogeneous convergence baseline has no generally valid destructive
inverse: recovery uses an explicit forward fix or a tested restore.

### Consequences

- Good, because the same migration can complete a missing table, add missing
  owned children, and leave a matching installation unchanged.
- Good, because unknown legacy additions remain inspectable without becoming
  automatically owned or destructively synchronized.
- Bad, because conflicting definitions and incompatible existing rows require
  a reviewed transformation; a safe tool cannot infer their business meaning.
- Bad, because authors must specify expected facets, ordering, and ownership
  and maintain a canonical target independently of the legacy starting states.

### Confirmation

Run the Core suite through the project command in CONTRIBUTING. In particular,
the builder, expected-definition, state-space, and preflight-projection tests
must cover granular expansion, immutable input, every defined state/policy
combination, and rejection of invalid enum values.

Run both provider integration suites on every supported engine cell. Confirm:

- a missing convergence table reaches its complete target;
- an existing partial table receives missing children;
- a matching second run is idempotent;
- unexpected legacy objects remain present and are reported;
- differing definitions, unsupported SQL, and conflicting data reject;
- matching derived contexts use canonical migrations and history;
- schema-changing derived contexts and wrong model fingerprints reject;
- pending preflight rejects unknown/backward targets without writing history;
- the sample baseline's Down fails before producing destructive operations.

The lifecycle and feature tests linked below implement these scenarios.
A local default MariaDB run does not establish MySQL qualification. A passing
history check alone does not prove live schema equivalence.

## Pros and Cons of the Options

### Explicit granular convergence against one canonical Core model

- Good, because one deterministic target accommodates several real starting
  states without making schema ownership implicit.
- Bad, because the safe boundary deliberately stops where data transformation
  or semantic equivalence cannot be proven.

### Table-existence checks with a synthetic baseline history

- Good, because it can be sufficient when every existing table is independently
  known to match the target and the historical baseline is trustworthy.
- Bad, because those prerequisites do not hold here; missing child objects
  would survive while fabricated history suggests completed work.

### Per-installation schema inference and generated migration paths

- Good, because tailored scripts can represent genuinely different product
  schemas when separate ownership and targets are intentional.
- Bad, because this violates the shared Core destination and turns ambiguous
  names, renames, and legacy differences into inferred migration intent.

### Rebuild every installation into a fresh database

- Good, because a verified export/transform/import can create a known schema
  without retaining unknown structural differences.
- Bad, because data mapping, downtime, integrity checks, and cutover become
  mandatory for every installation, even where additive convergence is enough.

## More Information

D-001 defines package ownership; D-004 defines when analysis is trustworthy
and why runtime guards and postflight are still needed. This record does not
claim that preflight alone freezes a database.

A future explicit drop or rename in the shared path is still possible when
authored and reviewed as such. Preserving unknown objects means that
convergence does not infer destructive cleanup; it does not mean all explicit
destructive migrations are silently turned into no-ops.

### Re-evaluation Triggers

- A product requirement intentionally introduces different Core target schemas
  rather than merely different installation starting states.
- New engine metadata permits a previously unsupported facet to be proven
  equivalent without weakening typed-expression or ownership boundaries.
- A proposed repair needs data conversion, narrowing, or destructive changes
  outside the current allowlist.

### Decision History

- 2026-08-26: Decision recorded with status proposed.
- 2026-08-26: Existing convergence and canonical-context implementation documented retrospectively without reconstructing historical approvals.
- 2026-08-26: Record expanded in Doka format to distinguish unknown legacy origins, canonical target snapshots, and per-instance extensions.
- 2026-08-26: Maintainer designated @doka-labs/core-maintainers as the informed audience.
- 2026-08-26: Status changed from proposed to accepted. Dominic Kalkbrenner confirmed the recorded decision and its existing implementation.
- 2026-08-26: Status changed from accepted to implemented. Explicit convergence, canonical-context ownership, and model guards are implemented and covered by the referenced Core and provider lifecycle tests.

### Implementation References

- [Table and convergence builders](../../src/Doka.EntityFrameworkCore.SafeMigrations/Features/Tables/SafeMigrationBuilderExtensions.Tables.cs)
- [Total decision planner](../../src/Doka.EntityFrameworkCore.SafeMigrations/Planning/SafeMigrationDecisionPlanner.cs)
- [Canonical migrations assembly](../../src/Doka.EntityFrameworkCore.SafeMigrations/Infrastructure/SafeMigrationMigrationsAssembly.cs)
- [Runner and model guard](../../src/Doka.EntityFrameworkCore.SafeMigrations/Analysis/SafeMigrationRunner.cs)
- [Operation-family builder tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Features/Lifecycle/SafeMigrationBuilderExtensionsTests.Lifecycle.cs)
- [Table-definition builder tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Features/Tables/SafeMigrationBuilderExtensionsTests.Tables.cs)
- [Planner tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Features/Lifecycle/SafeMigrationDecisionPlannerTests.cs)
- [Cross-object projection tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Features/Lifecycle/SafeMigrationPreflightProjectionTests.Lifecycle.cs)
- [Column-policy projection tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Features/Columns/SafeMigrationPreflightProjectionTests.Columns.cs)
- [MySQL/MariaDB lifecycle cases](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Integration/Features/Lifecycle)
- [PostgreSQL lifecycle cases](../../tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Integration/Features/Lifecycle)
- [Deployment and recovery](../runbooks/deployment-and-recovery.md)
- [Runnable sample](../../samples/Doka.EntityFrameworkCore.SafeMigrations.Sample/README.md)

### Sources

- [EF Core migration application and history-based idempotent scripts](https://learn.microsoft.com/en-us/ef/core/managing-schemas/migrations/applying) (primary source; retrieved 2026-08-26)
