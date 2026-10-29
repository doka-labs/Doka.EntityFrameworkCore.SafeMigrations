# Release process

This document defines the release contract. The executable workflow is
[release-candidate.yml](../.github/workflows/release-candidate.yml); the exact
operator commands and order live only in
[Release publication operations](operations/release-publication.md).

## Identity and entry conditions

SafeMigrations publishes these package IDs at one version:

- `Doka.EntityFrameworkCore.SafeMigrations`;
- `Doka.EntityFrameworkCore.SafeMigrations.MySql`;
- `Doka.EntityFrameworkCore.SafeMigrations.PostgreSql`;
- `Doka.EntityFrameworkCore.SafeMigrations.Sqlite`; and
- `Doka.EntityFrameworkCore.SafeMigrations.SqlServer`.

Stable 10.4.6 introduced this five-package inventory; releases 10.4.2 through
10.4.5 retain their historical four-package set. Version 10.6.1 keeps the shared
.NET/EF Core 10 identity, services dependency floors and corrects automated
release-line maintenance. The [changelog](../CHANGELOG.md#1061---2026-10-29) and
[upgrade notes](support-and-qualification.md#upgrade-to-1061) record current
consumer requirements. Public APIs, migration policies, report schemas and
fingerprint formats are unchanged relative to 10.6.0. Applications skipping
that minor release must still review its
[computed/CHECK approval boundaries](support-and-qualification.md#upgrade-to-1060).
Do not carry an obsolete contract approval forward or rewrite historical
migrations. Timing comparisons stay informational; functional and evidence
failures still block. Selecting another provider does not introduce SQL Server
runtime dependencies.

The workflow accepts a canonical lowercase NuGet version without a leading
`v`. The version must belong to the source `VersionPrefix`, have exactly one
dated changelog entry, be absent from all five NuGet package IDs, and be
dispatched from the exact current protected source-line SHA before any release
tag exists. [D-018](decisions/D-018-release-lines-and-monotonic-publication.md)
defines exact registered release lines. Only main/10 is active; maintenance/10.x
is inactive. This capability does not activate another framework or adopt .NET 11.

## Reversible qualification

Each admitted line uses its own reviewed SDK, EF graph, source and workflow.
Preflight freezes its exact ref/SHA/run, original producing attempt and toolchain
against independently observed protected-main authority. Prepared lines and the
qualification-only input run the complete reversible checks but skip the
environment-bearing publisher job, rather than reaching it with disabled writes.

CI and release call the same reusable quality workflow. It enforces:

- locked restore, formatting, warning-free Release build, Core tests, and
  Public API analyzers;
- merged line and branch coverage thresholds;
- all supported MySQL, MariaDB, PostgreSQL, and SQL Server integration cells
  plus the locked in-process SQLite runtime;
- EF CLI migrations, supported scripts, and Migration Bundles in every engine
  cell, including explicit rejection of SQLite safe-operation scripts;
- deterministic double-pack, exact package contents, provider separation, and
  isolated package-only consumers; and
- SPDX 2.2 SBOM generation and validation.

It also runs Core, MySQL/MariaDB, PostgreSQL, SQLite, and SQL Server benchmarks
in `--report-only` mode. Budget overruns remain visible in the retained evidence
but do not block qualification on shared hardware. Invalid benchmark
configuration, execution failures, incomplete measurements and report-write
failures remain blocking. Build, tests, coverage, tooling, package and SBOM gates
are unchanged.

The PostgreSQL EF tooling probe waits for the final TCP listener, never the
image's temporary socket-only initialization server. Large live performance
fixtures receive a fixture-only command timeout; production SafeMigrations
continues to use its normal configured timeout.

The qualified workflow artifact contains the ten package archives,
`SHA256SUMS`, performance evidence, and the SPDX manifest. GitHub creates build
provenance and SBOM attestations for those exact bytes. The attestation job
validates the build-provenance Sigstore bundle against all ten packages,
`SHA256SUMS`, and `manifest.spdx.json`, materializes exactly one canonical
`release-provenance.intoto.jsonl` record, and uploads it under a run- and
attempt-qualified artifact name before publication can reach the protected
environment.

## Irreversible publication boundary

The `publish` job is the only job with NuGet OIDC and GitHub Release write
permissions. Environment protection keeps it waiting while all reversible
work is reviewed. After the operator pushes the signed annotated tag and
approves the same run, publication verifies:

- the candidate remains on its exact protected release-line history;
- the tag peels to the qualified SHA and its SSH signature matches the
  repository's allowed-signers policy;
- package hashes and contents still match qualification; and
- the short-lived NuGet credential is issued for the exact repository,
  workflow, and `nuget` environment.

After approval and inside the shared non-cancelling publication queue, admission
reads current protected-main policy independently of the candidate checkout.
It verifies source, environment, frozen line identity, exact original artifact
metadata and candidate bytes. Permission is checked again before draft creation,
NuGet credentials and final publication. A previously qualified line cannot
publish after deactivation or reassignment. Reviewer, self-review, administrator
and Trusted Publishing settings are retained, not redesigned by line tooling.

Primary and symbol packages are pushed separately with duplicate tolerance for
same-run recovery. Completion requires downloading each public primary package,
verifying its NuGet repository signature, and comparing every archive entry
with the qualified package after excluding only NuGet's `.signature.p7s`.

Before draft creation or credential exchange, publication validates the
portable bundle's Sigstore envelope, SLSA v1 predicate, unique subject names,
and exact subject SHA-256 digests. It then verifies every selected subject with
`gh attestation verify --bundle`, pinned to the repository, release workflow,
workflow commit, exact qualified source ref and commit, and hosted-runner
boundary. An API-hosted attestation without the downloaded bundle cannot satisfy
this gate.

Before the NuGet credential is requested, the GitHub Release starts as a draft
with the expected title, Changelog-derived notes, classification, exact ten
qualified package files, `SHA256SUMS`, `manifest.spdx.json`, and
`release-provenance.intoto.jsonl`. On a same-run retry, matching uploaded assets
are retained and missing assets are added; any metadata, unexpected name, or
SHA-256 digest conflict fails closed. Draft discovery uses the authenticated,
paginated Release inventory because GitHub's tag endpoint returns published
Releases only. The complete thirteen-asset draft is read back before the first
NuGet push.

Immediately before requesting the NuGet credential, the workflow checks the
public state of every primary package with bounded retries. Missing primary
packages are published in dependency order: Core first, followed by MySQL,
PostgreSQL, SQLite, and SQL Server. A same-run retry skips primary packages that NuGet.org
already exposes through the Flat Container and retries every primary or symbol
package with duplicate tolerance when visibility still lags. The final
signed-content readback remains the acceptance gate.

After signed NuGet content has been read back, the workflow publishes the
verified draft. It then waits for the published immutable state and GitHub's
automatically generated Release and asset attestations with a bounded retry
window. Prereleases are never Latest. Stable promotion compares canonical
SemVer inside the shared publisher; an older major or out-of-order patch cannot
displace a higher stable release. An immutable historical retry verifies its
release without requiring continued Latest status. The signed tag
is the commit identity; `targetCommitish` is not treated as one after the tag
already exists.

## Recovery semantics

NuGet cannot publish five package IDs atomically. A network failure can occur
after one or more uploads are accepted. The supported recovery is rerunning the
failed `publish` job in the original run. Its publication preflight skips
primary packages already visible through the Flat Container, and
duplicate-tolerant primary and symbol pushes are safe to repeat. A new workflow
dispatch is intentionally rejected because the version is no longer unused.
Signed public content and the staged or immutable GitHub Release must still
match exactly. Missing draft assets can be uploaded; conflicting assets are
never overwritten. The successful attestation job supplies the same immutable
attempt-qualified provenance artifact to a publish-only retry; rerunning the
attestation job produces a new attempt-qualified artifact instead of
overwriting earlier evidence. A conflict is terminal. A timeout while waiting
for the platform-generated Release attestation is retryable only through the
same failed job; it never authorizes ignoring verification. Tags and published
versions are never moved, replaced, or reused.

Frozen line and admission receipts are separate immutable same-run workflow
artifacts; the public thirteen-asset inventory and twelve provenance subjects do
not change. A publish-only retry restores original admission and revalidates the
actual original artifact IDs/digests and local bytes against current authority.
Missing/expired/changed evidence after publication may have begun cannot be
replaced with a fresh admission. Re-running all jobs may rebuild artifacts and
is not an exact-byte recovery. Complete a release before changing its line.

## Evidence boundary

Actions logs, retained qualification and portable-provenance artifacts, GitHub
attestations, the authorized tag, NuGet repository signatures, and immutable
Release assets are the evidence. The focused provenance validator and Release
reconciliation script have no persisted release state. They enforce malformed,
missing, duplicate, extra, digest-conflicting, retry, and timeout paths through
executable positive and negative tests. The repository does not maintain a
second event-sourced release database. Hosted configuration and an actual
successful release remain necessary evidence.

Registered lines receive their own full CI and exact-base Dependency Review
before activation. Active lines receive branch-native scheduled qualification,
transitive vulnerability audits and reviewed dependency-update targets. See
[Release lines](operations/release-lines.md) for activation, scheduling and the
explicit hosted-evidence boundary.
