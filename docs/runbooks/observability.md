# Observability and evidence handling

## Public instruments

ActivitySource and Meter are both named
`Doka.EntityFrameworkCore.SafeMigrations`. The run activity is
`safe_migrations.run`. Names are published by
[`SafeMigrationDiagnostics`](../../src/Doka.EntityFrameworkCore.SafeMigrations/Diagnostics/SafeMigrationDiagnostics.cs).

| Instrument | Unit | Tags |
| --- | --- | --- |
| `safe_migrations.run.count` | Count | `db.system.name`, `safe_migrations.provider`, `safe_migrations.mode`, `safe_migrations.status` |
| `safe_migrations.run.duration` | `ms` | Same completed-run tags |
| `safe_migrations.operation.count` | `{operation}` | Same completed-run tags |
| `safe_migrations.run.failure.count` | Count | `safe_migrations.mode`, `safe_migrations.failure_code` |

Activity tags are mode, operation count, engine family, provider, and, on a
recorded failure, bounded failure code and the public runbook URL. It does not
attach exception objects, messages, stack traces, SQL, names, or instance IDs.
Use the [failure-code table](failure-codes.md#telemetry-failure-codes).

## Measurement boundary

These instruments describe preflight/postflight runner calls, not every EF
migration command or application query. Completed reports, including `Blocked`,
increment the completed-run counter; a blocked assessment is not an exception
failure. The activity can complete successfully while the deployment must stop.

Canonical model/fingerprint validation occurs before the activity and timed
analysis region. Connection opening occurs before the runner's analysis
exception handler. Cancellation is propagated and is not counted as an
analysis failure. Closing an owned connection occurs after completed-report
measurement. Therefore neither the failure counter nor duration histogram is
a complete end-to-end deployment SLI. A listed failure-code vocabulary also
does not prove every possible throw site is inside that instrumentation region.

The deployment orchestrator must record its own start/end/cancel/blocked outcome,
history check, backup reference, and postflight completion. Do not infer a
successful deployment from zero failure events or one successful activity.

## Privacy and retention

Full JSON reports intentionally contain object identity, pseudonymous instance
IDs, migration IDs, and fingerprints. Store them with access controls and an
organization-defined retention period, linked to the protected deployment
inventory. Do not publish them unredacted in GitHub issues or telemetry.

The caller chooses a pseudonymous instance ID; SafeMigrations does not remove
secrets from arbitrary caller text. Use a random/opaque deployment identifier,
not a database name or connection string. Configure EF/connector/exporter
logging separately and avoid sensitive-data logging in production.

## Operational signals

- Alert on blocked postflight or model/contract mismatch using the protected
  deployment outcome; keep writes fenced and use the recovery runbook.
- Investigate unexpected latency with provider identity, operation count,
  cancellation/timeout status, and protected catalog evidence.
- Compare live clean/noisy p95 only within the qualified runner/context.
  Benchmark budgets are regression evidence, not a universal production SLA.
- A missing telemetry event requires checking process, cancellation, export,
  and instrumentation boundaries; it is not proof of no migration activity.

The [deployment runbook](deployment-and-recovery.md) owns stop/continue
decisions. The [privacy tests](../../tests/Doka.EntityFrameworkCore.SafeMigrations.Tests/Unit/Features/Lifecycle/SafeMigrationRunContractTests.cs)
guard the bounded emitted fields.
