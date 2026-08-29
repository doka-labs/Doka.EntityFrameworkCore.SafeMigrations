# Release process

This document defines the release contract. The executable workflow is
[release-candidate.yml](../.github/workflows/release-candidate.yml); the exact
operator commands and order live only in
[Release publication operations](operations/release-publication.md).

## Identity and entry conditions

SafeMigrations publishes these package IDs at one version:

- `Doka.EntityFrameworkCore.SafeMigrations`;
- `Doka.EntityFrameworkCore.SafeMigrations.MySql`; and
- `Doka.EntityFrameworkCore.SafeMigrations.PostgreSql`.

The workflow accepts a canonical lowercase NuGet version without a leading
`v`. The version must belong to the source `VersionPrefix`, have exactly one
dated changelog entry, be absent from all three NuGet package IDs, and be
dispatched from the exact current `main` SHA before any release tag exists.

## Reversible qualification

CI and release call the same reusable quality workflow. It enforces:

- locked restore, formatting, warning-free Release build, Core tests, and
  Public API analyzers;
- merged line and branch coverage thresholds;
- Core, MySQL/MariaDB, and PostgreSQL performance/allocation budgets;
- all supported MySQL, MariaDB, and PostgreSQL integration cells;
- EF CLI migrations, idempotent and no-transaction scripts, and Migration
  Bundles in every engine cell;
- deterministic double-pack, exact package contents, provider separation, and
  isolated package-only consumers; and
- SPDX 2.2 SBOM generation and validation.

The PostgreSQL EF tooling probe waits for the final TCP listener, never the
image's temporary socket-only initialization server. Large live performance
fixtures receive a fixture-only command timeout; production SafeMigrations
continues to use its normal configured timeout.

The qualified workflow artifact contains the six package archives,
`SHA256SUMS`, performance evidence, and the SPDX manifest. GitHub creates build
provenance and SBOM attestations for those exact bytes before publication can
reach the protected environment.

## Irreversible publication boundary

The `publish` job is the only job with NuGet OIDC and GitHub Release write
permissions. Environment protection keeps it waiting while all reversible
work is reviewed. After the operator pushes the signed annotated tag and
approves the same run, publication verifies:

- the candidate remains on protected `main` history;
- the tag peels to the qualified SHA and its SSH signature matches the
  repository's allowed-signers policy;
- package hashes and contents still match qualification; and
- the short-lived NuGet credential is issued for the exact repository,
  workflow, and `nuget` environment.

Primary and symbol packages are pushed separately with duplicate tolerance for
same-run recovery. Completion requires downloading each public primary package,
verifying its NuGet repository signature, and comparing every archive entry
with the qualified package after excluding only NuGet's `.signature.p7s`.

Before the NuGet credential is requested, the GitHub Release starts as a draft
with the expected title, Changelog-derived notes, classification, exact six
qualified package files, `SHA256SUMS`, and `manifest.spdx.json`. On a same-run
retry, matching uploaded assets are retained and missing assets are added; any
metadata, unexpected name, or SHA-256 digest conflict fails closed. Draft
discovery uses the authenticated, paginated Release inventory because GitHub's
tag endpoint returns published Releases only. The complete draft is read back
before the first NuGet push.

After signed NuGet content has been read back, the workflow publishes the
verified draft. It then waits for the published immutable state and GitHub's
automatically generated Release and asset attestations with a bounded retry
window. Prereleases are not marked latest; stable releases are. The signed tag
is the commit identity; `targetCommitish` is not treated as one after the tag
already exists.

## Recovery semantics

NuGet cannot publish three package IDs atomically. A network failure can occur
after one or more uploads are accepted. The supported recovery is rerunning the
failed `publish` job in the original run. Duplicate pushes are tolerated, but
signed public content and the staged or immutable GitHub Release must still
match exactly. Missing draft assets can be uploaded; conflicting assets are
never overwritten. A conflict is terminal. A timeout while waiting for the
platform-generated Release attestation is retryable only through the same
failed job; it never authorizes ignoring verification. Tags and published
versions are never moved, replaced, or reused.

## Evidence boundary

Actions logs, retained qualification artifacts, GitHub attestations, the
authorized tag, NuGet repository signatures, and immutable Release assets are
the evidence. The focused reconciliation script has no persisted release state;
it reads GitHub's current draft or immutable state on every invocation and has
command-contract tests for positive, conflict, retry, and timeout paths. The
repository does not maintain a second event-sourced release database. Hosted
configuration and an actual successful RC remain necessary evidence before a
stable release.
