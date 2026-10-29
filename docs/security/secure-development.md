# Secure development and vulnerability handling

## Contribution and review contract

Follow [CONTRIBUTING.md](../../CONTRIBUTING.md) for reproducible setup and the
complete verification commands. Review every change at its actual trust
boundary using the [security design](security-design.md): input validation,
SQL roles, fail-closed decisions, context/history ownership, resource bounds,
privacy, and release identity. An AI review can assist but is not independent
human review or proof of developer training.

For every security or correctness fix:

1. Reproduce the root cause with synthetic inputs and identify affected paths.
2. Add a negative regression that fails on the old behavior and a positive
   control for legitimate behavior; include every affected engine/profile.
3. Correct the owning implementation rather than suppressing diagnostics,
   loosening policy, or bypassing a failed gate.
4. Re-run affected tests and the complete qualification required for release.
5. Record the reviewed revision, commands, outcomes, residual limits, and
   dependency impact. Private findings stay in the private disclosure record.

Test additions and behavior changes require a brief evidence summary in the
pull request. Thresholds or budgets must not be raised just to make CI green.
Small contributor tasks must be genuine current issues with scope and
acceptance criteria, not invented labels or an empty template.

## Analysis and dependencies

[Directory.Build.props](../../Directory.Build.props) enables nullable analysis,
warnings-as-errors, the SDK's recommended .NET analyzers, and NuGet audit of all
dependencies. Publishable projects additionally enable locked restore through
[src/Directory.Build.props](../../src/Directory.Build.props). SDK code-analysis
rules are separate from compiler warnings. The shared quality workflow
additionally runs public API, style/import, architecture, coverage, performance,
engineering, package, and SBOM checks. Record the SDK and enabled rules with
analysis evidence; a build command name alone does not establish which analysis
ran.

The five committed package-project lockfiles define the exact graph used to
compile and qualify package artifacts. Engineering projects resolve the central
declarations during CI. Dependabot proposes declaration and package-lockfile
updates through ordinary pull requests, where the complete quality workflow
tests every resolved graph. Dependency Review separates PR-owned resolution
from trusted data validation and publication:

- The unprivileged PR workflow resolves the exact base/head revisions in separate
  read-only jobs using each revision's SDK, then emits its completion signal.
  Fork and Dependabot PRs use the same path. Native `cache-mode: none` denies
  cache reads/writes independently of the read-only repository token; MSBuild
  hooks never execute in the privileged receiver or manual dispatch context.
- Only historical base capture keeps NU1901-NU1904 as visible warnings, allowing
  a corrective PR to compare against an already affected dependency graph.
  Both captures retain audited locked restore; advisory retrieval and unrelated
  restore failures remain fatal. Head capture keeps all advisory severities
  fatal. No warning is hidden and no final review policy is waived.
- The default-branch `workflow_run` receiver validates the source run, attempt,
  workflow, repository, current PR, completed source producer jobs and the two
  unique, nonexpired artifact identities. The five committed product locks remain
  enforced; engineering graphs resolve the central declarations. Artifacts from
  contributor execution are untrusted data, never executable receiver code.

The receiver obtains immutable IDs from the authenticated source-run inventory,
not contributor-provided job outputs. Submission jobs download by exact run and
artifact ID, never by a mutable name another job could replace. Fresh trusted
code revalidates bounded, data-only metadata before the
SHA-pinned official action runs; no restore or contributor code runs with write
access. Received data stays outside the trusted checkout and the Action's cwd.
Its explicit `MSBuildBinaryLog` filter reads the sanitized PackageReference
assets; binlogs, `.env`, NuGet configuration, targets, props and executables are
not accepted as artifact inputs. Source manifests must match Git blob identities
from the exact revision tree, and committed lock inventories/hashes must match
assets. Projects without committed locks do not gain an invented lock guarantee.
Both revisions use the same detector/correlator and explicit commit/ref inputs;
original absolute restore paths preserve unchanged manifest identities. The
receiver publishes the required `dependency-review` check on the validated PR
head, not on its own default-branch SHA. Failed, cancelled, stale, ambiguous or
incomplete capture/submission cannot produce a successful review.

Failed-job retries retain each successful producer's original attempt-bound
artifact; a later failed or skipped producer cannot borrow an earlier success.
The current source run/attempt and PR identities are still rechecked, and an
invalid new producer/artifact set must leave an explicit failed PR-head check.

The receiver rechecks head, base and run attempt before publishing success.
The repository's strict up-to-date required-check policy covers later changes
to `main`; keep that policy enabled or use an equivalent merge-queue contract.
This workflow does not change repository settings. GitHub only activates the
automatic `workflow_run` receiver once it is on `main`. For its first deployment,
a maintainer can dispatch the existing CI workflow from the reviewed,
same-repository PR head branch:

```bash
gh workflow run ci.yml --ref <reviewed-pr-branch> -f dependency-review-pr=<pr-number>
```

This calls the same data-only receiver, not a reduced review or replacement status.
It consumes the already completed matching PR run's exact artifacts and never
repeats contributor restore hooks under manual-dispatch cache authority. Its
helper requires the selected branch, executing commit and workflow commit to
match the current open PR head. The original dispatch input and authoritative
source/receiver run identities must agree; stale or mismatched runs cannot
publish success. The normal full qualification still runs with read-only
permissions. Only merge after the actual `dependency-review`, all other required
checks and reviews pass for the current commit; never disable a required check,
create a substitute success or grant contributor restore jobs write access.
A changed PR head requires a new dispatch from its newly reviewed commit.
This manual entry deliberately rejects fork heads; after deployment to `main`,
ordinary PR, Dependabot and fork runs use the automatic path and require no
manual snapshot repair.
GitHub's own first-time fork approval policy remains in force; this handoff does
not bypass it.

GitHub's independent automatic submission may still run, but is not the
authority for whether a particular base/head pair has matching snapshots.
Explicit submissions have precedence per manifest; the hosted warning header
must still be empty before success is claimed. The comparison job reviews the
pull-request delta with the official action, rejects new high-or-critical
vulnerabilities and dependencies outside the approved SPDX license set or the
two exact [package exceptions](#sql-server-dependency-license-exceptions), and
runs its bounded snapshot retry. The pinned action proceeds after its timeout
even if a warning remains, so the workflow then checks the same GitHub compare
endpoint and requires the snapshot-warning header to be present and empty.
GitHub's public REST reference did not specify that response header when
retrieved on 2026-08-28; the immutable action source is the primary evidence
for its name and base64 encoding. A missing header deliberately blocks the
pull request until the vendor contract is reverified and any workflow change is
reviewed. Configuration alone is not evidence that hosted alerts, code
scanning, automatic submission, or security updates actually ran. Review
affected production, development, action, and container dependencies against
primary advisories. Triage relevant alerts to resolution with release impact,
never by treating a successful restore or empty delta as proof that no
vulnerability exists.

The approved SPDX set includes `PostgreSQL`, used by the existing Npgsql packages.
It does not approve unknown licenses. Doka declares MIT; missing hosted license
metadata remains visible under the pinned review action's existing behavior,
without a Doka-specific package waiver, downloader or second license verifier.
The official submission action currently downloads the latest Microsoft
Component Detection executable; its action SHA alone does not pin that tool.
Record the actual detector version with hosted evidence. Local fixture tests
and source configuration do not prove GitHub accepted the snapshots or that a
fork completed the hosted handoff; the hosted comparison gate remains mandatory.

An EF Design or Tools update also crosses the source-generation trust boundary.
Re-run strict and legacy scaffolding for every provider, compile the generated
source, exercise the direct-Design, Tools-only, runtime-only, and excluded
SafeMigrations-build-assets package profiles, and retain negative tests for
unexpected generator shapes and unmodeled provider annotations.

Do not add a library, GitHub Action, scanner, or documentation tool merely to
improve a badge signal. New dependencies require review of purpose, authority,
license, maintenance, supply-chain exposure, and versioned verification. Use
the [upgrade contract](../efcore-provider-upgrade-risk.md).

### SQL Server dependency-license exceptions

Only the following versions have a repository-specific license exception:

| NuGet package | Approved version | Reviewed vendor terms |
| --- | --- | --- |
| Microsoft.Data.SqlClient.SNI.runtime | 6.0.3 | [Package license](https://www.nuget.org/packages/Microsoft.Data.SqlClient.SNI.runtime/6.0.3/License) |
| Microsoft.Identity.Client.NativeInterop | 0.20.6 | [Package license](https://www.nuget.org/packages/Microsoft.Identity.Client.NativeInterop/0.20.6/License) |

The 2026-10-20 EF Core 10.0.13 update resolves SqlClient 6.1.7 and SNI 6.0.3.
The SNI 6.0.3 package's `LICENSE.txt` is byte-identical to the previously
reviewed 6.0.2 file; both have SHA-256
`9335e8bad875dd7be4eebd55d2335eb6433d1cea61aadb3817af7807bef8932a`.
Only the exact SNI version changes; the exception's use and package-shape
boundaries below remain unchanged. The earlier version is not retained as a
second allowed version.

The 10.6.1 SafeMigrations servicing baseline promotes SqlClient to 6.1.8.
SNI.runtime remains 6.0.3 and Broker still resolves NativeInterop 0.20.6;
neither exact exception nor the use/package-shape boundary is expanded.

These transitive dependencies belong to the EF Core SQL Server / SqlClient
stack, not Core or the other providers. Their vendor terms allow the
repository's development and testing use. SafeMigrations publishes its own
MIT-licensed code and dependency references; its SQL Server package does not
bundle either vendor's managed or native binaries. This approval is for that
use and package shape, not a grant of consumer binary-redistribution rights.
Consumers remain subject to the terms of the SQL Server stack they choose.
No vendor license text or binaries are added to the SafeMigrations package.

The pinned Dependency Review action compares exception package names while
ignoring PURL versions. Therefore the required job additionally runs
`eng/verify-dependency-license-exceptions.py` over every page of the hosted
dependency comparison. Added packages must match the approved name, ecosystem,
version, and decoded PURL; other versions or inconsistent metadata block the
job. The guard recognizes the action's permissively parsed names before
enforcing consistent metadata; percent-encoded or malformed PURLs cannot
bypass the version check.
Removing an older version does not require a new exception. Product lockfiles
and positive/negative regression cases are checked by the engineering gate.
A version change requires a new review of the exact package's terms and an
explicit policy update; it is not automatically approved by this exception.

The global SPDX allowlist, high/critical vulnerability rejection, visible
Scorecard warnings, complete-snapshot requirement, and all other quality gates
remain unchanged. `LicenseRef-scancode-unknown` is not globally approved. A
local test pass is not a hosted Dependency Review verdict.

Primary implementation evidence, retrieved 2026-10-01:

- [Pinned action's version-independent PURL matcher](https://github.com/actions/dependency-review-action/blob/a1d282b36b6f3519aa1f3fc636f609c47dddb294/src/purl.ts).
- [GitHub dependency comparison response](https://docs.github.com/en/rest/dependency-graph/dependency-review#get-a-diff-of-the-dependencies-between-commits).
- [GitHub CLI pagination and slurp contract](https://cli.github.com/manual/gh_api).

Snapshot and handoff sources, verified 2026-10-21:

- [GitHub dependency submission identity and API](https://docs.github.com/en/rest/dependency-graph/dependency-submission).
- [Dependency graph source precedence](https://docs.github.com/en/code-security/concepts/supply-chain-security/dependency-graph-data).
- [Pinned official submission action](https://github.com/actions/component-detection-dependency-submission-action/tree/98beaea5a1227f3db476e4c9ca959a0cacd7ac4f).
- [GitHub workflow_run event and default-branch requirement](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#workflow_run).
- [Manual workflow execution and branch selection](https://cli.github.com/manual/gh_workflow_run).
- [Reusable workflow caller context and permissions](https://docs.github.com/en/actions/reference/workflows-and-actions/reusing-workflow-configurations).
- [Executing commit, ref and workflow-commit variables](https://docs.github.com/en/actions/reference/workflows-and-actions/variables).
- [GitHub Actions secure-use guidance](https://docs.github.com/en/actions/reference/security/secure-use).
- [Pinned review action's license resolution](https://github.com/actions/dependency-review-action/blob/a1d282b36b6f3519aa1f3fc636f609c47dddb294/src/licenses.ts).

Cache authority and source-artifact isolation, verified 2026-10-25:

- [Native job cache access and scoped-token enforcement](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#jobsjob_idcache-mode).
- [Cache access for low-trust triggers and explicit denial](https://docs.github.com/en/actions/reference/workflows-and-actions/dependency-caching#controlling-cache-access-with-cache-mode).
- [Artifact identities, expiry and workflow-run metadata](https://docs.github.com/en/rest/actions/artifacts).

## Dynamic and coverage evidence

Provider tests execute real Testcontainers databases and cover hostile
identifiers, conflicting definitions/data, partial failures, retries, and
cancellation. FsCheck adds generated and automatically shrunk properties for
Core fingerprint ordering and canonical form, structured-expression
equivalence and identifier rewriting, provider identifier rendering, catalog
normalization, and fail-closed rejection of foreign provider fragments. These
properties run as part of the ordinary test assemblies and do not claim
coverage-guided or continuous fuzzing. Fixed-seed generated state cases remain
separate deterministic matrix tests. Read the
[coverage policy](../support-and-qualification.md#coverage-gate) and preserve
per-release results; a percentage alone does not prove the security boundary.

OpenSSF's ordinary-test-suite alternative for dynamic analysis needs at least
80% branch coverage. The Passing assessment uses the actual scoped results from
[CI run 33079411038](https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/actions/runs/33079411038):
84.29% for Core, 85.34% for MySQL/MariaDB, and 86.88% for PostgreSQL. The
configured 75% MySQL/MariaDB floor alone does not preserve that evidence;
reassess a measured qualified run after relevant changes. Memory-safe C# does
not eliminate SQL injection, logic, availability, lifecycle, or supply-chain
risks.

## Private report workflow

Use the [security reporting policy](../../SECURITY.md#reporting-a-vulnerability). The security
responder keeps an access-restricted record of receipt, acknowledgement,
reproduction, impact, severity, affected/fixed versions, upstream coordination,
mitigations, tests, publication date, and agreed credit. Retain only information
needed for response; redact credentials and unrelated personal data.

Published advisories and release notes identify known fixed vulnerabilities
and actionable mitigations. A private coordination target does not prove a
past response time or authorize ignoring a public vulnerability. Measure actual
response/fix dates for OpenSSF assessment. If ownership is upstream, coordinate
with the reporter rather than forwarding private details without permission.

## Secrets and incident evidence

Before public exposure, review all reachable Git refs and working-tree content
for secrets and sensitive artifacts; repeat on relevant changes. A scanner
needs synthetic positive and negative controls. Findings need human triage;
absence of detections is not a secret-free guarantee. Test credentials must
be disposable and restricted to test resources, never production identities.

If a real credential is exposed, revoke or rotate it at its issuer, assess
access, preserve incident evidence, and coordinate history/artifact cleanup
with the owner. Deleting a line or rewriting history does not revoke a key.
Do not upload sensitive scan results to public workflow artifacts.

An incident involving source, signing, NuGet ownership, or CI credentials stops
publication until affected authority is re-established. Read back hosted
controls and requalify source/artifacts; do not reuse ambiguous provenance.
Restoring repository access is distinct from restoring a consumer database.

## Primary sources

- [.NET code analysis](https://learn.microsoft.com/en-us/dotnet/fundamentals/code-analysis/overview),
  retrieved 2026-08-26.
- [FsCheck running tests](https://fscheck.github.io/FsCheck/RunningTests.html),
  retrieved 2026-08-28; properties generate and shrink arguments, and an
  exhausted property fails rather than silently passing.
- [GitHub Automatic Dependency Submission](https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/secure-your-dependencies/submit-dependencies-automatically),
  retrieved 2026-08-28.
- [GitHub Dependency Review](https://docs.github.com/en/code-security/concepts/supply-chain-security/dependency-review),
  retrieved 2026-08-28.
- [GitHub dependency-review REST endpoint](https://docs.github.com/en/rest/dependency-graph/dependency-review#get-a-diff-of-the-dependencies-between-commits),
  retrieved 2026-08-28; the published response contract does not specify the
  snapshot-warning header consumed by the official action.
- [Dependency Review Action v5.0.0](https://github.com/actions/dependency-review-action/tree/a1d282b36b6f3519aa1f3fc636f609c47dddb294),
  retrieved 2026-08-28; the workflow uses the exact tagged commit and checks
  the comparison warning header read in the pinned
  `src/dependency-graph.ts` implementation after its bounded retry.
- [OpenSSF Passing criteria](https://www.bestpractices.dev/en/criteria/0),
  retrieved 2026-08-28; analysis and response evidence must be measured.
- [GitHub private vulnerability reporting](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository),
  retrieved 2026-08-26; enabling the feature and responder notifications is
  an operator action, not a property created by this document.
