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
warnings-as-errors, the SDK's recommended .NET analyzers, locked restore, and
NuGet audit of all dependencies. SDK code-analysis rules are separate from
compiler warnings. The shared quality workflow additionally runs public API,
style/import, architecture, coverage, performance, engineering, package, and
SBOM checks. Record the SDK and enabled rules with analysis evidence; a build
command name alone does not establish which analysis ran.

The committed lockfiles define the exact qualified dependency graph. Dependabot
proposes declaration and lockfile updates through ordinary pull requests, where
the complete quality workflow tests the proposed graph. GitHub Automatic
Dependency Submission supplies resolved base and head snapshots to the hosted
Dependency Graph. The separate Dependency Review workflow compares the
pull-request delta with the official action, rejects new high-or-critical
vulnerabilities and dependencies outside the approved SPDX license set, and
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

An EF Design or Tools update also crosses the source-generation trust boundary.
Re-run strict and legacy scaffolding for both providers, compile the generated
source, exercise the direct-Design, Tools-only, and runtime-only package
profiles, and retain negative tests for unexpected generator shapes and
unmodeled provider annotations.

Do not add a library, GitHub Action, scanner, or documentation tool merely to
improve a badge signal. New dependencies require review of purpose, authority,
license, maintenance, supply-chain exposure, and versioned verification. Use
the [upgrade contract](../efcore-provider-upgrade-risk.md).

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
