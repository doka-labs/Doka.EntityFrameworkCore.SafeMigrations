---
id: D-006
status: implemented
date: 2026-08-26
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Candidate identity, qualification, protected publication, and release recovery"
supersedes: []
superseded-by: []
amends: []
amended-by: []
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-006 -- Qualify untagged source before protected exact-byte publication

## Context and Problem Statement

SafeMigrations will deliver Core, MySQL/MariaDB, and PostgreSQL packages with
one coordinated version. The first release candidate must exercise the real
publication workflow without consuming tags and package identities for
ordinary qualification failures.

A tag-triggered build creates an irreversible release identity before tests
finish. A separate publication rebuild can upload bytes different from the
ones qualified. A multi-package publish can also fail after only part of the
set becomes public; a retry must distinguish matching content from a conflict.

The decision is how to bind source, version, qualified artifacts, approval,
tagging, publication, and readback in one understandable release path.

## Decision Drivers

- The complete intended initial product version is 10.0.0; candidates qualify
  that full contract, not a planned reduced feature release.
- Dispatch selects main and an entered version; the reviewed source determines
  the permitted release line, not a hardcoded workflow tag.
- Reversible qualification precedes tag creation and NuGet publication.
- All packages and evidence bind to the same source/run identity.
- Publication uses qualified bytes without rebuilding product packages.
- Partial publication and response-loss recovery fail closed on conflicts.
- Hosted protection, signing authority, and external readback need real
  evidence rather than hand-written fixtures alone.

## Considered Options

- Manual untagged qualification with a protected publication wait
- Tag-triggered qualification and publication
- Independent qualification and publication workflows with a product rebuild

## Decision Outcome

Chosen option: "Manual untagged qualification with a protected publication wait",
because it postpones release identity until evidence exists and keeps approval,
artifact identity, and recovery in the same workflow run.

The operator selects main and a canonical version in the release-candidate
workflow. Preflight requires current main, source/changelog agreement, an
unused tag, and definitive absence of the requested NuGet versions. The
release line derives from VersionPrefix; examples in documentation are not
hardcoded release identities.

The shared quality workflow performs the full package, engine, tooling,
dependency-profile, coverage, performance, and SBOM qualification.
Attempt-qualified artifacts preserve the exact packages and evidence.
Attestations bind the qualified bytes to their build identity.

Only then does publication wait at the protected nuget environment. The
operator verifies the qualified run, creates the authorized signed annotated
tag on its exact commit, and approves the waiting job. Publication rechecks
the exact candidate checkout, continued ancestry on refreshed protected main,
tag identity/signature, artifacts, and attestations before obtaining publication
credentials. Initial preflight requires current-main equality; unrelated later
merges do not invalidate the original qualified candidate. Successful producer
jobs retain their exact artifact identities across attempts; equality with one
global run attempt is not required.

The publish job does not rebuild or repack product packages. It stages and
reads back the complete GitHub Release draft before any NuGet write. The fixed
eleven-asset set consists of the three primary and three symbol packages,
qualified package and symbol manifests, SPDX 2.2 SBOM, and two attestation
bundles. Its membership is identical for staging, finalization, and retry.
The SBOM attestation predicate agrees with the producer's SPDX 2.2 manifest.

Credential-free preflight compares existing NuGet content. Only missing content
permits short-lived publishing authority and submission of qualified payloads.
An exact existing payload is an idempotent recovery case; conflicting bytes
are terminal. Matching content awaiting its repository signature is pending,
not complete verification.

After NuGet submission, the already complete GitHub Release is published and
read back as immutable before public NuGet verification completes. Final
readback requires valid repository signatures, canonical package content, and
public Portable PDB identity/checksums. NuGet's repository-signature entry is
distinguished from the originally qualified package content. GitHub visibility
while NuGet is pending is an explicit partial state, not release completion.

Attempt-specific observations, downloaded public payloads, logs, GitHub
staging/finalization results, summary, and generated SIGNED_SHA256SUMS remain in
the retained publication-attempt artifact, including available failure evidence.
They never extend the immutable GitHub asset set. SIGNED_SHA256SUMS hashes
repository-signed packages and public PDBs but is not itself a signature.

RC and stable releases use the same workflow and controls. Stable output is
newly qualified for its own version and API baseline; an RC archive is not
renamed into a stable package. Doka 10.0.0 is a qualified stable package input,
not an external SafeMigrations release-approval gate.

### Consequences

- Good, because failed qualification before tagging consumes neither a tag nor
  a NuGet version.
- Good, because source, artifacts, approval, and retry semantics remain within
  a single qualified run instead of a loosely linked producer/consumer pair.
- Bad, because the three NuGet packages cannot be published as one atomic
  transaction; partial success must be reconciled and may already be visible.
- Bad, because removal from protected source history, expired evidence, signing
  failures, or missing hosted protections can invalidate a waiting candidate
  and require a new qualification before tagging.
- Bad, because an immutable GitHub Release may already be public when NuGet
  verification fails; completion evidence and same-identity recovery remain
  necessary after that irreversible boundary.

### Confirmation

Run the Python engineering suite and
`node --test eng/tests/github-release.test.js`. Require positive and negative
coverage for version parsing, exact-candidate/ancestry binding, authorized signing,
existing package/signature/symbol conflicts, draft asset reconciliation,
partial retries, unchanged local tag refs, distinct successful producer
attempts, matching-payload/pending-signature handling, retained failure
diagnostics, and prerelease/latest metadata.

Run package qualification and the shared quality workflow for the candidate.
Inspect the actual GitHub run and job metadata used by the workflow; fixtures
must reflect the producer contract, not merely duplicate validator assumptions.

Before any first publication, read back repository and environment controls
using the repository-settings runbook. Then the actual candidate must prove
the protected wait before tagging, authorized tag binding, exact-byte upload,
signature and symbol readback, and final Release contents.
The complete eleven-asset draft must precede NuGet writes, and immutable Release
readback must precede successful final NuGet verification.

Local tests do not establish configured approval protection, successful OIDC
exchange, or public package availability. Double-pack determinism does not
establish reproducibility across independent build environments. This ADR
does not authorize dispatch, tagging, credential creation, or publication.

## Pros and Cons of the Options

### Manual untagged qualification with a protected publication wait

- Good, because the operator sees qualified evidence before creating an
  immutable identity and publication consumes exactly those artifacts.
- Bad, because the operator must bind the correct waiting run and commit;
  repository protections and identity readback are part of correctness.

### Tag-triggered qualification and publication

- Good, because the tag is a familiar entry point and fixes a source identity
  immediately for conventional release systems.
- Bad, because a qualification failure already consumes a release tag, contrary
  to this project's stated candidate workflow.

### Independent qualification and publication workflows with a product rebuild

- Good, because separate workflows can have independent schedules, ownership,
  and credential boundaries.
- Bad, because a rebuild no longer publishes the exact qualified bytes and
  introduces an additional run/artifact contract to secure and recover.

## More Information

The canonical release runbook owns commands and one-time hosted configuration.
This record explains the decision rather than maintaining a second procedure.

After a valid tag exists, recovery uses failed publication jobs in the same
run, not a new full qualification that requires tag absence. Tags are not moved,
reused, or deleted to hide a failed publication. Evidence that no remote
mutation occurred is different from assuming a failed request had no effect.
An already published matching immutable release is accepted read-only after
complete metadata and asset verification. A conflict is terminal. Source or
release-tool fixes require a new reviewed candidate rather than a same-run
retry. Expired artifacts, GitHub's 30-day rerun window, or exhausted rerun limits
require maintainer recovery or a new candidate; they do not permit repacking
or bypassing identity checks.

### Re-evaluation Triggers

- GitHub or NuGet changes workflow identity, OIDC claims, artifact APIs,
  signature behavior, or package/symbol readback.
- A real recovery incident reveals ambiguity in source, artifact, or partial
  publication identity.
- Required hosted controls become unavailable under the repository's actual
  plan or ownership model.

### Decision History

- 2026-08-26: Decision recorded with status proposed.
- 2026-08-26: Existing untagged candidate workflow documented retrospectively without claiming a completed hosted release.
- 2026-08-26: Doka-format revision adds alternatives, non-atomic publication, recovery constraints, and local-versus-hosted evidence boundaries.
- 2026-08-26: Maintainer designated @doka-labs/core-maintainers as the informed audience.
- 2026-08-26: Aligned the immutable eleven-asset boundary, protected-main ancestry, producer attempts, and retained publication diagnostics.
- 2026-08-26: Status changed from proposed to accepted. Dominic Kalkbrenner confirmed the recorded decision and its existing implementation.
- 2026-08-26: Status changed from accepted to implemented. The candidate workflow and publication tooling are implemented and locally regression-tested. Actual hosted qualification and publication remain per-release gates, not evidence supplied by this status change.

### Implementation References

- [Release candidate workflow](../../.github/workflows/release-candidate.yml)
- [Shared qualification workflow](../../.github/workflows/quality-gates.yml)
- [Version contract](../../eng/release/version_contract.py)
- [Pre-tag readback](../../eng/release/pre-tag-check.sh)
- [Local tag verification](../../eng/release/verify-tag.sh)
- [Attestation verification](../../eng/release/verify-attestations.sh)
- [GitHub Release reconciliation](../../eng/release/github-release.js)
- [NuGet publication](../../eng/publish-nuget.sh)
- [NuGet readback](../../eng/readback-nuget.sh)
- [Version tests](../../eng/tests/test_release_version.py)
- [Tag-contract tests](../../eng/tests/test_release_tag_contract.py)
- [Attestation contract tests](../../eng/tests/test_release_attestations.py)
- [NuGet publication tests](../../eng/tests/test_nuget_publication.py)
- [NuGet readback tests](../../eng/tests/test_nuget_readback.py)
- [Release reconciliation tests](../../eng/tests/github-release.test.js)
- [Release process](../release-process.md)
- [Publication operations](../operations/release-publication.md)
- [Hosted settings handoff](../runbooks/repository-settings.md)
- [Consumer release verification](../security/release-verification.md)

### Sources

- [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) (primary source; retrieved 2026-08-26)
- [GitHub attestation verification and identity constraints](https://cli.github.com/manual/gh_attestation_verify) (primary source; retrieved 2026-08-26)
- [GitHub immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases) (primary source; retrieved 2026-08-26)
- [GitHub rerun identity and limits](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs) (primary source; retrieved 2026-08-26)
- [NuGet validation and indexing](https://learn.microsoft.com/en-us/nuget/nuget-org/publish-a-package#package-validation-and-indexing) (primary source; retrieved 2026-08-26)
