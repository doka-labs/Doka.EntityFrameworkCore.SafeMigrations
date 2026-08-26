# Release process

This document owns SafeMigrations' release contract and qualification scope.
The [publication operations guide](operations/release-publication.md) is the
canonical maintainer procedure, including setup, commands, approval, readback,
and recovery. [Consumer verification](security/release-verification.md) is
separate.

SafeMigrations uses one manually dispatched workflow for every canonical
package version. It qualifies an untagged current `main` commit, attests the
qualified bytes, and waits at the protected `nuget` environment. Only then is
a signed annotated `v<version>` tag created and the publication approved.
The job validates that identity, stages and reads back the complete draft,
submits qualified bytes to NuGet, publishes and reads back the immutable GitHub
Release, then completes public NuGet signature/content/symbol verification.

The workflow has no push/tag trigger and no fixed release line. Failed
qualification before tagging consumes neither a tag nor a NuGet version.
RC and stable use the same complete qualification and three-package topology.

## One-time repository configuration

The canonical setup checklist and exact account/environment values are in
[one-time setup](operations/release-publication.md#one-time-setup).
The [settings handoff](runbooks/repository-settings.md) owns administrative
readback. A workflow declaration is not evidence that a hosted protection,
reviewer, signing identity, or publishing policy is active.

## Candidate preconditions

The synchronized package IDs are:

1. `Doka.EntityFrameworkCore.SafeMigrations`;
2. `Doka.EntityFrameworkCore.SafeMigrations.MySql`; and
3. `Doka.EntityFrameworkCore.SafeMigrations.PostgreSql`.

Every release uses the same version for all three primary and symbol packages.
Doka 10.0.0 is an ordinary pinned package dependency, not a cross-repository
release-approval gate. Locked restore and review cover the complete graph;
the package validator's `--require-stable-dependencies` option currently
checks Doka specifically, not every dependency.

[Version validation](../eng/release/validate-version.sh) accepts canonical
lowercase NuGet versions of at most 64 characters: `MAJOR.MINOR.PATCH` with
an optional prerelease suffix. The stable triplet must equal the reviewed
`VersionPrefix` in [source properties](../src/Directory.Build.props).
The changelog must contain exactly one dated entry for the full version.
A leading `v`, leading zeroes in numeric identifiers, uppercase prerelease
identifiers, build metadata, and a different source release line reject.

Initial preflight requires the requested tag to be absent, the dispatched SHA
to equal current `main`, and each NuGet primary endpoint to return a definitive
404 for that package/version. An authorization or service error is not absence.
The complete source/API/metadata/account checklist is in
[source preparation](operations/release-publication.md#1-prepare-reviewed-main).

## Start an untagged qualification

Run the [local preparation check](operations/release-publication.md#2-check-local-preparation),
then use [GitHub UI dispatch](operations/release-publication.md#3-start-qualification-and-wait).
Select `main` and the reviewed version without `v`; do not create the tag
before qualification.

## Qualification and protected wait

The reusable [quality workflow](../.github/workflows/quality-gates.yml)
must complete all of the following:

- locked restore, architecture, engineering/documentation/policy gates,
  warning-level style and imports checks, warning-free Release build,
  Core tests, and public API analysis;
- construction, generation, fingerprint, and serialization performance and
  allocation budgets, plus merged product coverage enforcement;
- six MySQL/MariaDB and five PostgreSQL live engine cells;
- EF CLI, normal, idempotent, no-transaction, and Migration Bundle paths in
  every engine cell;
- the isolated Latest EF/Npgsql dependency profile;
- deterministic double-pack, exact package file set, required archive
  entries/metadata, report-schema identity, provider separation, and
  package-only consumers; and
- Microsoft SBOM Tool generation and validation.

The package-content validator does not reject every possible additional
archive entry. Deterministic comparison and publication readback bind the
complete qualified content; source/package review still checks suitability.
Double-pack compares two packs of the same build, not independent builds by
different parties.

A separate attestation job downloads and verifies the qualified bytes, creates
build provenance and SBOM attestations, and preserves their Sigstore bundles.
The generated manifest is SPDX 2.2 and its attestation predicate is
`https://spdx.dev/Document/v2.2`; producer and verifier must agree.
The protected publication job may wait only after all prerequisites succeed.
Artifact names, contents, retention, attempt handling, and the operator's wait
check are owned by
[the operations guide](operations/release-publication.md#automated-checks-and-source-identity).

## Create the release identity

The tag must be annotated, SSH-signed by an authorized principal/key, and
directly identify the qualified commit. Both the reviewed repository signer
policy and GitHub's tag-signature verification must accept it. A valid signed
commit or an unrecognized signing key is insufficient.

The zero-argument local pre-tag check verifies clean current-main/signing
readiness before dispatch. After qualification, the operator checks the
selected run's version and exact captured commit, successful prerequisite
jobs, protected publication wait, and available qualification/attestation
artifacts. These are explicit operator checks, not automatic run discovery.

The runbook contains the tag commands directly. They recheck cleanliness,
source/version agreement and the exact candidate's continued ancestry on
refreshed protected `origin/main`, then create and verify the signed tag before
pushing only that tag. The source helper never fetches or overwrites local tags.
Publication independently rechecks the candidate and original qualified bytes.

The optional explicit version/commit/run-ID diagnostic additionally checks
the waiting run and unexpired artifacts through GitHub's API before tagging.
Successful package/attestation producers can have different attempts; the
diagnostic and publication retain their exact artifact identities rather
than requiring all to equal the current run attempt.
Use [tag creation](operations/release-publication.md#4-create-and-push-the-tag).

## Approve and publish

Publication requires explicit protected-environment approval. The job binds
the requested tag and exact checkout to its original qualified SHA, which must
remain an ancestor of freshly fetched protected `origin/main`. Initial preflight
requires current-main equality; unrelated later merges do not invalidate the
candidate or authorize publishing the later commit's bytes.

Qualified checksums, package contents, provenance and SBOM bundles are verified
against the exact repository, workflow, signer/source commit, `main` ref, and
hosted-runner constraints. Publication must not restore, rebuild, or repack.
The qualified bytes are the upload inputs.

The exact GitHub Release draft and all eleven immutable assets must be read
back before NuGet credential acquisition or a NuGet write: three `.nupkg`,
three `.snupkg`, `SHA256SUMS`, `SYMBOLS.json`, the SPDX 2.2 manifest, and both
attestation bundles. Staging, finalization, and retries use the same complete
set, specified in [consumer verification](security/release-verification.md#qualified-assets-and-provenance).

Credential-free preflight checks existing primary payloads and symbols; only
missing content permits OIDC login and push. Matching payloads awaiting a NuGet
repository signature are pending, not success or a payload conflict.
Duplicate-tolerant pushes do not establish success on their own.

After NuGet submission, the complete GitHub Release is published and read back
as immutable before final public NuGet verification. Repository signatures,
canonical package content, and public Portable PDB identity/checksums must all
pass before the workflow succeeds. A visible GitHub Release with pending NuGet
verification is a partial publication, not an accepted release.

`SIGNED_SHA256SUMS`, downloaded NuGet payloads, and attempt-specific observations
belong only to `safe-migrations-publication-<version>-<attempt>`, rooted at
`artifacts/release-publication` with payloads under `nuget-readback/`. Available
logs, GitHub staging/finalization results, summary, and payloads are retained
even on failure; they must never become additional GitHub Release assets.

For prerelease versions, `prerelease` is true and `make_latest` is false;
stable uses false and true respectively. The step order and operator approval
procedure are maintained in
[publication approval](operations/release-publication.md#5-approve-the-same-waiting-run).

## Candidate acceptance

Acceptance requires completed successful jobs, all three NuGet versions,
valid repository signatures, qualified package/PDB agreement, the correct
immutable GitHub Release and complete assets, and evidence bound to the same
version, source, workflow run, and actual producing attempts.

The actionable final readbacks and evidence-retention checklist are in
[completion](operations/release-publication.md#6-confirm-completion) and
[evidence retention](operations/release-publication.md#evidence-and-independent-verification).
Green local tests or documentation do not establish a successful hosted release.

## Failure and recovery

A tag or published NuGet version is never moved, replaced, or reused.
Recovery must preserve the original qualified identity and evidence, or
qualify a new version. After tagging, retry only failed publication work in the
same run. A matching immutable release is a read-only success; a conflict is
terminal. Source or release-tool fixes require a new reviewed candidate.
Expired artifacts and GitHub's 30-day rerun limit require maintainer recovery,
not repacking, moving tags, or bypassing verification.
The complete decision table and exact rerun procedure are maintained in
[recovery guide](operations/release-publication.md#recovery).

## Stable release

Stable is a distinct identity qualified through the same complete workflow,
not a renamed RC archive or a changed GitHub prerelease flag. API baseline
promotion, changelog preparation and the repeat publication procedure are in
[stable preparation](operations/release-publication.md#stable-preparation).

## Primary references

Operational platform semantics and their retrieval date are linked in the
[operations guide](operations/release-publication.md#maintenance-and-source-verification).
The contract additionally relies on:

- [NuGet package versioning](https://learn.microsoft.com/nuget/concepts/package-versioning)
- [NuGet symbol packages](https://learn.microsoft.com/nuget/create-packages/symbol-packages-snupkg)
- [NuGet package signatures](https://learn.microsoft.com/nuget/reference/signed-package-verification-options)
- [GitHub artifact attestations](https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations/using-artifact-attestations-to-establish-provenance-for-builds)
- [GitHub attestation verification](https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations/verifying-the-authenticity-of-artifacts)
- [GitHub reusable workflow outputs](https://docs.github.com/actions/how-tos/sharing-automations/reusing-workflows#using-outputs-from-a-reusable-workflow)
