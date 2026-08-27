# Support

This policy routes SafeMigrations questions and reports to the appropriate
channel. Public support is best effort. It is not a commercial support
agreement, a response-time guarantee, or an emergency incident-response service.

## Choose the Correct Channel

| Request | Channel | Public? |
| --- | --- | --- |
| Suspected security vulnerability | Follow [SECURITY.md](SECURITY.md#reporting-a-vulnerability) | No |
| Harassment or other conduct concern | Follow [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) | No |
| Reproducible SafeMigrations defect | [Bug report][bug] | Yes |
| Capability or compatibility proposal | [Feature request][feature] | Yes |
| Usage, configuration, or capability question | [Support question][support] | Yes |

Do not send ordinary usage questions to the private security or conduct
channels. Do not post vulnerabilities, credentials, or confidential customer
information in public issues, pull requests, or attachments.

## Before Opening an Issue

Check the [README](README.md), [sample](samples/Doka.EntityFrameworkCore.SafeMigrations.Sample/README.md),
[support and qualification contract](docs/support-and-qualification.md),
[failure codes](docs/runbooks/failure-codes.md), and [existing issues][issues].
For deployment failures, also consult the
[deployment and recovery runbook](docs/runbooks/deployment-and-recovery.md).

An actionable report normally includes:

- exact SafeMigrations package or commit, EF Core, provider, driver, and .NET versions;
- MySQL, MariaDB, or PostgreSQL family, exact server version, OS/architecture,
  and relevant proxy, pooler, cluster, or managed-service topology;
- the runtime, EF CLI, script, bundle, preflight, or postflight path;
- a minimal migration, expected definition, selected policy, and registration;
- a synthetic starting schema, relevant data, and migration-history state;
- expected and actual outcomes, stable failure codes, and any resulting changes;
- whether an isolated rerun or a different supported engine changes the result;
- the last working version if this is a regression.

For provider-neutral Core issues, state that no database is involved. Do not
invent server details to complete a form. For legacy consolidation, describe
partial tables, inconsistent history, or derived contexts explicitly; these
starting-state differences are central to diagnosis.

## Safe Diagnostic Sharing

Use disposable databases and synthetic data for reproducers. Replace sensitive
object names and values consistently so relationships and identifier behavior
remain reproducible. Use fenced blocks for code, SQL, and selected diagnostics.

Remove passwords, tokens, connection strings, personal data, customer/tenant
identifiers, internal host names, and confidential schema names from every
attachment. SafeMigrationRunReport contains object identities, operation
assessments, instance/migration metadata, and fingerprints; it does not embed
the expected definition objects. The absence of credentials does not make a
report public-safe.
Do not upload production dumps, backups, or complete unredacted logs.

If a migration fails against production, preserve the existing evidence and use
your tested recovery procedure. Do not delete EF history, mark a blocked
migration as applied, weaken a safety policy, or retry unknown destructive SQL
merely to make an issue reproducible. Coordinate suspected vulnerabilities
through the private security process.

## Supported Scope and Ownership

The authoritative package, runtime, engine, and capability matrix is maintained
in [Support and qualification](docs/support-and-qualification.md). This policy
does not extend that matrix or promise a release date. Use published packages
when available, or identify the exact commit for an unpublished build.

SafeMigrations owns its intent model, policies, expected definitions, planning,
provider adapters, convergence behavior, preflight/postflight, reports, and
documented EF migration integration. MySQL and MariaDB share the `.MySql`
adapter but are independently qualified engine families. PostgreSQL uses the
separate `.PostgreSql` adapter. Core remains provider neutral.

Reports are assessed at that boundary before being redirected. If evidence
locates a defect in EF Core, Doka, Npgsql, a driver, or an engine, maintainers
link a minimal upstream reproducer and retain any SafeMigrations-specific
impact or regression test. A database family alone is not sufficient reason
to redirect a report upstream.

General application architecture, database administration, unsupported engine
versions, inferred semantic renames or merges, and automatic reconstruction of
unknown legacy data are not maintained SafeMigrations contracts. They may
receive community guidance without expanding the support contract.

## Response and Lifecycle

Priority follows security, data integrity, supported-matrix regressions,
reproducibility, and affected users rather than submission order. Maintainers
may request a smaller reproducer or further sanitized evidence.

Issues may be closed as duplicates, documented expected behavior, unsupported
configurations, or upstream defects, or when essential reproduction details
remain unavailable. The reason and relevant references should be recorded.
New evidence can justify reopening an issue. Security reports follow the
separate acknowledgement and disclosure targets in the security policy.

[issues]: https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/issues
[bug]: https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/issues/new?template=bug-report.yml
[feature]: https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/issues/new?template=feature-request.yml
[support]: https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/issues/new?template=support-question.yml
