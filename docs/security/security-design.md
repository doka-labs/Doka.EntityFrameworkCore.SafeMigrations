# Security design and assurance

Recorded 2026-08-27 against the repository implementation. This is a design
and evidence map for review, not a performed independent security audit or a
claim that every supported combination is vulnerability-free. The
[security policy](../../SECURITY.md) owns security scope, invariants, and
private disclosure.

## Assets and exposure

SafeMigrations is an in-process EF Core migration library. It exposes no
listener, user account system, network service, password database, or tenant
authorization layer. Its important assets are:

- integrity of application data, schema, and migration history;
- correct ownership of target objects and canonical context/model identity;
- availability of the migrator, database, and pooled sessions;
- confidentiality of credentials, row data, schema identity, and reports;
- integrity of source, dependency graphs, qualified packages, and release evidence.

The application chooses database credentials, TLS settings, context, migration
assembly, and who may invoke deployment. Reviewed migration code is privileged
application code; it is not made safe for execution by untrusted users merely
because some operations use SafeMigrations.

## Trust boundaries

| Boundary | Potentially hostile input | Required treatment |
| --- | --- | --- |
| Migration author to Core | Definitions, enum values, collections, custom SQL, context configuration | Validate the closed contract and snapshot inputs; do not turn raw SQL into proven equivalence |
| EF/provider generator to scaffolder | Version-sensitive generated C# shape, provider annotations, selected design-time mode | Delegate rendering, validate one bounded source shape, freeze mode into source, reject unknown annotations |
| Database to classifier | Names, defaults, computed/check expressions, catalog shape, conflicting rows | Parameterized catalog filters, typed comparisons, explicit unsupported/data-blocked results |
| Classifier/planner to DDL | Observed state, old/new definitions, capabilities | Exact policy and repair allowlist; prerequisite and postcondition checks |
| Analysis to execution | Time and concurrent database changes | No claim that preflight reserves future state; external write/DDL fence and runtime guards |
| Runtime to pooled session | Cancellation, partial DDL, cleanup failure | Documented provider lifecycle, owned-resource cleanup, no automatic destructive rollback |
| Report to operator/exporter | Instance and object names, version/fingerprint metadata | Restricted evidence storage; telemetry uses only its bounded fields |
| Contributor to CI/release | PR code, workflow scripts, dependencies, downloaded artifacts | Read-only PR qualification, approved source/signers, protected publication, exact-byte readback |

A database server is not a trusted remote attestation service: a compromised
server can misrepresent its catalog. SafeMigrations cannot prove data integrity
against an administrator who controls the database and all returned evidence.
That limitation does not excuse injection or unsafe interpretation of metadata.

## Assurance cases

Each case names an intended property, why the implementation should preserve
it, and executable evidence to examine. Run results must identify the commit,
dependency profile, and engine; a linked test file alone is not proof of a
successful run.

### S1 - SQL roles remain distinct

Catalog values are parameters in analysis; migration scripts have no runtime
parameter channel and therefore use provider identifier/literal rendering.
Typed expressions retain identifier, literal, and grammar roles. Opaque or
provider-fragment SQL does not authorize structural equality.

Controls: [MySQL parameterizer](../../src/Doka.EntityFrameworkCore.SafeMigrations.MySql/Analysis/MySqlCatalogQueryParameterizer.cs),
[PostgreSQL parameters](../../src/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql/Analysis/PostgreSqlCatalogQueryParameters.cs),
and the [closed expression contract](../../src/Doka.EntityFrameworkCore.SafeMigrations/Expressions/SafeMigrationSqlExpressionInspector.cs).
Evidence: [MySQL parameter tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Unit/MySqlCatalogQueryParameterizerTests.cs),
[PostgreSQL parameter tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Infrastructure/PostgreSqlCatalogQueryParametersTests.cs),
and both provider [identifier suites](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Integration/Features/Identifiers).
The [PostgreSQL identifier suite](../../tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Integration/Features/Identifiers)
additionally checks server-deparsed typed expressions. These counter SQL
injection and second-order catalog interpretation errors.

### S2 - Unknown state cannot authorize unsafe convergence

The [planner](../../src/Doka.EntityFrameworkCore.SafeMigrations/Planning/SafeMigrationDecisionPlanner.cs)
is total over validated operation/state/policy inputs. Classification must
distinguish missing prerequisites from data violations. Repair requires
specific proven transitions; unknown extra objects are inventory, not deletion.
Existence-only container behavior is explicit and followed by owned children.

Evidence: [planner tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Features/Lifecycle/SafeMigrationDecisionPlannerTests.cs),
provider feature suites, and lifecycle wrong-kind, rename-collision, opaque-SQL,
unexpected-object, and partial-table tests. A privileged author can still
request an explicit `Drop*` or ordinary SQL operation; authorization for that
request belongs to the deployment review.

### S3 - Analysis and history have distinct authority

The [runner](../../src/Doka.EntityFrameworkCore.SafeMigrations/Analysis/SafeMigrationRunner.cs)
checks canonical model identity and performs read-only analysis outside EF
history. Ordinary provider operations remain explicitly not analyzed.
Snapshot-to-runtime comparison requires a model snapshot; without one, only
the runtime fingerprint is produced unless the caller supplies an independently
established expected fingerprint. Keep the canonical snapshot for normal EF
deployments, and bind execution to the analyzed target rather than an unchecked
latest migration. A fingerprint from the same unchecked runtime model does
not establish canonical identity.
Missing/conflicting registration must not produce safe target DDL or a success
history row. Model fingerprints detect drift; they are not signatures or a
tenant-isolation mechanism.

Evidence: both providers' lifecycle tests for model mismatch, derived contexts,
missing/conflicting adapters, preflight/postflight, history, and mixed ordinary
operations. [MySQL/MariaDB lifecycle](../../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests/Integration/Features/Lifecycle)
and [PostgreSQL lifecycle](../../tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests/Integration/Features/Lifecycle)
must both pass; success on one engine is not evidence for the other.

### S4 - Resource and failure bounds remain explicit

[Query limits](../../src/Doka.EntityFrameworkCore.SafeMigrations/Analysis/SafeMigrationCatalogQueryLimits.cs)
bound each optimizer-visible statement by operation count and each ADO.NET
transport batch by statement count, parameters, and UTF-8 payload. Provider
plan capture is separately windowed. Oversized single operations reject;
partial reports are not published after a later batch fails. Inputs, final
reports, and complete table discovery still grow with cardinality. There is no
global constant-memory or runtime-duration guarantee; callers need
cancellation, appropriate database timeouts, and bounded deployment
concurrency. SafeMigrations applies the configured EF command timeout to its
raw catalog commands and batches.
Native ADO.NET batching is capability-gated by `CanCreateBatch`. Compatible
wrappers without batch support use bounded sequential commands instead of
concatenated provider SQL, retaining parameterization, cancellation, timeout,
ordinal validation, and atomic report publication.

Evidence: [query-limit tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Analysis/SafeMigrationCatalogQueryLimitsTests.cs),
provider edge-case oversized/chunk-order tests, and
[performance budgets](../../eng/performance-budgets.json) with live clean/noisy
catalog measurements. Doka's scoped command owns cleanup after failure or
cancellation and session eviction if cleanup fails. PostgreSQL analysis owns
a read-only repeatable snapshot when no qualifying caller transaction exists;
its advisory analysis lock does not fence application writers. Runtime EF
locking and external SQL-script execution have separate lifecycles.

Evidence includes failed-body, cancellation, pool-eviction, transaction-rejection,
least-privilege, and competing-migrator tests. MySQL/MariaDB implicit commits
remain an external recovery constraint, not a rollback promise.

### S5 - Telemetry does not export protected report contents

[Telemetry](../../src/Doka.EntityFrameworkCore.SafeMigrations/Diagnostics/SafeMigrationTelemetry.cs)
records bounded provider/engine/mode/status or failure-code tags. It does not
attach raw exceptions, SQL, credentials, row values, object names, or instance
IDs. Reports intentionally retain more detail and need restricted storage.

Evidence: [run-contract privacy tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Features/Lifecycle/SafeMigrationRunContractTests.cs)
and the [observability contract](../runbooks/observability.md). Application,
EF, connector, or exporter logging is separately configured; SafeMigrations
does not redact another component's sensitive-data logging.

### S6 - Release evidence identifies the released content

The [release workflow](../../.github/workflows/release-candidate.yml) binds the
qualified commit in protected main history, its exact package artifact,
authorized signed tag, provenance, SBOM, NuGet repository signatures, and
immutable GitHub Release assets. Credentials are requested only in the
protected publication job, and publication never rebuilds packages.

Evidence comes from the shared quality workflow, package-content and consumer
checks, Git's allowed-signers verification, structurally and cryptographically
verified portable SLSA provenance, GitHub attestations and immutable
release-asset verification, and signed public NuGet content readback.
[Consumer verification](release-verification.md) and actual hosted run/settings
evidence remain necessary; local checks cannot establish configured protection
or successful publication.

### S7 - Scaffolding cannot silently widen migration policy

The design-time extension delegates C# argument and annotation rendering to EF
Core, then substitutes only the documented table/index operation calls after
validating one exact generated shape. Strict mode is the default. Legacy mode
is explicitly selected and frozen into the generated C# file; its rollback
body rejects before DDL. Column, constraint, rename, and schema operations are
not reinterpreted automatically. Unknown output shapes and unmodeled provider
annotations fail closed.

Controls: [operation generator](../../src/Doka.EntityFrameworkCore.SafeMigrations/Scaffolding/SafeMigrationCSharpMigrationOperationGenerator.cs),
[migration generator](../../src/Doka.EntityFrameworkCore.SafeMigrations/Scaffolding/SafeMigrationCSharpMigrationsGenerator.cs),
and provider `buildTransitive` discovery assets. Evidence:
[scaffolding unit tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Scaffolding/SafeMigrationScaffoldingTests.cs),
[real EF tooling qualification](../../eng/verify-ef-tooling.sh), provider
identity tests, and package-only Design/Tools/runtime consumer profiles. A
generated migration remains privileged reviewed source; scaffolding is not an
authorization boundary for an untrusted migration author.

## Residual risks and response

Threats include malicious schema metadata, inconsistent legacy data, privileged
incorrect migrations, races between analysis and execution, excessive catalog
size, connection/session loss, leaked diagnostics, compromised dependencies,
and artifact substitution. The cases above identify the relevant controls;
none is a blanket suppression of findings in that class.

Use least-privilege deployment credentials, validated transport, reviewed
artifacts, external write/DDL fences, tested backups, and per-instance
postflight. Reports do not replace application health checks or a restore drill.
Upstream findings require private coordination; this repository changes only
SafeMigrations-owned code and records dependency boundaries honestly.

## Review triggers and record

Re-review when an operation family, expression grammar, catalog interpretation,
provider/engine version, resource bound, diagnostic field, connection lifecycle,
or release trust boundary changes, and during major-release security review.
Record reviewer identity, date, exact revision, scope, assumptions, findings,
negative/positive tests, and dispositions. Do not call an automated review an
independent human security review. OpenSSF assessment uses that actual record,
not the existence of this document.
