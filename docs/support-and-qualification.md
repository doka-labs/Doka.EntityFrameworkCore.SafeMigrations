# Support and qualification

## Support contract

The 11.0 development line targets .NET 11 and EF Core 11 across all five
packages. Published upstream GA packages exist for every provider; no provider
RC is required. Upstream availability does not establish SafeMigrations native
qualification or publication. The [11.0 upgrade](#upgrade-to-1100) describes the
framework and dependency boundary; the 10.x sections below retain their
historical contracts and evidence.

### Previous 10.x contracts

Version 10.7.3 corrects asynchronous ownership, cancellation propagation,
cleanup failure handling, SQL Server application-lock validation and EF tooling
server identity checks through the existing public APIs. It retains .NET/EF
Core 10, public signatures, report schemas and fingerprint formats while
updating dependency floors. Review the [10.7.3 upgrade](#upgrade-to-1073),
including aligned SafeMigrations package versions and provider-owned dependencies.

Version 10.7.0 introduced required cancellation tokens on owned asynchronous
APIs and BenchmarkDotNet for engineering measurements. Removing public optional-
token defaults was an intentional source-breaking exception within 10.x, not a
strictly SemVer-compatible change. Version 10.7.3 retains those requirements.

Version 10.6.0 introduced public CASE expressions, certified generated-column
and CHECK support, lossless signed-integer upgrades, ordered projection/DROP
corrections and stack-safe traversal. Measured allocation reductions do not
change migration safety or make hardware-dependent timing a qualification gate.
It retains the 10.5.0 diagnostics with canonical report schema 4, selected
view schema 3 and contextual owned runtime errors. Older schema
files remain packaged; update validators and rebuild references to the compiled
`CurrentSchemaVersion` constant.
Those diagnostics do not change migration decisions or the target framework.
The separate Doka upgrade below changes the MySQL/MariaDB dependency range.
MySQL/MariaDB callers must account for the owned-guard outer exception type;
see [diagnostic compatibility](diagnostics.md#runtime-provider-differences).
Native qualification still requires the complete blocking matrix; ARM SQL Server
skips and offline SQL-shape tests are not substitutes for its x64 cells.

## Upgrade to 11.0.0

Upgrade the application SDK/runtime and complete EF/provider graph together.
All five SafeMigrations packages now target `net11.0`; do not load them into an
EF Core 10 process. Keep the own-package versions identical, including local
`11.0.0-dev` builds, because adapters consume Core's private ABI. The default
development version is not a published stable release. Applications staying on
.NET/EF Core 10 must remain on the compatible 10.x line.

The repository selects SDK 11.0.100, EF Core and dotnet-ef 11.0.0, Doka
`[11.0.0,11.1.0)`, Npgsql EF `[11.0.0,12.0.0)` and dependency-injection
abstractions `[11.0.0,11.1.0)`. Restore a coherent application graph rather than
suppressing downgrade or compatibility warnings. Doka 11 is consumed directly
from NuGet; no local Doka build or temporary opt-in framework property is needed.

SQL Server now requires SqlClient at least 7.1.2 and PKCS at least 11.0.0.
Its exact SNI.runtime 7.1.2 development/testing license exception replaces the
historical 6.0.3 exception; the unused NativeInterop exception is removed.
See [secure development](security/secure-development.md#sql-server-dependency-license-exceptions).
SqlClient 7 moved its built-in Azure/Entra authentication implementations to
`Microsoft.Data.SqlClient.Extensions.Azure`. Applications using those modes must
select and configure that optional package themselves. SQL-password, Windows
integrated authentication and application-supplied access tokens do not require
it. SafeMigrations does not add an Azure dependency to every SQL Server consumer.
See Microsoft's [SqlClient release notes](https://learn.microsoft.com/en-us/sql/connect/ado-net/microsoft-data-sql-client-release-notes)
and [authentication guidance](https://learn.microsoft.com/en-us/sql/connect/ado-net/sql/azure-active-directory-authentication).

EF11 defaults to SQL Server compatibility level 160, and Npgsql 11 defaults to
PostgreSQL 16. Applications targeting SQL Server 2019 or PostgreSQL 14/15 must
explicitly select the appropriate provider compatibility level. The existing
qualification cells remain; changing a provider default does not silently remove
an older engine cell. EF11 also rejects migration execution without migrations,
and its Tools package no longer supplies Design transitively. Reference Design
explicitly in design-time/tooling projects, retaining its private assets.

The existing MySqlConnector minimum remains 2.6.2; explicit 3.0.0 selection is
also allowed. Doka's `DefaultCharSet`/`DefaultCollation` APIs remain application
choices, not implicit model rewrites or safety bypasses. Ordinary model changes
still need reviewed migrations and matching snapshots. Additional EF11 migration
assemblies preserve canonical context ownership and duplicate-ID rejection;
the latest registered assembly supplies its snapshot, including an absent one.

No new public SafeMigrations overload is required for this framework migration.
Existing migration policies, report schemas, fingerprint formats, runtime guards,
history and recovery contracts remain. Updating dependencies does not grant a
schema change, rewrite historical migration source or waive fingerprint checks.
Public framework-facing implementations now follow EF11's own signatures.

For canonical timestamp-prefixed EF11 migration IDs, enabled SafeMigrations
scaffolding uses `Migration{timestamp}{name}` for both generated partial class
names. This retains timestamp uniqueness without the native leading underscore;
filenames, `MigrationAttribute` and snapshot `LastMigrationId` retain the original
full migration ID. Disabled scaffolding remains provider-owned.

The model-fingerprint v1 serialization and schema/facet ordering remain unchanged,
but EF11's `InversePropertyAttributeConvention:InverseNavigations` annotation can
contain a closed generic `Type.FullName` whose framework assembly identity changes
from `10.0.0.0` to `11.0.0.0`, changing the resulting hash without a schema change.
Cross-major hash identity is therefore not guaranteed: review and recompute any
persisted `ExpectedModelFingerprint` against the intended EF11 baseline rather
than disabling validation or blindly reusing the EF10 digest.
Runtime-versus-snapshot comparisons continue normally when both models use the
reviewed EF11 graph.

BenchmarkDotNet 0.16.0-preview.2 is a nonshipping dependency required for its
.NET 11 runtime support. Benchmark execution remains explicitly opted in, with
timing/allocation values informational. A successful restore/build or upstream
package publication is not native, package, CLI/script/bundle or release evidence.
The complete hosted matrix, including native x64 SQL Server, remains blocking.
See [local EF11 verification](../eng/compatibility/ef11/README.md) and the
[publication status](operations/release-publication.md).

Primary contracts checked on 2026-11-21:

- [EF11 breaking changes](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-11.0/breaking-changes).
- [Npgsql 11 release notes](https://www.npgsql.org/efcore/release-notes/11.0.html).
- [Doka 11.0.0 metadata](https://api.nuget.org/v3-flatcontainer/doka.entityframeworkcore.mysql/11.0.0/doka.entityframeworkcore.mysql.nuspec).
- [SQL Server EF11 metadata](https://api.nuget.org/v3-flatcontainer/microsoft.entityframeworkcore.sqlserver/11.0.0/microsoft.entityframeworkcore.sqlserver.nuspec).

## Upgrade to 10.7.3

Keep every installed SafeMigrations package on version 10.7.3 and install only
the adapters needed by the application. Each adapter requires its exact matching
Core package, `[10.7.3]`, because it consumes Core internals; mixing own-package
versions is not supported. Candidate versions follow the same exact-identity
rule. External provider dependencies retain their separately bounded ranges.

Public APIs and their shipped baseline are unchanged from 10.7.2. Required
operation tokens introduced in 10.7.0 remain required; framework overrides keep
their framework signatures. Protected cleanup remains non-cancellable where
cancellation could abandon an owned resource or leave session state unsafe.

The fixes retain ownership until actual analysis, recovery and disposal work
has finished. Cancellation does not release a command, transaction or connection
while that work still uses it. The original work or cancellation failure remains
primary if cleanup also fails; cleanup-only failures and secondary cleanup
evidence remain observable. Removing observer-only deadlines does not change
native command timeouts or permit detached work after resource disposal.

SQL Server rejects unproven application-lock status and quarantines failed
scope release without disposing caller-owned transactions or hiding the primary
failure. EF tooling validates native server identity before selecting its provider
profile or scaffolding migrations; missing, malformed and mismatched identities
are rejected rather than used to select an incompatible engine profile.

The dependency update below requires EF Core and dependency-injection
abstractions at least 10.0.15, Doka `[10.7.0,10.8.0)` for MySQL/MariaDB,
and PKCS at least 10.0.15 only for SQL Server. SqlClient remains at minimum
6.1.8. MySqlConnector remains at minimum 2.6.2 with explicit 3.0.0 selection
allowed; the default profile does not force a connector-major upgrade.

Report schema 4, view schema 3, diagnostic codes, fingerprint formats, authored
migration policies and history remain unchanged. Updating packages alone needs
no database schema migration, history rewrite or fingerprint approval renewal.
Applications upgrading from an older release must still apply the inherited
[10.7.2 catalog/default corrections](#upgrade-to-1072) and
[10.7.0 explicit-token requirements](#upgrade-to-1070).
The complete native, CLI/script/bundle, isolated-package, coverage and release
evidence gates remain required; ARM SQL Server skips do not qualify native x64.

## Stable 10.7.3 dependency update

The 10.7.3 source required Doka `[10.7.0,10.8.0)` for MySQL/MariaDB.
This changes the dependency floor from SafeMigrations 10.7.2; the historical
version notes below retain their original dependency contracts. The target
remains .NET/EF Core 10. Doka's new feature APIs are opt-in: this update does
not enable them, change application models or rewrite historical migrations.
MySqlConnector remains at minimum 2.6.2; System.IO.Hashing advances to 10.0.15.
Consumers may explicitly select MySqlConnector 3.0.0 without changing the
SafeMigrations APIs or excluding supported 2.x applications. Its Microsoft
dependency-injection and logging abstraction minimums are 10.0.15; explicit
application pins must satisfy those requirements rather than suppressing
downgrade diagnostics. The default repository profile remains on connector 2.6.2.
See the [published connector 3.0.0 dependency metadata](https://api.nuget.org/v3-flatcontainer/mysqlconnector/3.0.0/mysqlconnector.nuspec).
Native, tooling and isolated-package qualification remain release requirements.

Its SDK pin is 10.0.404; EF Core, dependency-injection abstractions
and dotnet-ef advance to 10.0.15. SQL Server also requires PKCS minimum
10.0.15: EF Core SQL Server 10.0.15 now declares PKCS at least 10.0.13, so
retaining a 9.x minimum would create a dependency downgrade. This remains a
SQL Server-only dependency; Core and the other adapters do not acquire it.
SqlClient remains on the separately reviewed 6.1.8 line. Native, tooling and
isolated-package qualification requirements remain unchanged.

## Upgrade to 10.7.2

Keep every installed SafeMigrations package on version 10.7.2 and install only
the adapters needed by the application. Public APIs and shipped baselines,
.NET/EF Core 10, dependency floors and provider isolation are unchanged from
10.7.1. MySQL/MariaDB still requires Doka `[10.6.5,10.7.0)`; this patch introduces
no new provider dependency, application option or migration policy.

The fixes preserve native function/type/operator roles, exact numeric tokens,
binary literal ownership and complete temporal values. Ambiguous SQL is no
longer silently reinterpreted as a portable expression. It remains opaque and
follows the provider's existing acceptance boundary; an unproved expression
can still be rejected. Review intentional typed expressions or provider mappings
rather than bypassing the guard or deleting native quoting blindly.

Corrected capture of numeric SQL defaults can change the resulting operation-
contract fingerprint even though its format is unchanged. Regenerate and review
affected fingerprint-bound approvals against the immutable migration and the
selected library version. Do not reuse an old digest to suppress a mismatch,
rewrite historical migrations or disable fingerprint validation. Explicitly
authored typed-expression identities are unchanged. See
[captured SQL defaults and literal ownership](migration-authoring.md#generated-check-constraints).

Binary literal inputs and the public `Value` result are defensive snapshots.
Mutating either array no longer changes an existing expression, definition or
fingerprint. Treat the value as data to inspect, not an API for mutating a
previously constructed contract.

- MySQL/MariaDB recognizes equivalent native nested AND/OR parentheses and
  complete temporal/default spellings while retaining predicate order and real
  literal drift. Functional-index comparison does not authorize unproved creation.
- PostgreSQL resolves expression keys, partial predicates and collations against
  native relation identity; finite temporal values cannot impersonate infinities.
- SQLite reads quoted/depth-sensitive index, collation and constraint metadata
  at its actual grammar positions, including rebuild and replay boundaries.
- SQL Server renders scalar predicates without losing UNKNOWN or changing CHECK
  semantics. Native dependency refusals and x64 qualification remain required.

Report schema 4, view schema 3, diagnostic codes, relational model-fingerprint
format and migration history remain unchanged. Updating packages alone requires
no additional database migration. SQL layout changes retain native values,
parameter ownership and byte/batch limits; no measured speedup is claimed.

The inherited [10.7.1 table/rebuild fixes](#upgrade-to-1071) and
[10.7.0 explicit-token requirements](#upgrade-to-1070) still apply. Required
native, tooling, package, coverage and public-readback gates are unchanged;
local ARM SQL Server skips never establish x64 qualification. Publication status
and exact candidate evidence belong in the
[release runbook](operations/release-publication.md), not product version claims.

## Upgrade to 10.7.1

Keep every installed SafeMigrations package on version 10.7.1 and install only
the adapters needed by the application. The MySQL/MariaDB adapter requires
Doka `[10.6.5,10.7.0)` and retains MySqlConnector minimum 2.6.2. EF Core,
dependency-injection and SQL Server transport floors are unchanged from 10.7.0;
provider dependencies remain isolated.

Public APIs and their shipped baselines remain unchanged from 10.7.0. The
internal scaffolder places certified table annotations inside the existing
typed `constraints` callback before immutable capture. The existing
`CreateTableIfNotExists<TColumns>` and `ConvergeTableFromModel<TColumns>` methods
retain their signatures and policy defaults. No new callback, factory overload,
configuration option or application wrapper is required.

MySQL/MariaDB now preserves an explicitly authored InnoDB requirement through
safe scaffolding, capture, DDL, projection and catalog checks. Unrelated provider
metadata and unsupported storage engines remain rejected; no existing table is
automatically converted. Review newly scaffolded annotation placement and its
captured contract rather than rewriting deployed migration history. An explicitly
captured engine participates in the authored contract fingerprint; an unchanged
fingerprint format is not permission to reuse an approval for a changed contract.
See [explicit InnoDB contracts](mysql-mariadb-ddl-behavior.md#explicit-innodb-table-contracts).

SQLite can remove an explicitly dropped FK on an owner certified to retire
within the same atomic structural window, including private populated cycles
absent from the final model. Surviving references, unsupported artifacts and
unmodeled retained owners still block. Safe rebuilds preserve native retained
column order unless explicitly overridden by model `ColumnOrder`; fresh owners
and ordinary EF lifetimes keep their provider semantics. See
[retiring-table foreign keys](sqlite-behavior.md#foreign-keys-on-retiring-tables).

PostgreSQL and SQL Server migration contracts are unchanged by these fixes.
Report schemas, diagnostic transport and fingerprint formats remain unchanged;
newly authored metadata is still part of its captured contract. Updating packages
alone requires no additional database migration or approval bypass. Runtime
guards, recovery, history, native qualification and coverage remain mandatory.
Action/image updates retain the existing engine lines and required checks;
C# cleanup claims no measured performance improvement.

Applications upgrading from before 10.7.0 must also apply its
[required-token source changes](#upgrade-to-1070), including the documented
SemVer exception. Earlier [10.6.0 approval boundaries](#upgrade-to-1060) still
apply; never reuse an obsolete approval or rewrite historical evidence.

## Upgrade to 10.7.0

Keep every installed SafeMigrations package on version 10.7.0 and install
only the adapters needed by the application. This release deliberately keeps
the .NET/EF Core 10 package line despite removing public optional-token defaults.
That is an explicitly approved exception to the
[SemVer major-version rule](https://semver.org/spec/v2.0.0.html#spec-item-8),
not a backward-compatible source change. Recompile and review all affected
call sites before deployment; late-bound callers must also provide the token.

Repository-owned cancellation parameters are now required. When recompiling,
update calls to runner/provider-analyzer methods and owned helpers to pass the
operation's token explicitly, or `CancellationToken.None` when intentionally
uncancellable. Existing binary signatures are unchanged; framework-owned
EF/ADO.NET seams retain their parameter conventions. This source contract
change does not require a database migration or alter SQL, policies, history,
report schemas or fingerprint formats. See the
[cancellation contract](api-reference.md#cancellation-contract).

For example, update `runner.AnalyzePendingMigrationsAsync(context, options)`
to `runner.AnalyzePendingMigrationsAsync(context, options, cancellationToken)`.
Use `CancellationToken.None` only when the operation is deliberately
uncancellable; do not restore silent defaults in application wrappers.

The MySQL/MariaDB adapter now requires Doka `[10.6.4,10.7.0)` and locks
the published 10.6.4 bug-fix package. Transport minimums and provider isolation
are unchanged; this dependency update requires no schema migration.

The SDK/EF/transport floors from [10.6.1](#upgrade-to-1061) still apply.
Applications upgrading from before 10.6.0 must also review its
[computed/CHECK approval boundaries](#upgrade-to-1060). BenchmarkDotNet `Dry`
runs validate execution/export only; durations and allocation measurements
remain informational. Native provider, coverage, tooling, package and public
readback gates are not waived by the unchanged CLR method signatures.

## Upgrade to 10.6.1

Keep every installed SafeMigrations package on the same 10.6.1 version; install
only the provider adapters the application needs. The EF Core and dependency-
injection floors advance to 10.0.14. MySQL/MariaDB requires Doka
`[10.6.3,10.7.0)` and promotes MySqlConnector to a 2.6.2 minimum. Doka retains
its `<3.0.0` connector bound and System.IO.Hashing minimum of 10.0.14.
SQL Server promotes SqlClient to minimum 6.1.8 and retains its separate PKCS
9.x servicing line with minimum 9.0.22; selecting another adapter adds neither.
Review direct consumer pins against the [dependency contracts](#dependencies-and-package-identity).

The SDK 10.0.403 and dotnet-ef 10.0.14 pins qualify repository builds/tooling;
they do not introduce a .NET 11 runtime or a new provider dependency.
Public signatures, migration policies, report schemas, fingerprint formats and
historical migration source are unchanged relative to 10.6.0. No additional
approval renewal or database schema migration is required solely for this patch.
Upstream dependency servicing still requires application qualification; it is
not evidence that every newly scaffolded model difference can be ignored.

Repository development keeps NU1901-NU1904 visible as warnings, while CI and
explicit release/consumer/SBOM qualification continue treating advisories as
errors. Auditing remains enabled; this is not a consumer warning-policy
override, vulnerability suppression or substitute for upgrading dependencies.
See [contributor checks](../CONTRIBUTING.md#local-advisory-warnings-and-strict-qualification).

Applications upgrading from before 10.6.0 must also review the earlier
[10.6.0 contract changes](#upgrade-to-1060), especially existing computed/CHECK
fingerprint approvals. The complete native matrix and package/public-readback
gates remain mandatory; see the [publication runbook](operations/release-publication.md).

## Upgrade to 10.6.0

The 10.6.0 minor release introduced the following boundaries. They also apply
when upgrading an older application directly to 10.7.2. Keep all installed
SafeMigrations packages on the selected shared version and review newly
scaffolded implicit index-length changes without rewriting historical migrations.

MySQL/MariaDB and PostgreSQL now qualify explicit lossless signed-integer
widening under `RepairIfSafe` when exact source, ancillary facets and native
storage/dependency requirements are proven. Accepted index removals and safe
sibling transitions preserve independently certified column facts, including
interrupted upgrade prefixes and replay; removed key/index or row proofs are
not restored. SQLite retains exact physical `INTEGER` matches without admitting
a store-type conversion. Runtime guards remain mandatory; unsupported
dependencies and unproven conversions do not become safe through this upgrade.
See the [MySQL/MariaDB](mysql-mariadb-ddl-behavior.md#lossless-signed-integer-alterations)
and [PostgreSQL](postgresql-behavior.md#lossless-signed-integer-alterations) boundaries.

The 10.6.0 generated-expression support conditionally changes approved
contract fingerprints: successfully parsed EF computed-column and CHECK SQL
now includes original source spelling and numeric-token provenance. Review
affected migration artifacts, regenerate stored
`SafeMigrationContractFingerprint` values and reapprove them before use.
Do not bypass mismatch checks or rewrite historical migrations. Ordinary
SQL-default parsing, opaque/unparsed SQL, explicit uncaptured definitions and
provider-owned operation fingerprint encoding are unchanged, as is the
relational model-fingerprint format. Parsed CHECK equality also retains exact
authored spelling; whitespace/parenthesis-only differences can be `Different`
without being `Unsupported`. Computed-column source was already exact and
does not gain automatic repair. See [expression compatibility](api-reference.md#sql-expressions-and-policy).

If an application stores no contract-fingerprint approval, this change does
not require inventing one or regenerating migration source. Existing approval
artifacts must be reviewed against the immutable migration with the selected
library version; do not reuse an older fingerprint as though it proved the new
contract. Valid native expressions can still be `Unsupported` when deterministic
binding, source independence or a native capability cannot be proved. A policy
change does not bypass that boundary. Review the intended dependency-removal
order, native permissions and affected provider guidance before execution.

Report schema 4 and view schema 3 are inherited from 10.5.0, not advanced again.
The complete hosted/native matrix and package/readback gates remain mandatory;
see the [publication runbook](operations/release-publication.md) for release
status and evidence requirements.

The earlier 10.x release-line preparation retained the same five packages and
signed-tag publisher. The current branch prepares the .NET 11 identity without
performing hosted cutover; see [release lines](operations/release-lines.md).

## Dependencies and package identity

All publishable projects target `net11.0`. The repository pins SDK 11.0.100
with roll-forward disabled. This source line supports EF Core 11; the separate
10.x line retains .NET/EF Core 10.

| Package | Runtime dependency contract |
| --- | --- |
| Core | `Microsoft.EntityFrameworkCore.Relational` `[11.0.0,11.1.0)` |
| MySQL/MariaDB | `Doka.EntityFrameworkCore.MySql` `[11.0.0,11.1.0)`; `MySqlConnector` minimum `2.6.2`, explicit `3.0.0` allowed |
| PostgreSQL | `Npgsql.EntityFrameworkCore.PostgreSQL` `[11.0.0,12.0.0)` |
| SQLite | EF Core SQLite Core `[11.0.0,11.1.0)`; application-owned native bundle |
| SQL Server (available since 10.4.6) | `Microsoft.EntityFrameworkCore.SqlServer` `[11.0.0,11.1.0)`; `Microsoft.Data.SqlClient` minimum `7.1.2`; `System.Security.Cryptography.Pkcs` minimum `11.0.0` |

Runtime transport/security dependencies remain owned by the selected adapter;
Core and sibling providers do not acquire SQL Server dependencies. The MySQL
range protects the public SPI boundary without an exact Doka pin; repository
locks identify the actual reviewed NuGet bytes, not the version selected by every
application. Qualified package consumers must use their own resolved graph.

### Historical 10.x dependency rationale

The following paragraphs preserve the 10.7.3/earlier dependency decisions and
their original source identities. They are not the current 11.x dependency table.

The EF Core and dependency-injection abstraction minimums are 10.0.15. These
are published package dependency floors, not CI-only updates; consumers on an
older 10.0 patch must update their resolved stack. EF Core SQL Server 10.0.15
retains its upstream SqlClient minimum of 6.1.7; SafeMigrations promotes it to
6.1.8 in the published SQL Server package. SNI remains 6.0.3. Its exact
development/testing license exception is documented in
[secure development](security/secure-development.md#sql-server-dependency-license-exceptions).
The target framework and independently bounded Npgsql range remain unchanged.
The separately reviewed Doka minor-line update is described below.

The SQL Server provider explicitly publishes PKCS minimum 10.0.15, so consumers
do not need the repository's central configuration or lockfile to inherit it.
The other four packages do not acquire that dependency. This retains the fixes for
the package versions identified by Microsoft's published
[PKCS decode advisory](https://github.com/dotnet/announcements/issues/454) and
[signature-check advisory](https://github.com/dotnet/announcements/issues/468),
verified on 2026-10-14. The original patched floor was 9.0.21, later serviced
to 9.0.22. EF Core SQL Server 10.0.15 now requires PKCS at least 10.0.13;
the current 10.0.15 floor satisfies that upstream requirement and uses its
latest .NET 10 servicing patch. Retaining a 9.x floor is no longer compatible
with this EF provider version. SqlClient remains 6.1.8.
The consumer owns runtime/SDK servicing and redeployment; a NuGet dependency
update does not update an installed or self-contained .NET runtime.
The minimum is not an exact version lock or a claim that every higher major
version is patched. The original advisories identify 10.0.13 as the first
fixed 10.x version; the current package contract requires 10.0.15 or later.
NuGet audit remains enabled for the actual resolved graph.

The 10.7.3 MySQL/MariaDB package requires Doka 10.7.0 or a compatible later 10.7 patch
release and rejects the next minor line. This boundary avoids an exact
transitive pin without claiming compatibility across an unqualified behavioral
SPI revision. CI does not build Doka and never uses a cross-repository
ProjectReference. The MySQL/MariaDB package lockfile resolves the public
Doka 10.7.0 package. Its NuGet repository metadata identifies source commit
`0bafbb1d3a0facd6ab67e26e98e83477f69ee1b7`. The complete package, engine,
tooling, coverage, and performance matrix remains the SafeMigrations release
gate; a local Doka ProjectReference or locally packed candidate is not release
evidence.

Doka 10.7.0 retains the EF Core 10 contract and raises System.IO.Hashing to
10.0.15. Its upstream MySqlConnector range is `[2.6.1,4.0.0)`; this repository
continues to resolve 2.6.2 by default and also allows explicitly selected
MySqlConnector 3.0.0. The published minimum is not an exact lock or a 2.x ceiling.
The connector 3 profile retains .NET/EF Core 10 and needs its own compatible
dependency graph; native qualification cannot be inferred from Doka alone.

Doka 10.6.1 introduced preservation of explicit GUID SQL defaults in its model and
migration metadata; an implicit client GUID strategy no longer overrides them.
Doka 10.6.2 additionally resolves SQL, computed and literal
defaults through actual property mappings, including GUID properties that
exist only in secondary Entity Splitting tables and shared secondary columns.
It retains the public migration SPI, dependency ranges and GUID storage defaults;
these metadata fixes do not require a schema migration.
Explicit application generation choices and historical snapshot annotations
remain authoritative. Review new scaffolded differences without rewriting
deployed history; see the [GUID SQL-default upgrade boundary](efcore-provider-upgrade-risk.md#guid-sql-defaults).

Doka 10.6.3 retains those metadata corrections and raises its published
MySqlConnector range to `[2.6.1,3.0.0)` for
[GHSA-473q-m89c-ghf8](https://github.com/advisories/GHSA-473q-m89c-ghf8).
Review direct consumer connector pins: older versions no longer satisfy the
provider contract. SafeMigrations additionally promotes the connector minimum
to an explicit published minimum of 2.6.2, so that floor reaches isolated
consumers without the repository's lockfile or central configuration.
Its System.IO.Hashing minimum is 10.0.14. The other four
SafeMigrations packages do not acquire Doka or MySqlConnector. No schema
migration, GUID storage change or historical migration rewrite is required.

SqlClient 6.1.8 corrects managed-SNI enforcement of an explicit
`HostNameInCertificate`; a certificate matching another name can now reject.
Review the [transport upgrade boundary](efcore-provider-upgrade-risk.md#sql-server-boundary)
before rollout. SafeMigrations does not enable certificate-validation or
revocation bypasses. That historical servicing change retained the exact SNI
6.0.3 and NativeInterop 0.20.6 exceptions. The current .NET 11 exception is
documented separately in [secure development](security/secure-development.md#sql-server-dependency-license-exceptions).

SafeMigrations 10.6.0 raises its Doka dependency floor to the 10.6 line.
Doka now budgets implicit indexed string/binary
lengths across every physical key part, overlapping indexes and compatible
principal-derived FK lengths. Review newly scaffolded column-length changes;
existing migrations and explicit application facets are not rewritten. Its
packed-DECIMAL correction agrees with SafeMigrations' existing projected and
native-catalog width formulas. EF and connector ranges remain unchanged;
Doka's System.IO.Hashing minimum advances from 10.0.12 to 10.0.13. See the
[versioned index contract](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/d1e40a7b211c10b354e5aa6df80d9fea710d8f58/docs/provider-configuration.md#index-key-byte-fidelity).
The full release matrix remains required; prior 10.5 qualification is not
automatically evidence for 10.6.

The earlier Doka 10.5 upgrade enabled explicit `MySqlGuidFormat.Uuid` on MariaDB, not an
automatic conversion of existing GUID columns. Doka requires MariaDB 10.7 or
later for this type; SafeMigrations qualification still uses the declared
engine matrix, not every server above that capability threshold. MySQL rejects
native UUID. Binary16 remains the default and Char36 remains supported.
Native UUID has 16-byte physical width and no text collation or index-prefix
contract. See [GUID storage and upgrade guidance](mysql-mariadb-ddl-behavior.md#guid-storage-and-native-uuid).

Doka 10.5.0 also maps implicit application-owned 16-byte binary properties to
`varbinary(17)`, independently of GUID defaults. This can create a model diff
and a newly scaffolded `AlterColumn`; review that authored migration and its
data contract. The dependency update does not rewrite existing migrations or
approve GUID representation changes.

Bounded package ranges describe external compatibility. All 24 repository
projects retain default source-mode locks for exact reviewed identities and
content hashes; they do not prescribe the graph selected by an application.
The four adapters additionally require their matching own-Core version because
they share a private ABI: 11.0.0-dev requires Core `[11.0.0-dev]`, while candidate
adapters require the exact corresponding Core candidate identity.
External dependency floors remain provider-owned;
the Doka update above advances its independently bounded range. Historical
releases keep their original package inventories.

The five-package set began with stable 10.4.6, including the optional SQL Server
provider. Version 10.7.3 retains that inventory and advances the SQL Server-only
PKCS minimum to 10.0.15. The Core API baseline includes the additive
CASE surface introduced in 10.6.0; the diagnostic surface began in 10.5.0.
Version 10.7.0 replaces ten optional-token declarations with required-token
declarations in the shipped baseline; it does not add an overload or restore
the removed defaults. Version 10.7.3 retains that shipped baseline exactly;
its ownership, cancellation and tooling corrections add no public declaration.
The historical Doka dependency contract in that release was the 10.7 line above;
the current 11.x dependency table supersedes it for this branch.
Qualification requires the blocking
workflow, including all three native x86-64 SQL Server cells. The
[publication runbook](operations/release-publication.md) owns the five-package
readback procedure and release evidence.

The 10.4.11 changes qualify sparse live/projected constraints and indexes from
actual native metadata, accepted physical lifetimes and current row proofs.
MySQL/MariaDB carries native owner equality through transition/backfill catalogs
and guards unsafe FK supporting indexes. Accepted PRIMARY/FK removal and ordinary
nonunique BTREE creation preserve independently proven intrinsic column sources,
not candidate-key or row authorization; execution retains fresh key/source guards.
PostgreSQL qualifies native CHECK and
physical index-input bindings before data probes, preserving bounded metadata
and SQL transport. SQLite retains declared parent-key equality, strict capacity
and appended-FK order; SQL Server uses compatible live endpoint/key evidence
under permission-first catalog and independent row checks. Complete source
qualification remains mandatory; local ARM SQL Server skips do not qualify its
native x86-64 cells or satisfy the full merged coverage gate.

The earlier 10.4.10 changes correct ordered nullability/backfill and sparse physical
constraint/index proofs, cross-operation rename evidence and canonical
model-managed-key identity. MySQL/MariaDB required additions use owned atomic
stages with forward recovery; PostgreSQL deletes fence dependent writers and
retain verified schema ownership; SQL Server cleanup requires owned session
acquisition. Earlier provider repair and trigger-freshness contracts remain.
These are bounded path-specific changes, not a general engine-latency or
peak-memory guarantee. SQLite retains its model-owned rebuild contract.
The historical engineering baseline used Testcontainers 4.16.0 and pinned
Action updates on `main`; the current dependency inventory is listed below.
For the historical 10.5.0 release only the MySQL/MariaDB package lockfile
changes to resolve Doka 10.5.0; the other four package lockfiles are unchanged.
No engine, tooling, package or coverage gate is removed. Performance measurements
remain informational; invalid evidence and functional failures remain blocking.
The analysis and runtime optimizations introduced in 10.4.7 remain in place;
see [MySQL and MariaDB behavior](mysql-mariadb-ddl-behavior.md) and
[D-014](decisions/D-014-mariadb-catalog-snapshot-for-batched-classification.md)
for catalog-copy ownership and fallback.

## Engine matrix

Every repository-defined GitHub-hosted Linux job is pinned to the explicit x64 `ubuntu-26.04`
label, including qualification, benchmarks, publication, dependency review,
maintenance and Scorecard. GitHub lists this image as generally available;
the moving `ubuntu-latest` label is not used. The complete hosted qualification
workflow must establish this candidate's actual engine, tooling, package and
SBOM evidence on the new image; changing a YAML label or running local ARM
tests does not establish that result. Existing SDK selection through
`global.json`, engine image digests and timeouts were unchanged by the runner
update. BenchmarkDotNet subsequently replaced the handwritten benchmark
budgets as described below. The repository maintains no permanent second Ubuntu matrix.
GitHub-managed default-setup CodeQL and automatic dependency-submission runs
are separate platform workflows; these repository labels do not configure them.

The reusable workflow `.github/workflows/quality-gates.yml` is invoked by CI,
release candidates, and stable releases. Its image source is the canonical
[data-only manifest](../docker/database-images.Dockerfile); fixtures and benchmark
assemblies consume that same file rather than maintaining another pin inventory.
The manifest declares targets `mysql84`, `mysql97`, `mariadb1011`, `mariadb114`,
`mariadb118`, `mariadb123`, `postgres14` through `postgres18`, and
`sqlserver2019`, `sqlserver2022`, `sqlserver2025`. SQLite uses the application-owned
native bundle and retains its engine floor.

The image patches and manifest digests were refreshed against the official
[MySQL](https://hub.docker.com/_/mysql),
[MariaDB](https://hub.docker.com/_/mariadb),
[PostgreSQL](https://hub.docker.com/_/postgres) and
[Microsoft Container Registry](https://mcr.microsoft.com/v2/mssql/server/tags/list)
registries on 2026-11-20. MySQL retains 8.4.13 and 9.7.4. PostgreSQL advances
to 14.25, 15.20, 16.16, 17.12 and 18.7. The four existing MariaDB tags and
three SQL Server lines receive newer manifest digests without changing series.
These declarations identify the configured matrix, not
completed qualification of every image; the required hosted matrix must pass.

MariaDB server patches 10.11.20, 11.4.14, 11.8.10 and 12.3.4 were released
upstream, but their official Docker tags were unavailable during that check.
Keep the available 10.11.19, 11.4.13, 11.8.9 and 12.3.3 images until exact
replacement tags and digests can be verified. No rolling database line is added.
PostgreSQL 14 reached upstream end of life; its existing qualification cell is
retained pending a separate support-policy decision, not represented as supported
by the vendor. No PostgreSQL 19 cell is introduced by this refresh.

That historical 10.7.3 inventory selects SDK 10.0.404, EF Core/DI/dotnet-ef 10.0.15,
PKCS 10.0.15, Testcontainers 4.17.0, Microsoft.CodeAnalysis.Analyzers 5.12.0
and Microsoft.NET.Test.Sdk 18.12.0. The SqlClient minimum remains 6.1.8;
raising it to 7.x requires
a separate [authentication migration](https://learn.microsoft.com/en-us/sql/connect/ado-net/microsoft-data-sqlclient-release-notes).
The Action refresh selects Dependency Review 5.0.1 and Scorecard 2.6.0;
the other Action pins, SBOM Tool 4.1.5 and Ryuk 0.14.0 remain current.
This refresh does not activate .NET 11 or change engine series. PKCS advances
to the compatible .NET 10 line because of the EF provider's explicit requirement,
not as an unrelated transitive-major override.

Local defaults select MariaDB 12.3.3, PostgreSQL 18.7 and SQL Server 2022 from
the manifest. Existing `SAFE_MIGRATIONS_MYSQL_ENGINE`, `SAFE_MIGRATIONS_MYSQL_VERSION`,
`SAFE_MIGRATIONS_MYSQL_IMAGE`, `SAFE_MIGRATIONS_POSTGRES_VERSION`,
`SAFE_MIGRATIONS_POSTGRES_IMAGE` and `SAFE_MIGRATIONS_SQLSERVER_IMAGE` inputs remain
supported. Explicit image overrides take precedence; deliberately unlisted
version inputs retain their explicit tag path. Consolidation introduces no forced
server charset/collation or owner-name mode hiding a native default.
Every other declared server line
still has its independent matrix cell. This repository default does not change
the provider version selected by a consuming application.

MySQL/MariaDB support follows Doka's canonical feature profiles. PostgreSQL
qualification spans major versions 14 through 18; the 14 cell is retained
compatibility coverage after upstream EOL. Every declared major has an
independent release-gate cell. SQLite xUnit qualification uses isolated
in-process databases, including private-cache file-backed concurrent
`MigrateAsync` callers that exercise EF's migration lock. The EF tooling gate
applies Strict and legacy migrations and bundles twice to asserted file
databases from the centrally declared dependency graph. A new endpoint or
removed upstream version requires a reviewed support-contract change and fresh
evidence.

The 2026-08-27 source review established the declared PostgreSQL 14 through 18
support matrix; the current patch/digest refresh is dated above. PostgreSQL 14
reaches upstream end of life on 2026-11-12. Release
qualification performed after that boundary must re-evaluate the declared
matrix instead of inheriting this dated support conclusion.

The table records the matrix configured in source. Only a successful workflow
run for the exact commit/package version establishes executed qualification;
the presence of a tag or image digest in this document does not.
SQL Server's three independent cells use the Developer edition for tests,
not a hosted service. Microsoft supports its Linux containers only on x86-64;
Apple Silicon emulation is not a valid qualification substitute. The matrix
does not claim Azure SQL Database, Managed Instance, or Synapse compatibility.
See [Microsoft's container guide](https://learn.microsoft.com/en-us/sql/linux/install-upgrade/quickstart-install-docker?view=sql-server-ver17)
and [SQL Server behavior](sqlserver-behavior.md).

## Dependency qualification

Central declarations define reviewed external ranges. All 24 source-mode projects
commit default-profile lockfiles, retaining resolved identities and package hashes
for shipping libraries and execution projects alike. CI identity is established
before evaluating root locked mode. Platform-neutral CI source restores must be
locked; local dependency changes update every affected lock for review.

RID-specific bundle publishes, explicit compatibility-matrix graphs and isolated
package-consumer modes retain their independently qualified restore behavior.
They do not rewrite default locks or count as validation of a different profile.
[D-006](decisions/D-006-package-qualification-and-release.md) preserves the older
smaller lock scope as history and records its replacement, not a continuing waiver.

Shipping projects disable implicit transitive promotion and declare the existing
security floors explicitly in their owning providers. Nonshipping projects keep
transitive pinning. The four providers publish an exact matching-Core dependency,
including candidate identities, because their internals are version-coupled.
Nuspec, isolated-consumer and SBOM controls must prove both matching and
conflicting selections; Core and unrelated adapters must not acquire provider-only
drivers or cryptography dependencies.

Dependabot proposes dependency and lockfile updates through ordinary pull
requests. Every accepted update therefore runs the same complete CI workflow as
product code, including all provider cells, package consumers, coverage,
performance, and SBOM validation. Dependency Review compares explicitly submitted
base/head snapshots bound to the current pull-request commit. Contributor code
runs only in separate read-only, cache-denied PR jobs; the trusted receiver
validates their exact source-run/attempt and immutable data artifacts before
submitting snapshots and publishing the required check. Neither its automatic
nor manual entry executes contributor restore hooks. Forks
and Dependabot use the same validation path. Missing, stale, or incomplete
evidence fails closed; automatic GitHub submissions are not the authority for
this check. The canonical [secure-development guide](security/secure-development.md)
defines the trust boundaries, bootstrap path, license/vulnerability policy,
bounded retry, and required comparison-header validation.

## Behavioral evidence

The executable test inventory consists of five independent xUnit assemblies
and focused engineering checks:

- [provider-neutral Core tests](../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests);
- [MySQL/MariaDB tests](../tests/Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests);
- [PostgreSQL tests](../tests/Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests);
- [SQLite tests](../tests/Doka.EntityFrameworkCore.SafeMigrations.Sqlite.Tests);
- [SQL Server tests](../tests/Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests);
- FsCheck properties across the Core and server-provider assemblies for generated Core-contract,
  identifier-rendering, catalog-normalization, and provider-boundary inputs;
- [coverage verifier tests](../eng/tests/test_verify_coverage.py);
- package-content, package-only consumer, SBOM, EF tooling, and public NuGet
  readback scripts exercised by the reusable quality and release workflows.

Test results from the qualified commit are the authority for current case
counts. Counts are not duplicated here because parameterized cases and the
engineering suites evolve independently of the support contract.

Server-provider tests use real Docker servers. SQLite xUnit tests use isolated
in-process databases; the SQLite EF tooling gate owns asserted file-database
coverage. The provider-specific suites collectively cover:

- all 23 operation kinds;
- all 22 supported non-null CLR literal families plus literal `NULL` across the
  server providers, with provider-specific convergence or pre-DDL fail-closed
  classification;
- missing, matching, transition-ready, different, unsupported, data-blocked,
  and prerequisite-missing states;
- `ExistenceOnly`, `ThrowIfDifferent`, and `RepairIfSafe`;
- source-frozen legacy convergence policy selection, mutable column repair,
  matching rerun, null-data blocking, invariant-drift rejection, and
  MySQL/MariaDB preservation of unmodeled `EXTRA` modifiers;
- provider-proven ordinary `VARCHAR` widening, grouped and deduplicated
  live-data narrowing proofs, execution-time race rejection, complete
  postflight, idempotent replay, operational-impact evidence, and exact
  MySQL/MariaDB `BIT(1) -> TINYINT(1)` Boolean repair;
- typed Doka column-metadata acceptance and rejection, plus exact repair of
  historical CLR defaults without retaining a permanent model default;
- granular heterogeneous table convergence and pairwise legacy-state
  generation with a fixed seed;
- exact expected column, index, primary-key, unique, check, and foreign-key
  facets, including one-field drift matrices and strict-table embedding;
- native MySQL JSON and Doka's MariaDB `longtext`/`utf8mb4_bin`/`JSON_VALID`
  representation, including strict postconditions, inventory ownership,
  invalid-value enforcement, and independent user-check drift;
- bounded EF-generated SQL defaults such as `CURRENT_TIMESTAMP(6)`, plus
  fail-closed retention of unbounded default SQL;
- bounded generated-column and CHECK expressions, including ordered searched
  `CASE` and overflow-guarded signed-integer branches; see the provider-specific
  type, storage and non-NULL proofs in [generated columns](migration-authoring.md#generated-columns);
- provider-specific index capabilities, generated MySQL/MariaDB prefix
  roundtrips, physical InnoDB key-limit boundaries, and unsupported branches;
- ordered ordinary and safe index drop/create replacement, including preserved
  duplicate-data, prerequisite, physical-key, and semantic-conflict rejection;
- InnoDB foreign-key support indexes as semantic matches for equivalent index
  ensures, without weakening exact-name or physical-facet drift;
- differently named equivalent primary-key, foreign-key, unique-constraint,
  check-constraint, and index no-ops, including composite definitions and
  multiple physical aliases, plus exact-name precedence and different-shape
  creation;
- MySQL check enforcement, MySQL index visibility, MariaDB ignored indexes and
  table-scoped check names, plus PostgreSQL index health, partition ownership,
  constraint backing, and partitioned-parent indexes;
- quotes, backslashes, mixed case, Unicode, and maximum-length identifiers;
- PostgreSQL non-default schemas, cross-schema foreign keys, and
  same-named-object isolation;
- fail-closed schema qualification across every MySQL/MariaDB operation family;
- server-provider connection-disposal guard recovery, partial-command retry,
  least privilege, and provider migration locks;
- four concurrent migrators on one server database and parallel independent
  server databases, plus two concurrent SQLite migrators against an asserted
  private-cache file database;
- normal EF operations mixed with safe operations;
- safe table, source-frozen model-managed ensure/update/delete, raw typed
  seed/update/delete-data, and following non-unique-index ordering, plus
  fail-closed unique-index projection after unanalyzed data;
- existing-table legacy projection for primary, unique, check, foreign-key, and
  index prerequisites, including empty/null-preserving row proofs, composite-
  key order, incompatible storage, and proof invalidation after provider data;
- accepted-index invalidation after ordered drops and column mutations, plus
  MySQL/MariaDB physical revalidation for single, composite, unique, primary,
  over-limit, 16-part, missing-table, replacement, row-format, and page-size
  key shapes;
- MySQL/MariaDB cross-kind unique-constraint and unique-index identity, including
  both drop/recreate directions, alias invalidation, exact-name conflicts,
  `PRIMARY` separation, and duplicate-row controls;
- maximum-length 16-column primary and unique keys matched by ordinal catalog
  rows independently of `group_concat_max_len`, with reversed-order controls;
- MySQL/MariaDB composite-index candidates whose generated catalog-reference
  count remains constant as the number of key parts grows, with functional
  classification on both engines;
- physical-identity binding for semantic unique, check, foreign-key, and index
  aliases, including drop/rename invalidation and ambiguous fail-closed
  prerequisite projection;
- exact and semantic-alias `Drop -> Ensure` replacement for primary keys,
  unique constraints, checks, foreign keys, and indexes, including live
  execution/postflight and negative intervening-DML controls on both server
  provider families;
- model-managed single/composite keys, mixed absent/source/target batches,
  unique/check conflicts, source drift, retry, idempotent replay, trigger
  postconditions, cancellation, and compare-and-swap races;
- Strict and legacy initial migrations generated through the real provider
  design-time differ, compiled, preflighted against an empty database, executed
  twice as the identical operation stream, and verified through postflight;
- independent application and derived extended migration projects, snapshots,
  assemblies, and history tables, including exact excluded-table
  model-managed-data ownership and retained shared-to-extended relationships;
- incoming model-managed delete dependencies for every supported referential
  action, including accepted ordered child/parent removal and rejection of one
  remaining or concurrently inserted dependent row;
- EF history success/failure and derived-context model-snapshot guards;
- read-only preflight, unexpected-object inventory, positive and negative
  postflight, and cancellation before and during catalog access;
- PostgreSQL-owned and caller-owned analysis transactions, including accepted
  read-only `RepeatableRead`/`Serializable` scopes and fail-closed rejection of
  read-write or weaker-isolation caller transactions.
- SQLite `main` identity, attached-database rejection, catalog normalization,
  generated columns, semantic aliases, atomic provider-owned rebuilds, and
  fail-closed unmanaged triggers, views, virtual tables, expression or partial
  indexes, `STRICT`, and `WITHOUT ROWID` boundaries.
- SQLite runtime and bundle application with identical replay, plus fail-closed
  safe-operation script generation that leaves no partial output.

The provider-analyzer contract accepts the ordered safe-operation batch. Each
provider first classifies table and referenced-column prerequisites, then
executes classification in parameterized ADO.NET batches. Optimizer-visible
statements, statements per transport batch, parameters, and UTF-8 payload are
bounded independently. MySQL/MariaDB provider plans are captured in bounded
windows. The unexpected-object inventory remains scoped to the expected table
set for child objects while retaining complete table discovery and provider-
verified semantic-alias reconciliation. Projection applies global ordered
results without per-operation catalog roundtrips, and no partial report is
published after a later failure. Every server-provider engine profile also
qualifies 100,000 deterministically ordered mixed operations. That workload covers `Missing`,
`Matching`, `Different`, `Unsupported`, `DataBlocked`, and
`PrerequisiteMissing`, `TransitionReady`, all seven planned actions, and
heterogeneous table, column, index, primary-key, unique, check, foreign-key, and
model-managed-data operations. Native
`DbBatch` execution and the sequential fallback for connections with
`CanCreateBatch == false` share the same bounded command adapter; live fallback
tests verify command order, timeout propagation, and cancellation. Coverage
jobs exclude the large-scale test because every supported engine matrix cell
already executes it against a live server.

The MySQL, MariaDB and PostgreSQL tooling gates execute freshly scaffolded
Strict and LegacyConvergence contracts through real `dotnet ef database update`,
Migration Bundles and native SQL clients. Normal scripts execute once;
idempotent scripts, including the no-transactions variant, execute twice.
The existing small mixed-operation fixture and derived ownership script also
execute. Generation or a nonempty artifact alone is not execution evidence.

Each transport uses a fresh owned database lifetime and independently verifies
physical schema, exact history, model-managed data, generated CASE results,
the computed-column index and CHECK enforcement. MySQL's authored default
schema is a database qualifier: fresh lifetimes retain that database identity
instead of rewriting SQL. PostgreSQL uses `psql -X` with `ON_ERROR_STOP=1`;
MySQL/MariaDB use explicit UTF-8 batch input without `--force`. Clients retain
the script's own transaction boundaries.

All generated migration source is compiled before bundle publication. Bundle
commands use `--no-build`: an earlier RID publish can change copied lockfiles,
which cannot validate a later automatic non-RID restore in CI. Initial locked
restore/build gates and original source-lock hashes remain enforced. Local
tooling parity checks must also exercise the CI restore policy, for example
with `GITHUB_ACTIONS=true` scoped to the harness command.

SQL Server qualifies Strict/LegacyConvergence CLI, bundle, normal-script and
idempotent-script targets with exact native schema/history and generated-value
readback. ODBC callers explicitly enable quoted identifiers and use finite
login/query budgets; these caller settings do not relax product guards.

SQLite runs both database-update and bundle paths twice. Its readback checks
both initial and transition history identities, final column/index shape,
generated behavior, CHECK enforcement and integrity independently. Safe-operation
SQL scripts remain a qualified negative boundary: normal/idempotent attempts
must fail for the intended provider reason without partial output, not merely
with an arbitrary CLI error. Existing xUnit tests cover incomplete and
compensating database fixtures against the actual consumer verifier.

Tooling cleanup attempts all owned resources. Cleanup-only failures reject the
gate; cleanup cannot replace an earlier execution failure. Shell counterprobes
and the existing SQL Server process tests verify these orchestration contracts;
they do not replace the required native engine matrix.

## Qualified capability boundaries

The common intent model remains provider neutral, while execution follows the
active engine's proven metadata and DDL capabilities. The following boundaries
distinguish qualified supported behavior from explicit rejections. Unsupported
cases stop before target DDL instead of being compared or applied heuristically:

- PostgreSQL 14 rejects `NULLS NOT DISTINCT`; PostgreSQL introduced that
  `CREATE INDEX` clause in version 15.
- Doka 10.3.0 parenthesizes `DateOnly` and `TimeOnly` typed literals in column
  defaults. The complete MySQL and MariaDB matrix qualifies the resulting DDL
  and each engine's catalog display form.
- Doka 10.3.0 emits `ClientGuid` for client-generated Guid keys, including
  application-converted relationship chains. SafeMigrations retains that
  annotation in operation identity and provider replay while comparing its
  live column as non-`AUTO_INCREMENT`. HiLo and unknown column annotations
  remain unsupported because their complete database prerequisites are not
  represented by the column catalog contract.
- Doka 10.3.0 supplies an omitted `AllowUserVariables=true` option only for
  provider-owned strings when SafeMigrations declares the capability.
  Contradictory owned values and incompatible borrowed connections or data
  sources fail closed. The provider also enforces `UseAffectedRows=false` and
  connector transport `GuidFormat=Binary16`; model-level Guid storage remains
  independently configurable as `binary(16)` or `char(36)`.
- Doka 10.3.0 exposes Guid, value-generation, and index-prefix migration
  metadata through a typed public snapshot. SafeMigrations rejects any extra,
  malformed, contradictory, or unmodeled annotation instead of inferring its
  semantics from a string key.
- Missing MySQL/MariaDB BTREE indexes are checked against the live InnoDB row
  format, page size, column encodings, store families, and explicit prefixes.
  SafeMigrations rejects an unachievable or unprovable key before provider DDL
  and never relies on server-side non-strict prefix truncation.
- Existing MySQL checks must be enforced. MySQL invisible and MariaDB ignored
  indexes do not satisfy the visible index shape emitted by ordinary EF
  operations. MariaDB check clauses are correlated by table and constraint
  name so the table-scoped names available from MariaDB 12.1 remain isolated.
- MySQL schema-wide CHECK and foreign-key symbol collisions reject before DDL.
  MariaDB foreign-key symbols reject database-wide collisions through 11.x;
  MariaDB 12.1 and later retain their documented table-scoped behavior.
- PostgreSQL rejects a non-equivalent second primary key and any schema relation
  name that prevents index creation, index rename, or creation of a primary-key
  or unique-constraint backing index. These paths produce the controlled
  different-object guard instead of a raw duplicate-relation failure.
- MariaDB's Doka-emitted JSON alias is the physical triple `longtext`,
  `utf8mb4_bin`, and the exact inline `JSON_VALID` check. Only that
  provider-generated check is excluded from strict child-object ownership.
- EF-generated SQL defaults receive typed equivalence only when the bounded
  parser proves their structure. Arbitrary SQL strings remain unsupported.
- The same structural boundary applies to EF computed-column and CHECK
  metadata. A parsed expression alone does not establish native type binding,
  required-result semantics or index eligibility; each provider must prove
  those independently. SQL Server native qualification requires the x64 matrix.
- Existing PostgreSQL indexes must be valid, ready, live, and independently
  owned. Partitioned parent indexes are supported; attached child indexes and
  constraint-owned backing indexes reject independent ensure, drop, or rename
  operations.
- MySQL, MariaDB 10.11, and MariaDB 11.4 reject a `Guid` literal default stored
  as `BINARY` because `INFORMATION_SCHEMA.COLUMNS.COLUMN_DEFAULT` does not
  preserve a complete value for repeatable semantic comparison. MariaDB 11.8
  and 12.3 preserve the complete expression and are qualified for missing,
  matching, different, retry, preflight, and EF-pipeline behavior.

These are complete fail-closed outcomes, not silent degradation. A provider or
server update may remove a boundary only after the same missing, matching,
different, retry, preflight, and EF-pipeline evidence passes for the changed
capability.

## Coverage gate

The release workflow runs all five test assemblies. Server adapters use pinned
MariaDB 11.8, PostgreSQL 18, and SQL Server 2022 images; SQLite uses its locked
in-process runtime with Microsoft's built-in code-coverage
collector. `eng/verify-coverage.py` conservatively merges Cobertura line and
branch evidence by product source line and excludes test and third-party
assemblies by exact package name.

`eng/coverage-thresholds.json` is a blocking floor, not a quality target:

| Product assembly | Line floor | Branch floor |
| --- | ---: | ---: |
| Core | 92% | 80% |
| MySQL/MariaDB adapter | 92% | 75% |
| PostgreSQL adapter | 94% | 84% |
| SQLite adapter | 90% | 75% |
| SQL Server adapter | 90% | 75% |

The behavioral and engine matrices remain mandatory even when the numeric
floor passes. A threshold reduction requires reviewed evidence and must not be
used to hide an uncovered regression.

## Performance and memory

Core, MySQL/MariaDB, PostgreSQL, SQLite and SQL Server each have an isolated
BenchmarkDotNet 0.16.0-preview.2 executable. The default workload families include all
registered parameterized cases, including operation-count parameters where applicable.
BenchmarkDotNet owns process isolation, warmup, timing, statistics, managed
allocation diagnostics and native full JSON export. The former custom timer,
median sampler, budget configuration and result schema are retired.

BenchmarkDotNet requires explicit execution, including `Dry`. Normal build/test
and cleanup paths and default CI do not start measurement. Dispatch with
`capture-benchmark-evidence: true` to request all five Dry suites, or request the
separate SQL Server Short measurement through `capture-sqlserver-baseline`.
Requested results are retained under `artifacts/performance` with native full JSON.
A Dry run checks execution/export, not statistically meaningful performance.

Deterministic workload, result, configuration and export-shape tests remain normal
blocking tests. Requested measurement rejects invalid configuration, generated
build/execution errors, missing/failed selected cases and invalid output. Numeric
duration/allocation values are informational. Native engine, tooling, coverage,
package and SBOM requirements remain blocking whether measurement is requested or
not; opting out does not create missing required benchmark evidence.

The entry point rejects `--resume` so that every selected case supplies current
execution evidence. It also rejects `--logBuildOutput` and `--generateBinLog`:
generated builds inherit environment variables, and build diagnostics can
expose native database credentials. These evidence/privacy restrictions do
not impose performance thresholds.

Native `--envVars` is supported except for the owned names
`SAFE_MIGRATIONS_ATTRIBUTION_OUTPUT`,
`SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_OUTPUT` and
`SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_CONNECTION`. Use shell environment
variables for these settings: a child-job override would bypass output-path
normalization or expose a native connection string in reported job parameters.

For measurement runs, use the [contributor commands](../CONTRIBUTING.md#required-engineering-gates)
without `--job Dry`, or choose `--job Short` for a shorter exploratory run.
Keep the native logs, summaries and `results/*-report-full.json` files together
with the source revision, runtime, OS, architecture and job configuration.
The [full exporter][bdn-json]
retains raw measurements rather than only a repository-derived median.

The historical .NET 10/BenchmarkDotNet 0.15.8 investigation showed that its
[allocation counter][bdn-gc]
used process-wide managed allocated bytes, including work on other managed
threads. This is not the old handwritten thread-local counter. Historical
allocation and timing numbers are not methodologically comparable with the
current .NET 11/BenchmarkDotNet toolchain, even when a workload name is unchanged.
That historical implementation citation does not establish every current
measurement detail. Managed bytes allocated
are neither peak live managed memory nor native process/database memory.

### Allocation-counter limitations

Runtime allocation counters are not an exact object census in every execution
window. In .NET 10, the [thread-local implementation][runtime-thread-allocation]
subtracts the unused portion of the current allocation context from its charged
bytes. The [background-GC accounting defect][runtime-bgc-accounting] can retire
that context without retiring its unused-byte charge. A counter delta can then
increase without a corresponding managed object. The related
[runtime correction][runtime-bgc-correction] is a draft, not a released fix, as
checked on 2026-11-11. The process-total counter also depends on allocation-context
accounting; switching counters alone does not establish exact object attribution.

For an unexplained delta, retain the source revision, exact runtime, collector
configuration and measured-thread boundaries. Distinguish sampled allocation
events from complete object events; check event loss, object-size alignment and
known positive/empty controls before attributing bytes to a call stack. A new
trace or a matching upstream mechanism does not retroactively identify an old
untraced CI outlier. BenchmarkDotNet remains the informational measurement
mechanism, not an exemption from these evidence limits.

Do not disable background GC, introduce a no-GC region, subtract an estimated
offset or add retry-until-pass behavior in functional qualification. Keep result,
ownership, SQL and recovery assertions independent of observed heap-byte totals.

[runtime-thread-allocation]: https://github.com/dotnet/runtime/blob/v10.0.0/src/coreclr/vm/comutilnative.cpp#L851-L865
[runtime-bgc-accounting]: https://github.com/dotnet/runtime/issues/134724
[runtime-bgc-correction]: https://github.com/dotnet/runtime/pull/134855

### Provider attribution boundaries

PostgreSQL eligibility attribution records exact SQL, transport and replay
evidence during setup. Its measured deterministic transport disables diagnostic
retention and disposal counters; no observation cleanup is included in each
invocation. Result construction and real resource cleanup remain active. These
client-side measurements are not native server timings.

[bdn-json]: https://github.com/dotnet/BenchmarkDotNet/blob/v0.16.0-preview.2/src/BenchmarkDotNet/Exporters/Json/JsonExporter.cs
[bdn-gc]: https://github.com/dotnet/BenchmarkDotNet/blob/v0.15.8/src/BenchmarkDotNet/Engines/GcStats.cs#L126-L146

Prepared-input BenchmarkDotNet workloads measure shared seed and diagnostic
evidence parsing, accepted-column projection, no-FK dependency and certificate
reuse, MySQL/MariaDB runtime generation, PostgreSQL table-definition composition
and SQL Server catalog-string assembly allocations. Numeric allocation samples
are informational, including absolute amounts, relative comparisons and
zero-byte observations; none determines functional-test or coverage success.
Functional regressions separately require exact results and reference ownership.
Runtime-generation SQL
fingerprints retain exact MySQL/MariaDB guards, statement order and cleanup;
PostgreSQL text and SQLite catalog regressions cover whitespace, comments,
quoted tokens and ordered keys. They exclude input construction and asynchronous
database latency. Parsed evidence owns its final arrays and field strings;
caller-owned evidence still receives defensive snapshots. These checks do not
measure disk flush latency, establish an end-to-end migration latency claim,
or replace live migration, recovery and native SQL Server qualification.

For source-labelled raw samples and exact output/transport counterprobes, use the
provider projects' [maintenance attribution mode](../CONTRIBUTING.md#maintenance-attribution).
These prepared client measurements do not establish native engine performance.
Attribution output environment paths are made absolute against the caller's
working directory before child launch, then restored in the caller after the
run. Relative paths therefore do not silently target disposable generated-build
directories whose cleanup would remove the structural evidence.

The SQL Server set retains eleven generation, analyzer-plan, model-managed
data, repair, and canonical-model cases. For historical provenance only, the
retired handwritten engine's initial duration values were the exact medians
from the five-sample macOS ARM64 developer capture at
`2026-09-30T18:07:25.269536+00:00`, running .NET `10.0.12` on `Unix 26.5.2`.
The evidence file is `artifacts/performance/sqlserver-baseline-macos.json`,
SHA-256 `6803e8aa94a3b512b80f2f66d6de700f813a08d2467db0732dadcf7ff5c1ddde`.
Its historical duration comparisons used a 200% regression tolerance.
Allocation caps were the captured median plus 25%, rounded upward to the next
65,536-byte block; the block minimum gave small cases more than 25% headroom.
These retired values are not BenchmarkDotNet baselines or current thresholds.

This historical capture is measurement-only, not GitHub-hosted x64
qualification. Current optional hosted capture uses the native BenchmarkDotNet
Short job described below, not the old median engine. SQL Server qualification
still requires the complete pinned x64 workflow.
SQL Server live workloads append timestamped stage markers
under `artifacts/performance/live/sqlserver-*-progress.log` before potentially
long-running phases. The existing always-upload artifact preserves these markers
even when a job does not produce its final TRX. They contain fixed workload/stage
identifiers, not SQL, database names or connection strings; they are diagnostics,
not successful qualification evidence.

The existing diagnostics ActivitySource optionally emits
`safe_migrations.analysis.stage` activities for provider baseline capture,
classification, bounded catalog batches, ordered projection and unexpected
inventory. Only fixed stage labels, operation counts, batch/parameter counts
and payload sizes are tagged, never SQL, object names or values. With no
listeners no stage activity is allocated. SQL Server stress workloads retain
start/completion markers and elapsed milliseconds for each stage; independent
workloads are isolated by trace identity. Missing completion evidence still
means the phase did not finish, not that qualification passed. Actual SQL
Server 2019/2022/2025 x86-64 durations must be established by the pinned live
cells; local ARM source tests cannot establish timeout closure.

The five independently restored benchmark projects
enforce the Core and provider dependency boundaries for:

- intent construction;
- decision planning;
- MySQL handler/generator output;
- PostgreSQL adapter output;
- SQL Server adapter output;
- canonical snapshot initialization, relational model differ, and fingerprint;
- report JSON serialization;
- blocker-view serialization across 50,000 assessments with a small selected
  result and no filtered collection;
- model-managed intent construction and contract fingerprinting at 384 row
  transitions;
- provider model-managed command generation and analyzer-plan construction at
  384 row transitions.

The SQL Server repair cases generate and build analyzer plans for 1,000 actual
`RepairIfSafe` alterations from nullable `nvarchar(64)`/maximum length 64 to
nullable `nvarchar(128)`/maximum length 128. Source and target definitions have
no defaults, comments, or provider annotations. This supported text-widening
workload does not substitute ordinary generation or an unsupported-operation
classification for the repair path; startup rejects a plan that is unsupported
or lacks safe repair capability.

Benchmark duration and allocation values are informational, not CI gates.
Wall-clock measurements on shared GitHub-hosted runners are not deterministic.
Compare complete measurement runs with the same source-controlled workload,
BenchmarkDotNet job and runtime on a comparable environment; `Dry` results
must not be used as throughput or regression proof. Review scaling and
allocation behavior separately from numeric timing, and retain raw evidence.
Earlier host measurements keep their actual OS and architecture attribution;
do not relabel an Ubuntu 24.04 result as a 26.04 baseline or compare the hosts
as though they were the same environment.

The default generation and analyzer-plan workloads do not open a server
connection, so they can also run locally on ARM64 for diagnostic evidence.
Their output records its actual environment and is not x64 Linux qualification
evidence. For an exploratory native BenchmarkDotNet report, run:

```sh
dotnet run --project benchmarks/Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Benchmarks \
  --configuration Release -- \
  --filter '*' --job Short --artifacts artifacts/performance/sqlserver-baseline
```

For capture on the pinned x64 Linux runner, dispatch the existing CI workflow
with the compatibility input `capture-sqlserver-baseline` enabled. It runs the
same command with `--no-build --no-restore` after the matching Release build
and retains `artifacts/performance/sqlserver-baseline` as an artifact directory.
All blocking qualification gates and ordinary deterministic workload/configuration
checks still run. Benchmark execution, including Dry smoke, requires an explicit
request. The optional report uses the same native full JSON format and failure
policy; it has no separate sampler, budget evaluation or custom median schema.

The MySQL/MariaDB benchmark has no Npgsql or SQLite dependency, the PostgreSQL
benchmark has no Doka MySQL or SQLite dependency, and the SQLite benchmark has
no server-provider dependency. Shared measurement and workload source is linked
at compile time; no benchmark assembly introduces a cross-provider runtime edge.

Every server-provider engine cell additionally verifies one complete pooled
`SafeMigrationRunner` invocation against 100 expected tables and another after
adding 1,000 foreign tables with child objects. Assessments must retain exact
identity/cardinality and foreign child rows must remain excluded by the expected
table scope. Statistical measurement of this same native workload now belongs to
the explicit BenchmarkDotNet `--live-catalog` mode, not ordinary tests.
The old 20-call percentile JSON and `2 * clean p95 + 250 ms` formula are retired.
Native PostgreSQL column attribution and MySQL migration/replay have separate
explicit modes; see [native measurement commands](../CONTRIBUTING.md#native-catalog-and-migration-measurements).
Setup/reset and functional validation remain outside timing. Each measured
invocation can include multiple roundtrips. Full JSON is diagnostic evidence,
not an absolute cross-machine latency promise or hardware-sensitive gate.

Every server-provider engine cell also analyzes 100,000 ordered mixed operations whose
observations cover every state and planner action, including model-managed
ensure, update, and delete. The analysis workload also includes accepted
`VARCHAR` widening, accepted narrowing, data-blocked narrowing, and the
MySQL/MariaDB Boolean repair; it is not a matching/no-op-only benchmark.
Separate live execution tests apply and replay at least 50,000 mixed absent,
source, and already-target row transitions using the production
128-row/4,096-cell partitioning. Their JSON evidence records elapsed time,
managed allocations, command count, maximum batch rows/cells, and maximum
generated command bytes. Semantic results, command counts and transport limits
remain blocking contracts. Recorded duration and managed-allocation values are
informational, not cross-machine or instrumented-run pass/fail budgets.

SQLite has engine-appropriate gates instead of inheriting server-transport
claims. Its live suite applies and replays 50,000 mixed model-managed row
transitions. Its allocation and duration evidence analyzes the same 384-row
operation set against both a 200-table catalog and a 1,000-table catalog whose
foreign tables carry indexes and foreign keys. The latter intentionally
measures whole-catalog discovery cost and prevents that cost from remaining
outside the retained release evidence. Numeric measurements are informational;
state, scope, recovery and replay correctness remain blocking.

After restoring the locked publishable package graphs, the quality workflow
rejects warning-level Roslyn style violations and unnecessary imports.
Rider/ReSharper remains the repository
formatter for layout rules that Roslyn cannot represent.

## Package and supply-chain evidence

`eng/qualify-packages.sh`:

1. packs the same Release build twice;
2. compares all five `.nupkg` and five `.snupkg` files byte-for-byte;
3. verifies the exact file set, metadata, dependency shape, assemblies, XML,
   symbols, README, license, and report schemas;
4. builds and runs an isolated consumer using packages only;
5. emits sorted SHA-256 checksums.

The four provider package-consumer fixtures, plus the split MySQL package
topology, are normal Solution projects in local `Source` mode so Rider loads
their complete C# and MSBuild models. That mode
uses the matching provider `ProjectReference` and participates in locked
Solution restore, formatting, and build gates. Package qualification copies
the fixtures into a temporary root and explicitly selects `Package` mode. That
mode removes every `ProjectReference`, consumes the generated packages, and
fails if the restored asset graph contains a project dependency. The local IDE
path therefore remains maintainable without weakening package-boundary
evidence.

The Microsoft SBOM Tool binary is downloaded at version 4.1.5 and verified
against the platform-specific release digest before execution. The generated
SPDX 2.2 manifest must validate all ten packages plus the checksum file and
contain the required resolved package graph.

Every future release adds GitHub/Sigstore build provenance and SBOM
attestations, a portable `release-provenance.intoto.jsonl` asset, NuGet Trusted
Publishing, NuGet repository-signature verification, and a content readback
that differs from the qualified package only by NuGet's `.signature.p7s`
entry. Publication validates the exact SLSA subject inventory and verifies the
portable bundle against the release workflow and qualified commit before the
protected environment obtains a short-lived NuGet credential. The final
thirteen-asset immutable GitHub Release is then covered by GitHub's native Release
and release-asset verification.

## Primary references

- [.NET 10 release metadata](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json),
  [SQL Server provider metadata](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.SqlServer/10.0.15)
  and [PKCS 10.0.15](https://www.nuget.org/packages/System.Security.Cryptography.Pkcs/10.0.15),
  retrieved 2026-11-20. SDK 10.0.404 carries runtime 10.0.15; the EF SQL
  Server dependency requires PKCS at least 10.0.13, so the former 9.x floor
  cannot satisfy the current provider graph.
- [Testcontainers 4.17.0](https://www.nuget.org/packages/Testcontainers.MySql/4.17.0),
  [test SDK 18.12.0](https://www.nuget.org/packages/Microsoft.NET.Test.Sdk/18.12.0),
  [Roslyn analyzers 5.12.0](https://www.nuget.org/packages/Microsoft.CodeAnalysis.Analyzers/5.12.0),
  [Dependency Review 5.0.1](https://github.com/actions/dependency-review-action/releases/tag/v5.0.1)
  and [Scorecard 2.6.0](https://github.com/ossf/scorecard-action/releases/tag/v2.6.0),
  retrieved 2026-11-20; version availability does not qualify the candidate.
- [PostgreSQL versioning policy](https://www.postgresql.org/support/versioning/)
  and [MariaDB releases](https://mariadb.org/mariadb/all-releases/), retrieved
  2026-11-20. Registry availability was checked independently; a server release
  does not prove that its official container tag has been published.
- [GitHub-hosted runner Ubuntu 26.04 migration announcement](https://github.com/actions/runner-images/issues/14748),
  retrieved 2026-09-21 and rechecked 2026-10-25.
- [GitHub-hosted runner labels and x64 resources](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)
  and [Ubuntu 26.04 image software](https://github.com/actions/runner-images/blob/main/images/ubuntu/Ubuntu2604-Readme.md),
  retrieved 2026-10-25. The image's preinstalled SDK is not the repository's
  SDK selection; `setup-dotnet` continues to install the exact `global.json` version.
- [.NET 10 release metadata](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json),
  retrieved 2026-10-20; SDK 10.0.402 carries runtime 10.0.13.
- [.NET 10 SDK 10.0.403 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json),
  [EF Core Relational 10.0.14](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Relational/10.0.14),
  [Dependency Injection Abstractions 10.0.14](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection.Abstractions/10.0.14),
  [dotnet-ef 10.0.14](https://www.nuget.org/packages/dotnet-ef/10.0.14) and
  [PKCS 9.0.22](https://www.nuget.org/packages/System.Security.Cryptography.Pkcs/9.0.22),
  retrieved 2026-10-29. The SDK carries runtime 10.0.14; PKCS retains its existing
  consumer dependency major.
- [EF Core Relational 10.0.13 package](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Relational/10.0.13),
  [Dependency Injection Abstractions 10.0.13 package](https://www.nuget.org/packages/Microsoft.Extensions.DependencyInjection.Abstractions/10.0.13),
  [Npgsql EF Core 10.0.3 package](https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL/10.0.3),
  [EF Core SQLite Core 10.0.13 package](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.Sqlite.Core/10.0.13),
  [EF Core SQL Server 10.0.13 package](https://www.nuget.org/packages/Microsoft.EntityFrameworkCore.SqlServer/10.0.13),
  and [EF Core custom SQLite versions](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions),
  retrieved 2026-10-20.
- [Doka 10.2.0 package](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.2.0),
  [release notes](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.2.0/CHANGELOG.md),
  [provider configuration](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.2.0/docs/provider-configuration.md),
  [ownership-aware connection decision](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.2.0/docs/decisions/D-029-ownership-aware-connection-invariants.md),
  [value-generation strategies](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.2.0/src/Doka.EntityFrameworkCore.MySql/MySqlValueGenerationStrategy.cs),
  and [migration-operation handler contract](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.2.0/docs/migration-operation-handlers.md),
  retrieved 2026-08-31.
- [Doka 10.3.0 package](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.3.0),
  [release notes](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.3.0/CHANGELOG.md),
  [migration-operation metadata](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.3.0/src/Doka.EntityFrameworkCore.MySql/Migrations/MySqlMigrationOperationMetadata.cs),
  and [signed release tag](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/releases/tag/v10.3.0),
  retrieved 2026-09-01.
- [Doka 10.4.2 package](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.4.2),
  [migration-operation handler contract](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/v10.4.2/docs/migration-operation-handlers.md),
  and [signed release tag](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/releases/tag/v10.4.2),
  retrieved 2026-09-21.
- [Doka 10.5.0 package](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.5.0),
  [verified signed release](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/releases/tag/v10.5.0),
  [package catalog provenance](https://api.nuget.org/v3/catalog0/data/2026.10.14.23.33.52/doka.entityframeworkcore.mysql.10.5.0.json),
  and [GUID migration contract](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/27a8d3cce3ce2bc64f6efd1e9034b1cd66eb1ff0/docs/operations/migrations.md#guid-representation-changes),
  retrieved 2026-10-15.
- [Doka 10.6.0 package](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.6.0),
  [verified signed release](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/releases/tag/v10.6.0),
  [package catalog provenance](https://api.nuget.org/v3/catalog0/data/2026.10.20.06.45.24/doka.entityframeworkcore.mysql.10.6.0.json),
  and [physical index-budget contract](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/d1e40a7b211c10b354e5aa6df80d9fea710d8f58/docs/provider-configuration.md#index-key-byte-fidelity),
  retrieved 2026-10-20.
- [Doka 10.6.1 package](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.6.1),
  [verified signed release](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/releases/tag/v10.6.1),
  [package catalog provenance](https://api.nuget.org/v3/catalog0/data/2026.10.22.06.05.55/doka.entityframeworkcore.mysql.10.6.1.json),
  and [GUID SQL-default contract](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/53c8fbd7ae8987b5c376250fe4d6296d729669cf/docs/operations/migrations.md#guid-sql-defaults),
  retrieved 2026-10-22.
- [Doka 10.6.2 package](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.6.2),
  [verified signed release](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/releases/tag/v10.6.2),
  [package catalog provenance](https://api.nuget.org/v3/catalog0/data/2026.10.25.00.38.58/doka.entityframeworkcore.mysql.10.6.2.json),
  and [GUID SQL-default contract](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/6e2618140d821bd6befdf46181fcb82dca323c4f/docs/operations/migrations.md#guid-sql-defaults),
  retrieved 2026-10-25.
- [Doka 10.6.3 package](https://www.nuget.org/packages/Doka.EntityFrameworkCore.MySql/10.6.3),
  [verified signed release](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/releases/tag/v10.6.3),
  [package catalog provenance](https://api.nuget.org/v3/catalog0/data/2026.10.29.13.59.01/doka.entityframeworkcore.mysql.10.6.3.json),
  and [connector security upgrade](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/0372df8dd0c77b6f04e72fa94adae837e4deba7a/docs/security/dependency-audits.md#mysqlconnector-security-floor),
  retrieved 2026-10-29.
- [Doka 10.6.4 package metadata](https://api.nuget.org/v3-flatcontainer/doka.entityframeworkcore.mysql/10.6.4/doka.entityframeworkcore.mysql.nuspec),
  retrieved 2026-11-05. The bug-fix release retains the same EF Core,
  MySqlConnector and System.IO.Hashing dependency ranges.
- [Doka 10.6.5 package metadata](https://api.nuget.org/v3-flatcontainer/doka.entityframeworkcore.mysql/10.6.5/doka.entityframeworkcore.mysql.nuspec),
  retrieved 2026-11-09. The maintenance release retains those dependency ranges,
  the public migration SPI and the existing storage defaults.
- [Doka 10.7.0 package metadata](https://api.nuget.org/v3-flatcontainer/doka.entityframeworkcore.mysql/10.7.0/doka.entityframeworkcore.mysql.nuspec),
  retrieved 2026-11-19. The published .NET/EF Core 10 package identifies commit
  `0bafbb1d3a0facd6ab67e26e98e83477f69ee1b7`, MySqlConnector
  `[2.6.1,4.0.0)` and System.IO.Hashing minimum 10.0.15.
- [MySQL 8.4 InnoDB limits](https://dev.mysql.com/doc/refman/8.4/en/innodb-limits.html),
  [MySQL 8.4 CREATE INDEX](https://dev.mysql.com/doc/refman/8.4/en/create-index.html),
  [MySQL 8.4 invisible indexes](https://dev.mysql.com/doc/refman/8.4/en/invisible-indexes.html),
  [MySQL 8.4 CHECK constraints](https://dev.mysql.com/doc/refman/8.4/en/create-table-check-constraints.html),
  [MySQL 8.4 foreign keys](https://dev.mysql.com/doc/refman/8.4/en/create-table-foreign-keys.html),
  [MariaDB InnoDB limitations](https://mariadb.com/docs/server/server-usage/storage-engines/innodb/innodb-limitations),
  [MariaDB InnoDB row formats](https://mariadb.com/docs/server/server-usage/storage-engines/innodb/innodb-row-formats/innodb-row-formats-overview),
  and [MariaDB ignored indexes](https://mariadb.com/docs/server/ha-and-performance/optimization-and-tuning/optimization-and-indexes/ignored-indexes),
  retrieved 2026-09-01.
- [MariaDB 12.1 changes](https://mariadb.com/docs/release-notes/community-server/12.1/changes-and-improvements-in-mariadb-12.1),
  retrieved 2026-09-02.
- [PostgreSQL 17 `pg_constraint`](https://www.postgresql.org/docs/17/catalog-pg-constraint.html),
  and [PostgreSQL 18 `CREATE INDEX`](https://www.postgresql.org/docs/18/sql-createindex.html),
  retrieved 2026-09-02.
- [PostgreSQL 18 `pg_index`](https://www.postgresql.org/docs/18/catalog-pg-index.html),
  [`pg_class`](https://www.postgresql.org/docs/18/catalog-pg-class.html), and
  [`pg_inherits`](https://www.postgresql.org/docs/18/catalog-pg-inherits.html),
  retrieved 2026-09-01.
- [EF Core generated values](https://learn.microsoft.com/en-us/ef/core/modeling/generated-properties),
  retrieved 2026-08-29.
- [PostgreSQL versioning policy](https://www.postgresql.org/support/versioning/)
  and [18.6/17.11/16.15/15.19/14.24 announcement](https://www.postgresql.org/about/news/postgresql-186-1711-1615-1519-1424-and-19-beta-3-released-3365/),
  retrieved 2026-08-27.
- [MySQL supported platforms and lifecycle](https://www.mysql.com/support/supportedplatforms/database.html),
  [MySQL 8.4.11 release notes](https://dev.mysql.com/doc/relnotes/mysql/8.4/en/news-8-4-11.html),
  and [MySQL 9.7.2 release notes](https://dev.mysql.com/doc/relnotes/mysql/9.7/en/news-9-7-2.html),
  retrieved 2026-08-27.
- [MariaDB 10.11.18, 11.4.12, and 11.8.8 release announcement](https://mariadb.com/resources/blog/mariadb-community-server-q2-2026-corrective-releases/),
  [MariaDB 12.3 release inventory](https://mariadb.org/mariadb/all-releases/),
  and [MariaDB release model](https://mariadb.com/docs/release-notes/community-server/about/release-model),
  retrieved 2026-08-27.
- [GitHub artifact attestations](https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations/using-artifact-attestations-to-establish-provenance-for-builds),
  [NuGet Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing),
  and [Microsoft SBOM Tool](https://github.com/microsoft/sbom-tool), retrieved
  2026-08-27.
