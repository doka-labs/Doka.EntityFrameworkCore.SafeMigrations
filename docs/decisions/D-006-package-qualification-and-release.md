---
id: D-006
status: implemented
date: 2026-08-26
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Candidate qualification, signed release identity, and protected publication"
supersedes: []
superseded-by: []
amends: []
amended-by: []
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-006 -- Qualify untagged source before protected publication

## Context and Problem Statement

SafeMigrations publishes Core, MySQL/MariaDB, PostgreSQL, and SQLite packages at
one version. Ordinary qualification failures must not consume a tag or public
NuGet version, and publication must use the exact bytes that passed the full
provider matrix. The workflow must also remain understandable and maintainable
without a repository-owned release orchestration framework.

## Decision Drivers

- Complete reversible qualification must precede the release tag.
- One protected job must own all irreversible writes.
- Publication must use qualified bytes without rebuilding.
- Consumers must receive portable, cryptographically verifiable provenance for
  every immutable Release asset selected as a build subject.
- Credentials must be short-lived and scoped to the exact workflow/environment.
- Partial multi-package publication must have a fail-closed recovery path.
- Dependency updates must not leave only a subset of project lockfiles current.
- GitHub, Git, .NET, and NuGet platform contracts should replace duplicated
  repository state machines and hand-written API fixtures.

## Considered Options

- Manual untagged qualification with a protected publication wait
- Tag-triggered qualification and publication
- Separate build and publication workflows with a rebuild
- A repository-owned publication transaction and reconciliation engine

## Decision Outcome

Chosen option: "Manual untagged qualification with a protected publication
wait" using platform-native publication primitives.

The operator dispatches `release-candidate.yml` from `main` with a version.
Preflight validates the source release line, changelog, current SHA, tag
absence, and unused NuGet identities. The shared quality workflow performs all
build, test, provider, tooling, coverage, performance, package, consumer, and
SBOM gates. GitHub attests those exact artifacts. The attestation job validates
the resulting Sigstore bundle and retains it as the canonical single-record
`release-provenance.intoto.jsonl` workflow artifact.

The only write-capable job waits at environment `nuget`. After the operator
creates the authorized signed annotated tag on the qualified SHA and approves
that same run, the job verifies source, tag, signature, hashes, and package
contents. `NuGet/login` exchanges OIDC for a short-lived key. `dotnet nuget
push` publishes the primary and symbol packages. Public primary packages must
then have valid NuGet repository signatures and match every qualified archive
entry except NuGet's added `.signature.p7s`.

Before requesting the NuGet credential, the protected job downloads the exact
same-run provenance artifact, validates its SLSA v1 envelope and complete
ten-subject name/digest inventory, and verifies every subject with
`gh attestation verify --bundle` pinned to the repository, release workflow,
workflow commit, source ref, source commit, and hosted-runner boundary. It then
creates a GitHub Release draft with the expected title, Changelog-derived
notes, classification, exact eight package files, `SHA256SUMS`, the SPDX
manifest, and `release-provenance.intoto.jsonl`. A retry retains digest-matching
assets, uploads missing assets, and rejects every mismatch. Only a completely
read-back eleven-asset draft permits the first NuGet push.

After signed public NuGet content matches the qualified packages, the job
publishes the draft. GitHub's immutable-release and release-asset verification
then supply the release association and digest checks. Because GitHub generates
the Release attestation asynchronously, verification uses a bounded readback
window and fails closed after exhaustion. The focused adapter persists no
parallel release state and does not parse the symbol server.

### Repository dependency lock scope

Central Package Management owns dependency floors and compatible ranges for the
entire repository. Only the four publishable package projects commit
`packages.lock.json`. Their lockfiles preserve the exact compile-time package
graph and NuGet content hashes used to produce and qualify release artifacts;
they do not control the graph selected by a consuming application.

Test, benchmark, sample, and engineering projects intentionally resolve the
central declarations without committed lockfiles. This differs from NuGet's
recommendation to commit lockfiles for executable projects at the start of a
dependency chain. It also means those projects do not retain lockfile
`contentHash` validation or expose every transitive resolution change as a pull
request diff.

The limitation is accepted because maintaining one lockfile for each of the 20
projects caused grouped Dependabot updates to regenerate only a subset and made
otherwise coherent dependency pull requests fail locked restore. This upstream
behavior is tracked by dependabot-core issue 13950 for Central Package
Management, project references, and locked mode. The remaining controls are one
cleared NuGet source, central non-floating declarations, lowest-applicable-
version resolution, warnings as errors, automatic dependency snapshots,
Dependency Review, full provider and engineering execution, package content
verification, and an SPDX SBOM. These controls test the resolved graph; they do
not recreate the omitted lockfile guarantees.

A workflow that runs `dotnet restore --force-evaluate` and commits regenerated
lockfiles back to Dependabot branches was considered and rejected. It would
give dependency-validation automation repository write access and make CI
mutate bot-authored pull requests. The smaller lock scope keeps validation
read-only while the upstream regeneration defect remains open.

### Consequences

- Good, because failed qualification consumes no release identity.
- Good, because CI and release share the same complete quality workflow.
- Good, because the trusted-publishing and immutable-release boundaries are
  implemented by their owning platforms.
- Good, because an interrupted GitHub asset upload resumes from a verified
  draft without overwriting conflicting public evidence.
- Good, because the complete Release draft exists before the first irreversible
  NuGet write and attestation visibility is handled as a bounded readback.
- Good, because consumers can verify the exact SLSA provenance bundle shipped
  inside the immutable Release instead of depending only on GitHub API state.
- Good, because the engineering surface is smaller and has fewer contracts
  that can disagree with GitHub or NuGet.
- Good, because grouped dependency updates cannot leave a hidden subset of
  engineering lockfiles stale.
- Bad, because four NuGet package IDs cannot be published atomically.
- Bad, because symbol indexing is asynchronous and remains a NuGet-hosted
  validation state after upload.
- Bad, because GitHub and NuGet still cannot commit atomically; recovery after
  the first NuGet write remains an idempotent same-job reconciliation.
- Bad, because hosted environment, ruleset, immutable-release, and NuGet policy
  settings cannot be proven by local tests.
- Bad, because non-package projects lose lockfile content-hash verification and
  pull-request visibility for purely transitive resolution changes.

### Confirmation

Require local shell syntax checks, portable-provenance and GitHub Release
reconciliation positive/negative cases, version-validator positive/negative
cases, locked restore of the four package projects, resolved restore of all
execution projects, format, Release build, all test suites, coverage thresholds,
performance budgets, deterministic package qualification, package-only
consumers, SBOM validation, and every supported live provider/tooling cell.
Locked restore covers every platform-neutral CI restore of the package
projects. EF Migration Bundles publish for the runner RID, which the
platform-neutral lockfiles cannot record, so those RID-specific restores run
unlocked inside isolated repository copies.

The first actual RC must additionally prove the hosted protected wait, OIDC
exchange, authorized tag verification, public signed-package readback,
successful symbol validation, exact immutable Release assets, portable
provenance readback, and artifact attestations. Local fixtures do not substitute
for that hosted evidence.

## Pros and Cons of the Options

### Manual untagged qualification with protected publication wait

- Good, because the operator reviews completed evidence before creating the
  immutable identity.
- Bad, because the operator must preserve the exact run/SHA relationship.

### Tag-triggered qualification and publication

- Good, because a tag is a conventional workflow trigger.
- Bad, because every test failure consumes a release identity.

### Separate build and publication with a rebuild

- Good, because deployment responsibilities are visibly separate.
- Bad, because published bytes are no longer the qualified bytes.

### Repository-owned transaction and reconciliation engine

- Good, because it can model every observed partial state explicitly.
- Bad, because it duplicates platform contracts, requires extensive fixture
  maintenance, and adds more failure modes than the four-package release needs.

## More Information

The [release process](../release-process.md) owns the technical contract. The
[publication runbook](../operations/release-publication.md) owns the exact
operator sequence. The [verification guide](../security/release-verification.md)
owns independent consumer readback.

### Re-evaluation Triggers

- GitHub or NuGet changes environment approval, OIDC, attestation, immutable
  Release, repository-signature, or symbol publication semantics.
- The package family stops sharing one version or grows beyond the existing
  non-atomic recovery model.
- An actual release incident reveals ambiguous identity or recovery behavior.
- NuGet adds a repository-wide lock mechanism, or dependabot-core issue 13950
  is resolved and Dependabot reliably regenerates every affected project
  lockfile in one grouped pull request.
- A second package source, a floating declaration, or a restore-resolution
  incident weakens the current centrally declared single-source graph.
- Dependency Review or SBOM evidence no longer exposes the resolved engineering
  graph required by the qualification workflow.

### Decision History

- 2026-08-26: Decision implemented for the first complete release candidate.
- 2026-08-26: Implementation simplified to platform-native publication while
  preserving untagged qualification, exact-byte publication, and protected
  approval.
- 2026-08-29: The rc.2 run exposed delayed immutable-release attestation
  visibility after NuGet and GitHub publication. Draft staging now precedes the
  first NuGet write, and Release plus asset verification uses bounded retry.
- 2026-08-29: Review of the published 10.0.0 line exposed that API-hosted
  attestations were not retained as a portable Release asset. Future releases
  now publish and independently verify the exact `actions/attest` Sigstore
  bundle as `release-provenance.intoto.jsonl` before requesting NuGet OIDC.
- 2026-09-18: D-013 expanded qualification, exact-byte publication,
  reconciliation, SBOM, and readback from three to four version-aligned NuGet
  package IDs for the SQLite provider release.
- 2026-09-21: Limited committed lockfiles to the four publishable package
  projects after grouped Dependabot updates left a subset of 20 project
  lockfiles stale. Recorded the lost execution-project content-hash and
  transitive-diff guarantees, compensating controls, and re-evaluation triggers.
- 2026-09-21: The former root-level locked-restore condition was evaluated
  before `ContinuousIntegrationBuild` was defined, so only explicit
  `--locked-mode` restores had been locked. Moving it behind the root import
  activated it and made the RID-specific EF Migration Bundle restores fail;
  locked restore now excludes restores with a `RuntimeIdentifier`.

### Implementation References

- [Release candidate workflow](../../.github/workflows/release-candidate.yml)
- [Shared quality workflow](../../.github/workflows/quality-gates.yml)
- [Version validation](../../eng/validate-release-version.sh)
- [Package qualification](../../eng/qualify-packages.sh)
- [NuGet readback](../../eng/readback-nuget.sh)
- [Portable provenance](../../eng/release-provenance.sh)
- [GitHub Release reconciliation](../../eng/reconcile-github-release.sh)
- [Allowed signers](../../eng/release/allowed-signers)
- [Publication operations](../operations/release-publication.md)

### Sources

- [GitHub deployments and environments](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments) (primary source; retrieved 2026-08-26)
- [GitHub artifact attestations](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations) (primary source; retrieved 2026-08-26)
- [`actions/attest` bundle output](https://github.com/actions/attest#outputs) (primary source; retrieved 2026-08-29)
- [GitHub offline attestation verification](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/verify-attestations-offline) (primary source; retrieved 2026-08-29)
- [GitHub immutable Releases](https://cli.github.com/manual/gh_release_create) (primary source; retrieved 2026-08-26)
- [GitHub immutable Release concepts](https://docs.github.com/en/enterprise-cloud@latest/code-security/concepts/supply-chain-security/immutable-releases) (primary source; retrieved 2026-08-29)
- [GitHub REST release inventory](https://docs.github.com/en/rest/releases/releases?apiVersion=2022-11-28#list-releases) (primary source; retrieved 2026-08-29)
- [OpenSSF Signed-Releases check](https://github.com/ossf/scorecard/blob/main/docs/checks.md#signed-releases) (primary source; retrieved 2026-08-29)
- [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) (primary source; retrieved 2026-08-26)
- [`dotnet nuget push`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-push) (primary source; retrieved 2026-08-26)
- [NuGet PackageReference lockfile guidance](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files#locking-dependencies) (primary source; retrieved 2026-09-21)
- [dependabot-core issue 13950](https://github.com/dependabot/dependabot-core/issues/13950) (primary upstream issue; retrieved 2026-09-21)
