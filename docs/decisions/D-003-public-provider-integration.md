---
id: D-003
status: implemented
date: 2026-08-26
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "EF provider extension boundaries and exact ownership of safe operations"
supersedes: []
superseded-by: []
amends: []
amended-by: []
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-003 -- Integrate through the public Doka SPI and explicit Npgsql composition

## Context and Problem Statement

SafeMigrations must add guarded migration operations without maintaining a
fork of each provider's standard DDL generator. A missing registration must
fail visibly: a provider must not ignore an annotation and execute an ordinary
unguarded operation instead.

The MySQL/MariaDB provider exposes Doka's operation-handler SPI. PostgreSQL
uses the Npgsql migrations generator boundary and requires explicit
composition when an application customizes that generator. Pretending these
boundaries are identical would hide important command and lifecycle semantics.

The decision is how to own safe operations, preserve provider authority over
ordinary operations, and qualify the actual package boundary independently of
the provider repositories.

## Decision Drivers

- Exact, fail-closed ownership of the sealed SafeMigrationOperation envelope.
- Preserve provider SQL helpers, type mappings, active engine features,
  generation options, command order, and transaction semantics.
- No copied provider generator, internal reflection, or source-project
  dependency on the Doka repository.
- The same integration works through EF internal service providers, runtime
  migration, scripts, bundles, and isolated package consumers.
- Caller-selected PostgreSQL baseline generation must remain effective.
- SQL generation remains deterministic and does not query the database.

## Considered Options

- Public Doka handler SPI and explicitly composed Npgsql baseline
- Safe annotations on ordinary EF operations
- Fork or subclass provider-specific generator internals
- Generate and execute all provider DDL outside EF migrations

## Decision Outcome

Chosen option: "Public Doka handler SPI and explicitly composed Npgsql baseline",
because it makes the safe-operation owner explicit while preserving the
provider's existing standard-operation and tooling contracts.

Core exposes a sealed safe envelope and typed intent rather than an annotation
on ordinary EF operations. The provider adapter must recognize that envelope.
Absent or conflicting ownership is an error, never a fallback to unguarded
standard DDL.

For MySQL/MariaDB, one scoped handler owns the exact envelope type through
Doka's public `Doka.EntityFrameworkCore.MySql` namespace.
`RenderStandardOperation` supplies the provider baseline. SafeMigrations
consumes validated command fragments and
returns a scoped setup/body/cleanup command; it does not split provider SQL
on semicolons or derive from the Doka generator.

The read-only analyzer captures the adapter's typed plan while Doka supplies
the real handler context and ordinal. It does not parse emitted commands or
invent a second engine-feature model. Doka remains responsible for dispatch,
command-scope execution, and cleanup; SafeMigrations owns the guard plan,
catalog interpretation, and migration-specific decisions.

For PostgreSQL, the adapter implements `IMigrationsSqlGenerator`, intercepts
safe envelopes, and delegates ordinary operations to an explicit baseline
generator. The typed registration overload selects a caller's custom
baseline; the adapter must not silently replace it with the default.
Baselines inside safe operations use that same selection.

A transaction-suppressed PostgreSQL baseline cannot be wrapped inside the
adapter's guarded DO block and is rejected. Ordinary delegated commands retain
their provider semantics; composition does not make every provider command
safe to embed.

The current MySQL/MariaDB adapter consumes Doka 10.1.0 as an exact NuGet
dependency. Core/provider ranges and committed lockfiles remain repository-owned
inputs. Package qualification uses actual packages, not Doka ProjectReference
or unpublished local source. Provider release approval is not a
cross-repository prerequisite controlled by SafeMigrations.

### Consequences

- Good, because missing or conflicting safe-operation ownership is observable
  before target DDL and successful migration history.
- Good, because provider standard DDL and application PostgreSQL customization
  remain authoritative rather than being copied into SafeMigrations.
- Bad, because the two adapters intentionally have different integration
  mechanics and need distinct lifecycle/composition tests.
- Bad, because public provider contracts are still version-sensitive; a clean
  compilation is insufficient evidence for an upgrade.

### Confirmation

Run the provider suites from CONTRIBUTING, the EF tooling procedure, and
package qualification across the support matrix. Require evidence for:

- options registration in both call orders and explicit internal-service-
  provider use;
- no target DDL or success-history row when ownership is absent or conflicting;
- ordinary provider operations mixed with safe operations;
- custom Npgsql baseline use for ordinary DDL and safe-operation baselines;
- rejection of unsupported transaction-suppressed guarded PostgreSQL commands;
- command fragment order and scope behavior without SQL parsing;
- normal, idempotent, no-transaction, CLI, and bundle paths;
- independently restored package consumers with the intended dependencies.

The Doka package-contract and provider composition tests are linked below.
The runtime cleanup evidence belongs to D-004. Upgrade qualification must
record exact resolved package versions and applicable engine results, not
only the source branch of an upstream provider.

## Pros and Cons of the Options

### Public Doka handler SPI and explicitly composed Npgsql baseline

- Good, because exact operation ownership and delegation preserve both safe
  semantics and the provider's existing extension surface.
- Bad, because integration code and tests must reflect real differences between
  Doka's scoped SPI and PostgreSQL generator composition.

### Safe annotations on ordinary EF operations

- Good, because annotations can carry provider-specific metadata through
  established EF operation types with little new public surface.
- Bad, because a generator that does not understand the annotation can still
  execute the ordinary operation without the required guard.

### Fork or subclass provider-specific generator internals

- Good, because direct access to provider implementation details can expose
  behavior not yet available through a public extension point.
- Bad, because copied/internal behavior creates upgrade coupling and can
  diverge from standard DDL, command scopes, and supported tooling.

### Generate and execute all provider DDL outside EF migrations

- Good, because a standalone executor can define its own operation and cleanup
  protocol without depending on a provider generator extension.
- Bad, because it duplicates SQL generation, history, locking, execution, and
  tooling concerns already owned by EF and the selected provider.

## More Information

D-001 defines package boundaries and D-004 defines runtime ownership. This
decision does not promise identical SQL or transactions on both providers.
Equivalent product intent may legitimately yield different command plans or
a stable Unsupported result.

Typed identifiers, literals, and expressions remain distinct from arbitrary
SQL. Provider catalog decompilation is version-sensitive, particularly for
PostgreSQL expressions. An unrecognized form must remain unproven rather than
being accepted through broad string normalization.

### Re-evaluation Triggers

- A provider changes exact dispatch, baseline rendering, command fragments,
  generation options, or transaction-suppression behavior.
- Npgsql introduces a suitable public operation-handler contract, or a caller's
  supported baseline can no longer be composed without semantic loss.
- An upgrade breaks package, lifecycle, expression, or EF tooling qualification.

### Decision History

- 2026-08-26: Decision recorded with status proposed.
- 2026-08-26: Existing integration against stable Doka 10.0.0 and Npgsql documented retrospectively without assigning historical approval.
- 2026-08-26: Record aligned with Doka format and expanded with explicit ownership, composition, and package-boundary consequences.
- 2026-08-26: Maintainer designated @doka-labs/core-maintainers as the informed audience.
- 2026-08-26: Status changed from proposed to accepted. Dominic Kalkbrenner confirmed the recorded decision and its existing implementation.
- 2026-08-26: Status changed from accepted to implemented. The public Doka SPI integration and explicit Npgsql composition are implemented and verified by package-contract, composition, suppression-boundary, and provider tests.
- 2026-08-26: Clarified the SPI's actual root namespace against the Doka 10.0.0 package API; integration and decision status are unchanged.
- 2026-08-28: Updated the exact dependency to stable Doka 10.1.0 after confirming that its additive provider APIs leave the migration-operation handler SPI unchanged; the decision remains implemented and requires fresh SafeMigrations qualification.

### Implementation References

- [Exact Doka handler](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/SqlGeneration/MySqlSafeMigrationOperationHandler.cs)
- [Typed plan capture](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Analysis/MySqlSafeMigrationPlanCapture.cs)
- [MySQL/MariaDB registration](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Extensions/MySqlServiceCollectionExtensions.cs)
- [PostgreSQL composed generator](../../src/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/SqlGeneration/PostgreSqlSafeMigrationsSqlGenerator.cs)
- [PostgreSQL registration](../../src/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/Extensions/PostgreSqlServiceCollectionExtensions.cs)
- [Doka package-contract tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Unit/DokaPackageContractTests.cs)
- [MySQL/MariaDB composition tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Infrastructure/MySqlServiceCompositionTests.cs)
- [PostgreSQL composition tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Infrastructure/PostgreSqlServiceCompositionTests.cs)
- [EF tooling gate](../../eng/verify-ef-tooling.sh)
- [Dependency upgrade contract](../efcore-provider-upgrade-risk.md)
- [Support and qualification](../support-and-qualification.md)

### Sources

- [Doka 10.0.0 migration-operation handler contract](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.0.0/docs/migration-operation-handlers.md) (primary source; retrieved 2026-08-26)
- [Doka 10.1.0 migration-operation handler contract](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.1.0/docs/migration-operation-handlers.md) (primary source; retrieved 2026-08-28)
- [Doka.EntityFrameworkCore.MySql 10.1.0 package](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.1.0) (primary package metadata; retrieved 2026-08-28)
- [Npgsql EF Core provider and configuration](https://www.npgsql.org/efcore/) (primary source; retrieved 2026-08-26)
