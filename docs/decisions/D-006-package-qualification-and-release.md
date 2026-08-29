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

SafeMigrations publishes Core, MySQL/MariaDB, and PostgreSQL packages at one
version. Ordinary qualification failures must not consume a tag or public
NuGet version, and publication must use the exact bytes that passed the full
provider matrix. The workflow must also remain understandable and maintainable
without a repository-owned release orchestration framework.

## Decision Drivers

- Complete reversible qualification must precede the release tag.
- One protected job must own all irreversible writes.
- Publication must use qualified bytes without rebuilding.
- Credentials must be short-lived and scoped to the exact workflow/environment.
- Partial multi-package publication must have a fail-closed recovery path.
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
SBOM gates. GitHub attests those exact artifacts.

The only write-capable job waits at environment `nuget`. After the operator
creates the authorized signed annotated tag on the qualified SHA and approves
that same run, the job verifies source, tag, signature, hashes, and package
contents. `NuGet/login` exchanges OIDC for a short-lived key. `dotnet nuget
push` publishes the primary and symbol packages. Public primary packages must
then have valid NuGet repository signatures and match every qualified archive
entry except NuGet's added `.signature.p7s`.

Before requesting the NuGet credential, the job creates a GitHub Release draft
with the expected title, Changelog-derived notes, classification, exact six
package files, `SHA256SUMS`, and the SPDX manifest. A retry retains
digest-matching assets, uploads missing assets, and rejects every mismatch.
Only a completely read-back draft permits the first NuGet push.

After signed public NuGet content matches the qualified packages, the job
publishes the draft. GitHub's immutable-release and release-asset verification
then supply the release association and digest checks. Because GitHub generates
the Release attestation asynchronously, verification uses a bounded readback
window and fails closed after exhaustion. The focused adapter persists no
parallel release state and does not transport attestation bundles or parse the
symbol server.

### Consequences

- Good, because failed qualification consumes no release identity.
- Good, because CI and release share the same complete quality workflow.
- Good, because the trusted-publishing and immutable-release boundaries are
  implemented by their owning platforms.
- Good, because an interrupted GitHub asset upload resumes from a verified
  draft without overwriting conflicting public evidence.
- Good, because the complete Release draft exists before the first irreversible
  NuGet write and attestation visibility is handled as a bounded readback.
- Good, because the engineering surface is smaller and has fewer contracts
  that can disagree with GitHub or NuGet.
- Bad, because three NuGet package IDs cannot be published atomically.
- Bad, because symbol indexing is asynchronous and remains a NuGet-hosted
  validation state after upload.
- Bad, because GitHub and NuGet still cannot commit atomically; recovery after
  the first NuGet write remains an idempotent same-job reconciliation.
- Bad, because hosted environment, ruleset, immutable-release, and NuGet policy
  settings cannot be proven by local tests.

### Confirmation

Require local shell syntax checks, GitHub Release reconciliation
positive/negative cases, version-validator positive/negative cases,
locked restore, format, Release build, all test suites, coverage thresholds,
performance budgets, deterministic package qualification, package-only
consumers, SBOM validation, and every supported live provider/tooling cell.

The first actual RC must additionally prove the hosted protected wait, OIDC
exchange, authorized tag verification, public signed-package readback,
successful symbol validation, exact immutable Release assets, and artifact
attestations. Local fixtures do not substitute for that hosted evidence.

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
  maintenance, and adds more failure modes than the three-package release needs.

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

### Decision History

- 2026-08-26: Decision implemented for the first complete release candidate.
- 2026-08-26: Implementation simplified to platform-native publication while
  preserving untagged qualification, exact-byte publication, and protected
  approval.
- 2026-08-29: The rc.2 run exposed delayed immutable-release attestation
  visibility after NuGet and GitHub publication. Draft staging now precedes the
  first NuGet write, and Release plus asset verification uses bounded retry.

### Implementation References

- [Release candidate workflow](../../.github/workflows/release-candidate.yml)
- [Shared quality workflow](../../.github/workflows/quality-gates.yml)
- [Version validation](../../eng/validate-release-version.sh)
- [Package qualification](../../eng/qualify-packages.sh)
- [NuGet readback](../../eng/readback-nuget.sh)
- [GitHub Release reconciliation](../../eng/reconcile-github-release.sh)
- [Allowed signers](../../eng/release/allowed-signers)
- [Publication operations](../operations/release-publication.md)

### Sources

- [GitHub deployments and environments](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments) (primary source; retrieved 2026-08-26)
- [GitHub artifact attestations](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations) (primary source; retrieved 2026-08-26)
- [GitHub immutable Releases](https://cli.github.com/manual/gh_release_create) (primary source; retrieved 2026-08-26)
- [GitHub immutable Release concepts](https://docs.github.com/en/enterprise-cloud@latest/code-security/concepts/supply-chain-security/immutable-releases) (primary source; retrieved 2026-08-29)
- [GitHub REST release inventory](https://docs.github.com/en/rest/releases/releases?apiVersion=2022-11-28#list-releases) (primary source; retrieved 2026-08-29)
- [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) (primary source; retrieved 2026-08-26)
- [`dotnet nuget push`](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-nuget-push) (primary source; retrieved 2026-08-26)
