# Errors and readable migration reports

SafeMigrations diagnostics identify the authored operation, affected owner and
established reason without changing the migration decision. No automatic
preflight, data scan, new privilege or external logging service is required.
Prepared stable 10.5.0 introduces this diagnostic contract relative to
10.4.11. Preparation does not establish public package availability.

## Read a rejected operation

Start with the operation, table and named member rather than the generic code.
For example, a required column without a replacement on a populated owner can
produce a diagnostic such as:

```text
doka_sm_data_blocked: EnsureColumn <provider-default>.ExampleAnswers.Id
reason=Existing rows need an explicitly supplied replacement value.
next=Add nullable, explicitly backfill, then make the column required.
```

Exact punctuation is not a machine contract. The stable guard/reason codes,
assessment action and report status remain the machine-readable evidence.
An unqualified schema is not an independently verified physical database name.
Unknown owners and ordinals are labeled unavailable; they are not inferred from
constraint names or the final migration's identifier.

`SafeMigrationDiagnosticMessages.FormatRuntime` preserves recognized guard
categories and replaces an unrecognized caller-supplied code with
`doka_sm_unknown`. This is a display fallback, not a native database error code
or evidence of an owned rejection. The former presentation alias
`doka_sm_postcondition_failed` renders as the existing `doka_sm_postcondition`.

Specific reasons are used only when the classifier proves them. A broad
`DataBlocked` result alone cannot distinguish NULL values, oversized strings,
duplicates or FK orphans. A prerequisite failure may be an absent object,
incompatible storage, unsuitable referenced key or discarded ordered evidence.
The message must not turn any of these into a guessed diagnosis or a suggested
safety bypass. A postcondition error states that DDL may already have run.

## JSON owner identity

Canonical reports now use schema version 4; selected views use view version 3.
Each assessment preserves its original `objectName` and adds:

- `identityKnown`, `schema`, `table`, `identityObjectName` and `columns`;
- FK `principalSchema`, `principalTable` and `principalColumns`;
- rename `newSchema`, `newTable` and `newObjectName`;
- `migrationId` and `policy` when known;
- `reasonCode`, `reason`, `nextStep` and `diagnosticContextTruncated`.

This distinguishes two tables whose columns are both named `Id`. FK diagnostics
show the dependent and principal direction explicitly. For ordinary EF
operations, authored identity does not certify behavior: the assessment remains
`provider_owned_not_analyzed`. Old public assessment constructors retain unknown
identity rather than inventing a table.

New displayed context is bounded and truncation is explicit. The existing
`objectName` field retains its exact meaning and value. No row, seed, SQL
parameter, comment or arbitrary default-expression payload is added. A report
still contains structural identifiers and must be protected as deployment
evidence. Fingerprint formats and selection/order semantics are unchanged.

The package retains all older schemas for saved evidence. Consumers validating
the new output must select the matching schema; do not validate a version 4
document against version 3. Rebuild consumers that reference the compiled
`SafeMigrationRunReport.CurrentSchemaVersion` constant, now 4. See
[API reference](api-reference.md#reports-serialization-and-failure).

## Console and log summaries

The built-in text writer avoids application-side filtering and DTOs:

```csharp
var report = await runner.AnalyzePendingMigrationsAsync(
    dbContext,
    new SafeMigrationRunOptions("instance-pseudonym"),
    cancellationToken);

SafeMigrationReportText.Write(Console.Error, report);
report.ThrowIfBlocked();
```

`Format(report)` returns the same summary as a string. Both methods default to
`BlockingOnly`; pass `Complete` or `NonMatching` explicitly for other selections.
At most 20 assessment details and 10 informational unexpected-object details
are displayed. Total and omitted counts remain explicit, and the complete JSON
report remains available. Writers stay caller-owned; no file or automatic
console/log output is created by the library.

Deferred entries are not proven failures or successful execution. They retain
their invalidating origin and require a fresh decision during execution.
Blocked postflight entries are selected by failed postconditions, not by the
earlier planner action. Text and JSON share the same selection rules.

## Runtime provider differences

| Provider | Readable failure transport |
| --- | --- |
| MySQL/MariaDB .NET execution | Positively correlated assertions and exactly identified owned staging CHECK failures raise `SafeMigrationExecutionException` after Doka cleanup; the original `MySqlException` remains its exact `InnerException`. |
| MySQL/MariaDB generated scripts | A full bounded diagnostic result row precedes an owned assertion. The native category remains 1062/23000 with its `doka_sm_*` key and bounded correlation. The script client must consume/display results before the subsequent error. |
| PostgreSQL | Required context is in native `MESSAGE`, with unchanged SQLSTATE and guard-token prefix. No `Include Error Detail` setting is enabled. |
| SQL Server | Native `THROW` keeps its guard number/state and adds bounded text; identity-scope recovery remains separate. |
| SQLite | Managed guards add owner and established analysis/decision context; transactional rollback remains unchanged. Safe-operation scripts remain explicitly unsupported. |

The new MySQL/MariaDB owned-guard exception type is a caller-visible change. Code
catching only `MySqlException` for SafeMigrations assertions must handle
`SafeMigrationExecutionException` and inspect its original cause when needed.
It exposes `GuardCode`, `ReasonCode`, `Identity`, `OperationOrdinal` and `Policy`.
It does not fabricate a migration ID. Guard codes are not a promise of rollback
across earlier MySQL/MariaDB implicitly committed DDL.

An owned required-add CHECK failure establishes that the staging invariant was
violated, not whether the row was pre-existing or concurrent. Generated scripts
cannot add a diagnostic result after a native DDL error has aborted that command;
such errors retain their native representation. This does not change the guarded
DDL or its recovery contract.

An arbitrary native exception, timeout, cancellation or uncertain provider
cleanup failure remains its original exception. Error 1062 or a similarly named
application constraint alone is not an owned SafeMigrations rejection. Never
use localized sentence fragments as an application classifier contract. The
MySQL/MariaDB adapter recognizes only its qualified native message grammar and
exact command correlation; an unexpected rendering remains the original native
exception rather than being guessed into a SafeMigrations rejection. See
[failure codes](runbooks/failure-codes.md#runtime-database-error-identity).

## Other error phases and privacy

Argument errors retain their parameter and violated rule. Setup and scaffolding
errors explain the missing registration or unsupported source contract. Model
mismatches describe model-contract identity, not live database drift. Internal
invariant failures require redacted diagnostic evidence, not an invented data
repair. Existing exception types and parameter-name contracts are retained.

Formatting is bounded, escapes control/non-ASCII identifiers for single-line
display and runs during command generation, failure handling or an explicit
report-rendering request. It performs no I/O or data lookup. Identifiers are
never metric dimensions.
Native causes are retained, not sanitized copies; consumers remain responsible
for access control and redaction when exporting native exception details.
