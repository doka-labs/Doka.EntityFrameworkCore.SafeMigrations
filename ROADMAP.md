# Project direction

Planning horizon: 2026-08-26 through 2027-08-26. This document describes
intended work and explicit non-goals, not promised publication dates.

## One complete delivery

The initial complete stable delivery is **10.0.0**, aligned with .NET 10,
EF Core 10, and Doka 10. The three published release candidates qualified
successive revisions of that same complete contract and the publication
workflow. They were not reduced feature releases or a plan to move unfinished
functionality into a later version.

- Prepare the repository for public contribution and auditable maintenance.
- Qualify all three packages together: Core, MySQL/MariaDB, and PostgreSQL.
- Exercise the real candidate workflow before stable publication, including
  protected approval, signed identity, exact-byte publication, and readback.
- Validate heterogeneous legacy convergence, runtime/history semantics,
  provider-specific failure paths, and operational recovery over the full
  [qualification matrix](docs/support-and-qualification.md).
- The verified rc.3 contract was promoted to stable 10.0.0 without a feature
  delta, and the exact stable package bytes were independently qualified.

Prepared release notes and workflow files do not prove a package is published.
The actual [release record](https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/releases)
and NuGet readback are the publication authority.

## Maintenance during the horizon

After the complete delivery, respond to defects, security reports, supported
dependency updates, and changed external conditions. A confirmed defect is a
changed condition requiring assessment; an incomplete initial feature is not
a maintenance strategy. There is no scheduled second feature version.

The published 10.0.1 maintenance release responded to Doka 10.1.2 and an
observed publication-assurance gap. Current unreleased maintenance responds to
Doka 10.2.0's ownership-aware connection invariants and to a confirmed
generated-migration namespace defect. These are changed dependency conditions
and a correctness repair, not an incomplete initial feature line.

Requalify affected behavior before adopting a provider, EF Core, database,
SDK, action, or tooling update. Preserve public API, report, migration, data
integrity, and resource-use contracts. Correctness or security takes priority
over a planned calendar date. Record compatibility implications and any
necessary servicing release in the changelog and release notes.

The public [OpenSSF Best Practices project](https://www.bestpractices.dev/projects/14265)
reports Passing. Maintain that self-assessment using the
[evidence mapping](docs/openssf-best-practices.md). Silver and Gold are not
claimed, and the separate automated Scorecard result is not established until
its repository workflow publishes a readable result.

## Explicit non-goals

- No destructive general-purpose schema synchronizer or inferred rename/data
  repair across unknown legacy installations.
- No automatic history rewriting, destructive baseline rollback, or weakening
  of failed guards to make a deployment continue.
- No different Core target model per instance under one migration history.
- No provider implementation inside Core or cross-provider runtime dependency.
- No coordinated Doka release gate or Doka source build in this repository.
- No new framework, hosted documentation service, badge automation, or release
  workflow solely to make the documentation look more elaborate.
- No claim of certified security, independent human review, failover topology
  support, or unqualified engine/version support without the required evidence.
- No planned feature subset, second feature release, or platform-major upgrade
  in anticipation of dependencies that have not changed the requirements.

## Review and change

Review this direction quarterly and after a relevant vulnerability, upstream
support change, confirmed consumer requirement, or accepted architectural
change. Update it through the [governance process](GOVERNANCE.md). A changed
scope must be explicit; release notes in [CHANGELOG.md](CHANGELOG.md) describe
what actually shipped.
