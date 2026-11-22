---
id: D-006
status: implemented
date: 2026-11-22
decision-makers: ["Dominic Kalkbrenner"]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Candidate qualification, signed release identity, and protected publication"
supersedes: []
superseded-by: []
amends: []
amended-by: [D-013, D-018]
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-006 -- Qualify untagged source before protected publication

## Context and Problem Statement

SafeMigrations publishes Core and provider packages at one version. The SQL
Server provider extends the release set to five packages. Ordinary qualification
failures must not consume a tag or public
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

Chosen option: "Manual untagged qualification with a protected publication wait",
because qualification must finish before the protected publisher consumes the
exact reviewed bytes through platform-native publication primitives.

The operator dispatches `release-candidate.yml` from an explicitly authorized
release line with a version. [D-018](D-018-release-lines-and-monotonic-publication.md)
extends the original main-only process without replacing its publisher.
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
twelve-subject name/digest inventory, and verifies every subject with
`gh attestation verify --bundle` pinned to the repository, release workflow,
workflow commit, source ref, source commit, and hosted-runner boundary. It then
creates a GitHub Release draft with the expected title, Changelog-derived
notes, classification, exact ten package files, `SHA256SUMS`, the SPDX
manifest, and `release-provenance.intoto.jsonl`. A retry retains digest-matching
assets, uploads missing assets, and rejects every mismatch. Only a completely
read-back thirteen-asset draft permits the first NuGet push.

After signed public NuGet content matches the qualified packages, the job
publishes the draft. GitHub's immutable-release and release-asset verification
then supply the release association and digest checks. Because GitHub generates
the Release attestation asynchronously, verification uses a bounded readback
window and fails closed after exhaustion. The focused adapter persists no
parallel release state and does not parse the symbol server.

### Release-line authority

Qualification freezes its own source/toolchain and exact candidate artifacts.
It does not grant perpetual publication rights. The existing protected publisher
independently checks current protected-main policy, selected branch protection
and the existing nuget environment. Prepared/qualification-only runs skip that
environment-bearing job. All lines share one non-cancelling publication queue;
same-run retries retain their original admission and bytes. Stable Latest is
monotonic by canonical SemVer; historical verification does not require a release
to remain Latest. The .NET 11 source branch prepares the complete main/11 and
maintenance/10 active target with six Dependabot entries in one reviewed PR.
Maintenance qualification precedes its separately approved environment grant;
the complete release and evidence gates remain unchanged.
Separate immutable workflow receipts do
not expand the twelve provenance subjects or thirteen public Release assets.

### Repository dependency lock scope

Central Package Management owns the reviewed dependency declarations. All 24
repository projects commit their default source-mode `packages.lock.json`, not
only the five shipping projects. The locks retain resolved identities and content
hashes for tests, samples, benchmarks and engineering consumers as well as the
shipping libraries. A library's repository lockfile does not lock its consumers.

The root defines CI identity before evaluating locked mode. Every platform-neutral
CI source-mode restore uses the committed lock for its actual project. RID-specific
publishes, explicit compatibility-matrix graphs and isolated package-consumer
profiles retain their separately qualified restore behavior; they must not rewrite
or pretend to validate the default source-mode locks. Local dependency edits update
all affected locks for review. Grouped dependency updates must leave all 24 graphs
consistent; the historical smaller lock scope below is no longer the current rule.

Transitive pinning remains enabled only for nonshipping projects. Shipping provider
projects publish explicit owner-specific minimum references: MySqlConnector 2.6.2
belongs only to MySQL/MariaDB; SqlClient 7.1.2 and PKCS 11.0.0 belong only to SQL
Server. Disabling shipping transitive promotion must not remove these existing
consumer floors. Core and unrelated providers must not acquire those dependencies.
Actual nuspec, lock, isolated-consumer and SBOM checks qualify the resulting graph.

The four adapters consume Core internals and therefore publish an exact dependency
on their matching SafeMigrations Core package version, including candidate versions.
This private-ABI constraint does not pin external EF or provider dependencies to
one patch. Package consumers must restore matching own-package identities and reject
conflicting Core selections rather than silently mixing private contracts.

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
- Good, because all default repository graphs retain reviewed identities and
  content hashes; grouped updates cannot silently omit engineering resolutions.
- Bad, because five NuGet package IDs cannot be published atomically.
- Bad, because symbol indexing is asynchronous and remains a NuGet-hosted
  validation state after upload.
- Bad, because GitHub and NuGet still cannot commit atomically; recovery after
  the first NuGet write remains an idempotent same-job reconciliation.
- Bad, because hosted environment, ruleset, immutable-release, and NuGet policy
  settings cannot be proven by local tests.
- Bad, because grouped updates now require regenerating and reviewing every
  affected repository lock; exact private-ABI versions reject mixed own-package sets.

### Confirmation

Require local shell syntax checks, portable-provenance and GitHub Release
reconciliation positive/negative cases, version-validator positive/negative
cases, locked restore of all 24 default source-mode projects, format, Release
build, all test suites, coverage thresholds, deterministic benchmark workload
contracts, explicitly requested BenchmarkDotNet execution/export, deterministic
package qualification, package-only consumers, SBOM validation, and every
supported live provider/tooling cell.
Locked restore covers every platform-neutral CI restore of all 24 source-mode
projects. EF Migration Bundles publish for the runner RID, which the
platform-neutral lockfiles cannot record, so those RID-specific restores run
unlocked inside isolated repository copies.

The first actual RC must additionally prove the hosted protected wait, OIDC
exchange, authorized tag verification, public signed-package readback,
successful symbol validation, exact immutable Release assets, portable
provenance readback, and artifact attestations. Local fixtures do not substitute
for that hosted evidence.

## Pros and Cons of the Options

### Manual untagged qualification with a protected publication wait

- Good, because the operator reviews completed evidence before creating the
  immutable identity.
- Bad, because the operator must preserve the exact run/SHA relationship.

### Tag-triggered qualification and publication

- Good, because a tag is a conventional workflow trigger.
- Bad, because every test failure consumes a release identity.

### Separate build and publication workflows with a rebuild

- Good, because deployment responsibilities are visibly separate.
- Bad, because published bytes are no longer the qualified bytes.

### A repository-owned publication transaction and reconciliation engine

- Good, because it can model every observed partial state explicitly.
- Bad, because it duplicates platform contracts, requires extensive fixture
  maintenance, and adds more failure modes than the five-package release needs.

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

- 2026-08-26: Decision recorded with status implemented.
- 2026-08-26: Existing implementation supports the first complete release candidate.
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
- 2026-09-30: Expanded the release set to five version-aligned package IDs for
  SQL Server. The five-package contract applies to the provider's first release;
  the 10.4.5 release contract remains the four-package historical set.
- 2026-10-02: Made duration and allocation benchmark overruns informational in
  the shared CI/release workflow. Keep all five benchmark sets, unchanged numeric
  comparison limits, measurements and failed JSON verdicts. The shared runner's
  explicit report-only mode changes only the exit policy of a complete report;
  configuration, execution and report-write failures remain fatal. Strict manual
  comparison and all product-correctness qualification gates remain unchanged.
- 2026-10-05: Prepare the shared stable 10.4.6 identity for compatible maintenance
  of the existing providers and the first optional SQL Server package. Promote
  the additive Core and SQL Server API inventories without rewriting earlier
  signatures or migration history. Require the complete five-package matrix,
  provenance, SBOM, primary/symbol publication, and public readback before a
  publication claim. Releases 10.4.2 through 10.4.5 remain four-package sets.
- 2026-10-06: Prepare stable 10.4.7 at the same five-package identity for
  MariaDB analysis and MySQL/MariaDB runtime performance changes. Preserve
  dependency ranges, API baselines, migration source, policies, report schemas,
  and history. Keep the complete qualification and publication gates;
  performance measurements remain informational rather than release blockers.
- 2026-10-06: Prepare stable 10.4.8 at the same five-package identity for explicit
  column-transition, ordered-projection and provider correctness fixes. Preserve
  dependency ranges, API baselines, migration source, policies, report schemas
  and history. Historical 10.4.7 qualification evidence is not new patch evidence;
  require the full qualification, provenance and public readback gates again.
- 2026-10-08: Prepare stable 10.4.9 at the same five-package identity for proven
  SQL Server integer widening/CHECK validation, PostgreSQL column contracts,
  trigger-aware ordered evidence, lossless SQLite repairs, and bounded
  PostgreSQL/SQL Server work and retention reductions. Preserve dependency
  ranges, API baselines, migration source, policy selection, report schemas and
  history. Local tests, ARM diagnostics and deterministic packing do not replace
  the full native engine, tooling, coverage, provenance and public readback gates.
- 2026-10-13: Prepare stable 10.4.10 at the same five-package identity for ordered
  proof, concurrent required-add/delete, schema-identity and session-recovery
  fixes. Preserve production dependency ranges, public APIs, migration source,
  policies, reports and history. Inherit reviewed engineering dependencies on
  `main`; keep native p95 and cancellation latency informational with unchanged
  comparisons. Correctness, invalid evidence, execution and artifact failures
  remain blocking, as do the full hosted/native qualification and readback gates.
- 2026-10-14: Prepare stable 10.4.11 at the same five-package identity for sparse
  live/ordered prerequisites, native owner/supporting-index/backfill contracts,
  PostgreSQL CHECK/index-input qualification and SQLite key/capacity/order fixes.
  Retain public signatures, production dependency ranges, migration source,
  policies, fingerprint formats, reports and history. Keep native SQL Server
  x86-64, full coverage/tooling, exact packages, provenance, SBOM and public
  readback mandatory; local source preparation does not establish publication.

- 2026-10-15: Prepare stable 10.5.0 at the same five-package identity for
  actionable diagnostics, optional MariaDB native UUID through Doka 10.5,
  exact persisted defaults and isolated parallel provider tests. Promote the
  prepared diagnostic API baseline and explicitly document report schema 4/view 3,
  the owned MySQL/MariaDB exception, Doka range and default reclassification.
  Preserve migration source, policies, fingerprint formats and history; retain
  native qualification, coverage, tooling, provenance, SBOM and public readback.

- 2026-10-21: Set the stable 10.6.0 release contract at the shared five-package
  identity for additive public CASE APIs, certified generated/CHECK contracts, independent
  DROP and stack-safe traversal fixes, and the reviewed Doka/EF dependency
  baseline. Promote the nine CASE declarations and retain report schema 4/view 3,
  diagnostic transport and historical migration source. Document conditional
  contract-fingerprint reapproval and exact expression identity. Retain the full
  native matrix, coverage, tooling, exact packages, provenance, SBOM and public
  readback requirements; record release status in the publication runbook and
  release evidence.

- 2026-10-25: D-018 extends the existing publisher to exact release lines,
  branch-native checks, current protected-main admission and monotonic Latest.
  Retain five packages, twelve provenance subjects, thirteen public assets and
  existing operator protections; additional lines remain inactive and .NET 11
  adoption is excluded.
  Consolidate this contract, lossless signed-integer upgrades, ordered projection
  fixes, measured allocation reductions and the final Doka 10.6.2/tooling baseline
  under the shared 10.6.0 release notes dated 2026-10-25. Keep one section per
  change category, prior decision dates and historical releases unchanged.

- 2026-10-29: Set the next shared five-package stable identity to 10.6.1 for
  serviced Doka/connector, EF/DI and PKCS minimums, reliable maintenance audit
  evidence and bounded exact-run visibility. Retain public APIs, policies,
  report schemas, fingerprint formats, .NET 10 and all native release gates.
  Keep local advisory warnings visible while qualification remains strict.
  Preserve historical 10.6.0 notes/approval boundaries; publication status
  remains owned by the runbook and exact candidate evidence.
- 2026-11-04: Amend the 2026-10-02 benchmark policy to use native
  BenchmarkDotNet 0.15.8 execution/export evidence instead of report-only budget
  comparisons. All five suites retain their default workloads and run with
  `--filter '*' --job Dry` during CI and release qualification. This is smoke,
  not statistical performance qualification. Invalid configuration, generated
  build/execution failure, missing/failed cases and missing/invalid full JSON
  remain fatal; numeric duration/allocation values never determine success.
  The compatibility input `capture-sqlserver-baseline` adds an ordinary native
  Short job and artifact directory, not a separate median engine. Historical
  reports and approvals are not rewritten or claimed as current BDN baselines.
  Published package dependencies, protected publication, coverage, native
  engine/tooling, supply-chain and readback gates remain unchanged.
- 2026-11-06: Set the shared five-package stable identity to 10.7.0. The
  operator explicitly selected the retained .NET/EF Core 10 package line
  despite the source-breaking removal of public optional-token defaults.
  Document this exception to strict SemVer, affected callers and unchanged
  CLR method signatures; promote the ten required-token declarations into
  the Core shipped baseline. Include the BenchmarkDotNet transition and
  Doka 10.6.4 minimum without changing SQL/policies or historical releases.
  Local ARM preparation is permitted while native x64 SQL Server and complete
  merged coverage remain explicitly pending and blocking. This is not a
  release waiver, extra-line activation or .NET 11 adoption.
- 2026-11-09: Set the shared five-package stable identity to 10.7.1. Keep
  public declarations and shipped API baselines identical to 10.7.0. Correct
  table annotation capture through existing typed callbacks rather than
  publishing the unreleased operation-factory overloads. Retain 10.7.0's
  explicit-token requirements and documented SemVer exception.
  Include explicit InnoDB metadata and atomic SQLite retirement/rebuild-order
  fixes, the verified C# cleanup, Doka 10.6.5 and current Action/image pins.
  Product documentation describes the stable contract without publication-status
  wording. Record actual qualification/publication only in the runbook and
  exact release evidence; preserve native, coverage, SBOM and protected-release
  gates. No .NET 11 or additional release line is activated.

- 2026-11-11: Set the shared five-package stable identity to 10.7.2. Include
  native expression/catalog, temporal-value and binary-literal ownership fixes
  through existing public contracts, plus readable raw SQL and LF-stable C#.
  Public APIs, dependency floors, report schemas and fingerprint formats remain;
  corrected captured numeric-default identities require review and renewal of
  affected approvals. Preserve previous release/upgrade history and the explicit
  10.7.1 completion-only recovery example. Qualify exact 10.7.2 package bytes;
  earlier local/native results do not complete the final hosted matrix, x64
  SQL Server, coverage or public-readback gates. No publication, extra release
  line, .NET 11 support or timing threshold is authorized by preparation.
- 2026-11-19: Aligned last-update metadata, reciprocal amendments, option titles
  and source/navigation format with the current documentation profile. Retained
  the recorded status and historical approval evidence; this migration grants no
  new architectural acceptance or publication authority. Restored all 24 default
  project locks, explicit shipping security floors and matching-Core private ABI;
  benchmark execution now requires explicit opt-in, including Dry.
  The original record starts at implemented; earlier proposed/accepted transition
  evidence is not recorded and is not reconstructed by this profile migration.

- 2026-11-20: Set the shared five-package stable identity to 10.7.3 for
  internal asynchronous ownership, cleanup, application-lock and native tooling
  identity fixes without changing the public API, report or fingerprint formats.
  Include current dependency/image pins and explicit matching-Core private ABI;
  retain MySqlConnector 2.6.2 with optional 3.0.0 and .NET/EF Core 10.
  Qualify exact 10.7.3 package/README bytes and preserve earlier native evidence
  at its original source identity. Product documentation states the stable
  contract; only the runbook and exact release evidence record publication
  status. Existing protected publication, hosted matrix, x64 SQL Server,
  coverage, SBOM and readback gates remain. No tag, package upload, additional
  release line or .NET 11 support is authorized by preparation.

- 2026-11-21: The owner approved full .NET/EF Core 11 source migration and the
  required SqlClient 7.1.2 transition. Select the shared 11.0.0 source identity,
  retaining the default development suffix and exact matching own-Core ABI.
  Review the byte-identical SNI.runtime 7.1.2 license as the sole exact
  development/testing exception and remove the unused NativeInterop waiver.
  Keep the existing five-package inventory, strict audits and complete native,
  tooling, package, coverage and publication gates. Local prepared policy does
  not activate hosted publication or maintenance; historical 10.x evidence is
  not carried forward as 11.x qualification.

### Implementation References

- [Release-line decision](D-018-release-lines-and-monotonic-publication.md)
- [Release-line policy](../../eng/release/lines-policy.json)
- [Release-line operations](../operations/release-lines.md)

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
- [dependabot-core issue 13950](https://github.com/dependabot/dependabot-core/issues/13950) (primary source; retrieved 2026-09-21)
- [NuGet transitive pinning and pack promotion](https://learn.microsoft.com/en-us/nuget/consume-packages/central-package-management#transitive-pinning) (primary source; retrieved 2026-11-19)
- [NuGet lock ownership and locked restore](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files#locking-dependencies) (primary source; retrieved 2026-11-19)
- [.NET dependency-range trade-offs](https://learn.microsoft.com/en-us/dotnet/standard/library-guidance/dependencies) (primary source; retrieved 2026-11-19)
