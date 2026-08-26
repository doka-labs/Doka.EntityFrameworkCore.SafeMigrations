---
id: D-001
status: implemented
date: 2026-08-26
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Package dependencies and ownership of migration feature implementation"
supersedes: []
superseded-by: []
amends: []
amended-by: []
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-001 -- Separate provider packages and use hybrid vertical slices

## Context and Problem Statement

SafeMigrations expresses the same migration intent for MySQL, MariaDB, and
PostgreSQL, but their catalogs, supported facets, SQL generation, and failure
behavior differ. A consumer using one database family must not restore or
initialize the other provider. Core policy must not acquire provider-specific
branches as individual features evolve.

Within each provider, grouping all catalog queries in one file and all SQL
generation in another spreads one change across large unrelated areas.
Conversely, copying connection handling and decision policy into every feature
would create several competing lifecycle implementations.

The decision is how to partition packages and source ownership so feature
changes remain local without adding runtime discovery or duplicating the
shared migration lifecycle.

## Decision Drivers

- Core knows neither MySQL/MariaDB nor PostgreSQL.
- MySQL and MariaDB share one adapter package but retain explicit engine
  capability distinctions; PostgreSQL has its own adapter.
- Consumers, tests, benchmarks, and package qualification expose separate
  dependency graphs rather than hiding them in a combined application.
- A feature owns its definitions, catalog interpretation, projection, and
  focused tests; policy and lifecycle semantics remain consistent.
- Source organization must not add reflection, per-feature service lookup, or
  an additional runtime registration layer.

## Considered Options

- Independent provider packages with hybrid vertical slices
- One combined provider-aware package
- Independent packages organized only by technical layers
- Independent self-contained feature plugins

## Decision Outcome

Chosen option: "Independent provider packages with hybrid vertical slices",
because it separates database dependencies while co-locating changes to one
migration capability and preserving one shared policy and lifecycle.

The package dependency direction is explicit:

- `Doka.EntityFrameworkCore.SafeMigrations` owns provider-neutral definitions,
  operation contracts, planning, analysis orchestration, and evidence.
- `Doka.EntityFrameworkCore.SafeMigrations.MySql` depends on Core and the Doka
  provider and owns both MySQL and MariaDB behavior.
- `Doka.EntityFrameworkCore.SafeMigrations.PostgreSql` depends on Core and
  Npgsql and owns PostgreSQL behavior.
- Neither adapter references the other. Core does not reference either one.

Inside each package, source ownership mirrors Schemas, Tables, Columns,
Indexes, and the four constraint families. A slice owns its immutable
definitions/intents, builder methods, standard-operation conversion,
fingerprint fields, projection, provider classification, and focused tests.

The shared kernel owns the sealed envelope, total decision planner, report
contract, typed-expression primitives, registration, and execution/analysis
lifecycle. Central dispatchers route to slices; they do not accumulate the
feature rules again. Partial classes organize source without creating a
runtime plugin mechanism or changing public namespaces.

Root MSBuild properties define repository-wide defaults. The src, tests,
benchmarks, and samples scopes import those defaults and add role-specific
settings. Provider dependencies stay visible in their corresponding project
files. Package-only consumers verify the actual published package graph.

### Consequences

- Good, because an adapter can evolve without adding an unrelated provider to
  Core or to a consumer's restore graph.
- Good, because a feature review can follow corresponding Core, provider, and
  test slices while shared lifecycle changes have one owner.
- Bad, because engine-specific implementations and their tests must be kept
  semantically aligned; matching folders alone do not prove parity.
- Bad, because cross-feature rules such as rename propagation require an
  explicit shared-kernel decision instead of arbitrary local duplication.

### Confirmation

Build the complete solution and run all three test projects. Inspect changed
project references and package graphs when adding a slice or project:
provider-crossing dependencies, missing mirrored ownership, feature behavior
in central dispatchers, and aggregate test ownership remain rejected in
review. The package qualification procedure in CONTRIBUTING must build
independent consumers from packages, not references to this repository's
production projects.

A provider feature change also requires the applicable engine matrix and
performance budgets. The architecture checks prove structure and dependency
direction, not SQL correctness, feature parity, or a measured speedup.
These commands describe acceptance evidence; this ADR is not a test-run log.

## Pros and Cons of the Options

### Independent provider packages with hybrid vertical slices

- Good, because deployment dependencies and feature ownership are visible at
  compile time without an additional runtime abstraction.
- Good, because lifecycle, policy, and evidence contracts remain centralized.
- Bad, because provider parity and shared-kernel promotion still require
  deliberate review across corresponding slices.

### One combined provider-aware package

- Good, because installation and registration can expose one package surface
  for an application that intentionally uses every engine.
- Bad, because a single-provider consumer inherits unrelated dependencies and
  provider-specific changes become coupled to one artifact.

### Independent packages organized only by technical layers

- Good, because provider dependencies remain isolated and technical specialists
  can inspect one layer in one location.
- Bad, because each feature change is distributed across aggregate definition,
  classifier, generator, and test files with wider review conflict surfaces.

### Independent self-contained feature plugins

- Good, because independently shipped extensions can own their own lifecycle
  when the product intentionally supports an open operation ecosystem.
- Bad, because the current closed operation contract would gain discovery,
  configuration, and duplicate policy/lifecycle responsibilities without a
  consumer that needs independent feature deployment.

## More Information

This is the package/source ownership decision, not the provider integration
decision in D-003 or the execution lifecycle decision in D-004. Reusing the
word slice does not authorize a public namespace change or a new NuGet package
per operation family.

Common code belongs in the kernel when its semantics are genuinely shared.
Similar SQL spelling is not evidence that two engines have the same behavior.
Numerical performance and allocation limits remain in the versioned budgets;
the folder layout itself is not performance evidence.

### Re-evaluation Triggers

- A supported provider cannot preserve the current dependency direction or a
  public extension contract needs independently deployed feature ownership.
- Two or more slices implement the same semantic primitive and tests establish
  a shared contract that should move to the kernel.
- Package qualification finds an unrelated provider dependency, or measured
  construction/generation behavior changes after an ownership refactor.

### Decision History

- 2026-08-26: Decision recorded with status proposed.
- 2026-08-26: Existing package and vertical-slice implementation documented retrospectively; no earlier approval date is inferred.
- 2026-08-26: Record aligned with Doka MADR Enterprise Profile 1.0 and expanded with alternatives, boundaries, and confirmation evidence.
- 2026-08-26: Maintainer designated @doka-labs/core-maintainers as the informed audience.
- 2026-08-26: Status changed from proposed to accepted. Dominic Kalkbrenner confirmed the recorded decision and its existing implementation.
- 2026-08-26: Status changed from accepted to implemented. Package boundaries and vertical slices are present and verified by project references, package consumers, and provider tests.

### Implementation References

- [Architecture and ownership contract](../vertical-slice-architecture.md)
- [Central package declarations](../../Directory.Packages.props)
- [Production build scope](../../src/Directory.Build.props)
- [Test build scope](../../tests/Directory.Build.props)
- [Benchmark build scope](../../benchmarks/Directory.Build.props)
- [Core features](../../src/Doka.EntityFrameworkCore.SafeMigrations/Features)
- [MySQL/MariaDB features](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Features)
- [PostgreSQL features](../../src/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/Features)
- [Package qualification](../../eng/qualify-packages.sh)
- [Performance budgets](../../eng/performance-budgets.json)

### Sources

- [MSBuild folder-scoped customization](https://learn.microsoft.com/en-us/visualstudio/msbuild/customize-by-directory) (primary source; retrieved 2026-08-26)
