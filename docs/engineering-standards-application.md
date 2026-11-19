# Engineering standards application

SafeMigrations applies the central Engineering Standards edition of November 19,
2026. This document owns the product-specific application decisions; it does not
replace the public behavior contracts in the user, provider and operations guides.

## Authority and scope

The reviewed authority snapshots are:

| Document | SHA-256 |
| --- | --- |
| Engineering-Standards.md | `cd554e9873131182c4f36cbe3ea8e0448c0e1e183e5245015049487690aef1c0` |
| Repo-Standard.md | `8167f674a72659d5b199a77d50a5f145b20acbd397385f204f82d928f05931de` |

The maintainer approved implementation of the standards remediation on November
19, 2026. The repository standard also contains decisions for a different initial
product. Those decisions do not remove established SafeMigrations functionality,
qualification, package, security or publishing requirements.

The baseline remains .NET 10 and EF Core 10. The approved xUnit 2 test platform
and its assembly-owned container framework remain in use. .NET 11, a different
test platform and additional release lines require separate authorization.

Corrections use existing internal contracts. They do not add public overloads,
options or consumer responsibilities merely to simplify their implementation.
Unchanged public API and report/schema inventories remain comparison targets.

## Adoption decisions

### Audit policy

Retain the stronger existing SafeMigrations CI, release, dependency-review and
maintenance audit policies. The initial-product template's ordinary-build audit
exceptions do not authorize relaxing these controls. Local vulnerability warnings
retain their documented development-only treatment; release and security checks
continue to enforce their explicit strict policy.

### Dependency ownership and private ABI

MySQL and SQL Server packages retain their published driver and cryptography
minimums. Adopt nonshipping-only transitive pinning and explicit references in the
owning shipping providers, preserving those exact minimum contracts in each
nuspec. The transition is qualified against the existing package floors before
the old pinning scope is removed. A lockfile-only change does not protect package
consumers. Core and other providers must not acquire unrelated dependencies.

Adopt reviewed lockfiles for all 24 default-profile repository projects, replacing
the earlier five-shipping-project-only policy. Set CI identity before evaluating
locked restore. Preserve RID-specific and explicit compatibility-matrix profiles;
isolated package consumers continue generating their own temporary profile locks.
Update D-006 and the Dependabot policy to record this present change without
rewriting the earlier accepted decision.

The four shipping providers use Core internals. Their package dependency must
select the matching Core package identity, including development and prerelease
versions. This own-package private-ABI constraint is not applied to external EF or
provider dependencies. Package qualification verifies both matching and conflicting
own-package selections. Release notes must identify this resolution constraint.

### Cancellation and cleanup

Apply the current central mandatory-token rule to owned asynchronous methods,
including local functions and callbacks. This supersedes the earlier local
captured-token signature exception, without changing the actual operation token.
Tokens are required, last among regular parameters, and accurately named.
Framework signatures retain their required form.

Protected cleanup remains tokenless and passes explicit `CancellationToken.None`
to native APIs where needed. Cancellation of an observer must not abandon actual
work or allow its owner to be disposed while work is pending. Native timeout and
session-quarantine contracts remain distinct from artificial observer deadlines.
Primary failures and cancellation must retain their identity when cleanup fails.

### Rider annotations and authoring settings

The maintainer separately approved JetBrains.Annotations 2026.2.0 on November 19,
2026, exclusively as a build-only dependency. References use `PrivateAssets="all"`.
Qualification must verify that the annotations add neither runtime assembly
references/attributes nor dependencies to published packages. Public API files
remain authoritative; annotations do not replace them.

The team-shared cleanup profile is derived from the current global
`Built-in: Custom` original, not an unchecked sibling-repository copy. Preserve
disabled auto-property transformations. The explicit project exception disables
Markdown reformatting and excludes Markdown in CLI cleanup because automatic
formatting must not alter prose, mathematical notation or examples. Preserve the
existing independent test-process parallelism setting.

### Syntax and navigation

Use explicit constructors for classes and structs, preserving initialization,
captures and ownership. Positional records remain valid. Use C# 14 extension
blocks only after qualifying public API, static/extension calls, generated XML
documentation and the consuming tooling; preserve classical syntax where a real
compatibility requirement remains.

The actual November 19, 2026 compilation probe preserved flattened CLR calls,
but PublicApiAnalyzers 5.6.0 reported RS0016 for public extension containers and
members against the unchanged shipped inventory, including an internal-only
block in a public container. Affected families retain classical extension syntax
with local compatibility explanations. No baseline update or diagnostic
suppression conceals that tooling boundary; conversion needs renewed complete
API, XML and consumer qualification when the toolchain supports the contract.

Centralize imports in each consuming project's Imports.cs. Shared linked sources,
aliases and exact overload binding are reviewed across all consuming projects.
Keep fluent `ToArray()`/`ToList()` materialization; do not replace it with spread
collection expressions. Preserve explicit capacity/comparer and buffer contracts.

Complete virtual solution configuration navigation and explicit project roles
without unnecessary physical relocation. Keep the established `.config` tool
manifest and required `dotnet-ef` consumers. Adopt one canonical database-image
manifest, [docker/database-images.Dockerfile](../docker/database-images.Dockerfile),
with the current supported targets and qualified architecture pins.
Its consumers retain the existing target/environment overrides, architecture
qualifications and tests of native charset/collation defaults; it must not force
a server default that conceals provider differences. This consolidates existing
targets and does not authorize unrelated server-version upgrades.

### Tests and measurements

Functional tests use observable readiness and actual ownership completion, not
machine-speed watchdogs. Deliberate native timeout contracts and external
infrastructure budgets remain explicit and are not performance assertions.
Assessment assertions prove expected cardinality, identity, order and decisions.
Each test has one meaningful Arrange/Act/Assert sequence; coherent retry or recovery
steps may share its Act phase.

Diagnostic benchmarking must be explicitly started and remain informational.
Normal build, test and cleanup must not implicitly start statistical benchmarking,
including BenchmarkDotNet Dry jobs. Once-per-operation diagnostic counters and
duration observations are operational evidence, not a second statistical runner.
Functional workload/configuration assertions remain ordinary deterministic tests.
Optimization candidates require appropriate measurements before implementation;
source shape alone does not establish a speedup or allocation regression.

### Decision records and evidence

Reconcile the existing MADR corpus against the current central profile while
preserving genuine historical facts and original recording dates. The current
`date` identifies the last recorded update. Reciprocal relationships and source
links must be consistent and accessible to repository readers.

Implementation authorization does not accept a proposed ADR retroactively. Missing
historical approval events are identified as evidence limitations, not fabricated
to satisfy a template. Existing proposed records retain their status until their
text is actually accepted. Retain the established decision against adding a new
mandatory ADR validator or generated-index framework.

Publishable, sanitized evidence summaries support repository decisions. Generated
binaries, raw build caches and confidential logs remain untracked. Historical
local observations are not represented as current hosted qualification.

## Verification and change control

The implementation is checked against these choices, the source inventory and
the existing public API, SQL, report, history, package and provider contracts.
Each correction receives relevant positive and negative controls. Qualification
includes actual package consumers and EF CLI, scripts and bundles, not compilation
alone. Native SQL Server qualification requires supported x64 execution.

Passing checks does not authorize a commit, push, workflow dispatch or release.
Those actions retain their separate maintainer approvals. Later authority changes
require an explicit application review rather than silently inheriting a mutable
template.
