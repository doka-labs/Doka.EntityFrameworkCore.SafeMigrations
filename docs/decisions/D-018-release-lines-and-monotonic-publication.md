---
id: D-018
status: implemented
date: 2026-10-25
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Exact release lines, current publication authority and monotonic Latest"
supersedes: []
superseded-by: []
amends: [D-006]
amended-by: []
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-018 -- Separate release-line qualification from publication authority

## Context and Problem Statement

SafeMigrations must be able to maintain one framework line while development
continues on another. The main-only qualification, provenance and PR checks
cannot provide that contract. Publishing an older stable patch must not replace
a higher stable GitHub Latest release. The existing five-package publisher,
single-maintainer approval and exact-byte recovery must remain intact.

## Decision Drivers

- Preserve one existing publisher, five packages and thirteen immutable assets.
- Qualify each exact source with its own SDK, dependencies and complete matrix.
- Distinguish frozen evidence from current permission to publish.
- Retain existing environment, reviewers, credentials and administrator policy.
- Preserve exact-byte retry and historical verification without a second database.
- Keep dependency review and security servicing valid on maintained branches.
- Do not activate extra lines or migrate to .NET 11 as part of this capability.

## Considered Options

- One shared pipeline with exact line policy and bounded admission adapters
- Copied qualification and publisher workflows for every framework branch
- Keep publication restricted to main

## Decision Outcome

Chosen option: "One shared pipeline with exact line policy and bounded admission
adapters". Reuse the existing release, signature, package, readback and provenance
primitives. Adapt Doka D-030's line contract, not its provider-specific release
subsystem, package inventory or framework migration.

The reviewed policy names exact refs, roles, package majors, channels and upstream
stages. Lifecycle states are inactive, prepared and active. Main/10 remains active;
maintenance/10.x is registered inactive. The x is literal. No preview line, new
SDK, dependency major, branch or hosted grant is introduced or activated.

The existing release-candidate workflow qualifies each admitted source using its
branch-owned toolchain. Qualification-only and prepared runs do not request the
publisher environment or NuGet credentials. Frozen evidence binds the original
repository, ref, SHA, workflow, run/producer attempt and qualification graph.
After protected approval, publication independently reads current protected-main
authority and verifies the existing nuget environment. The candidate's checkout
cannot grant permission. Only one active publisher may own a package major.

The shared non-cancelling publication queue covers all versions and lines.
Admission binds exact original artifacts and package/provenance bytes. Failed-job
retry restores original admission, rejects replaced or expired evidence, and
rechecks current permission. It cannot authorize a rebuild under an existing
signed tag. Complete an affected release before disabling or reassigning its
line; the queue does not lock maintainer policy edits or revoke old workflow code.

Stable Latest promotion compares canonical SemVer inside that publisher.
Prereleases never become Latest, and an older major or out-of-order patch cannot
displace a higher stable version. Historical immutable verification uses its own
signed source, ref and asset contract, not continued current eligibility or Latest
status. Twelve portable provenance subjects and thirteen public Release assets
are unchanged; line/admission receipts are separate immutable workflow artifacts.

Registered lines receive preparation PR checks independently of publication
activation. Active lines receive branch-native scheduled qualification and
transitive vulnerability audits. The coordinator verifies actual source and
successful producers, not successful dispatch alone. Existing Dependabot
ecosystems and grouping remain; maintained-line version targets follow reviewed
activation, while explicit audits cover non-default security-update limitations.

### Consequences

- Good, because new lines reuse the proven five-package publication boundary.
- Good, because historical evidence and same-run recovery retain their meaning.
- Good, because source qualification cannot become perpetual publication rights.
- Good, because ordinary feature CI remains nonpublishing and line-independent.
- Bad, because every maintained line still needs reviewed ports and qualification.
- Bad, because hosted protections and actual publication require separate evidence.
- Bad, because unavailable authority or expired retry evidence blocks publication.

### Confirmation

Require behavioral positive/negative policy, toolchain, current-authority,
qualification-only, tampering, retry, Latest, scheduling and dependency tests.
Retain existing five-package/provenance/readback shell regressions, including
partial publication and historical retries. Verify actual workflow step order,
Action contracts, engineering suites, locked restore and warning-free build.
Keep .NET 10 unchanged and all additional policy lines inactive. Local mocked
tests do not prove hosted grants, dispatch, OIDC, approval or publication.
Exercise timestamped skipped-step histories, every declared native matrix row
under key permutations, and next-major prerelease dependency boundaries.

## Pros and Cons of the Options

### One shared pipeline with exact line policy and bounded admission adapters

- Good, because one trust boundary serves independently qualified lines.
- Bad, because frozen identity and current authority must remain distinct.

### Copied qualification and publisher workflows for every framework branch

- Good, because branches could specialize workflows independently.
- Bad, because signing, recovery, dependency and Latest rules would drift.

### Keep publication restricted to main

- Good, because there is only one source-line identity.
- Bad, because an older framework cannot be serviced after main moves on.

## More Information

[Release lines](../operations/release-lines.md) owns setup and activation.
The existing [release process](../release-process.md) and
[publication runbook](../operations/release-publication.md) retain exact commands.
No additional active line or hosted configuration is implied by this decision.

### Re-evaluation Triggers

- GitHub changes concurrency, rerun, artifact, protection or OIDC semantics.
- Another framework major is explicitly adopted for maintenance or preview.
- NuGet changes publication, signature or normalized-version contracts.
- A real release exposes an ambiguity not covered by exact candidate recovery.

### Decision History

- 2026-10-25: Owner approved complete release-line support modeled on Doka,
  excluding .NET 11 work and activation of any additional line.
- 2026-10-25: Decision accepted and implemented. Behavioral engineering tests,
  original shell regressions, locked restore and warning-free Release build
  confirm the repository contract, separately from hosted operational activation.

### Implementation References

- `eng/release/lines-policy.json`, `lines.py` and `publication.py`
- `eng/release/maintenance.py`, `dependencies.py` and `latest.py`
- `.github/dependabot-template.yml` (canonical retained update policies)
- `.github/workflows/release-candidate.yml` and `ci.yml`
- `.github/workflows/release-line-maintenance.yml`
- `eng/dependency-review-handoff.py` and `eng/pre-tag-check.sh`
- `eng/verify-workflow-concurrency.py` (explicit GitHub/actionlint queue compatibility)
- `eng/tests/test_release_lines.py`, `test_release_publication.py`,
  `test_release_line_maintenance.py`, `test_release_latest.py` and
  `test_release_line_workflow.py` and `test_release_line_flow.py`
- `test_release_line_recovery.py`, `test_release_line_matrix.py` and
  `test_release_line_dependency_bounds.py` (rereview regressions)

### Sources

- [Doka D-030](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/6e2618140d821bd6befdf46181fcb82dca323c4f/docs/decisions/D-030-release-lines-and-monotonic-publication.md)
  (reference implementation; retrieved 2026-10-25)
- [GitHub concurrency](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency)
  (primary source; retrieved 2026-10-25)
- [Workflow events](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows)
  (primary source; retrieved 2026-10-25)
- [GitHub environments](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments)
  (primary source; retrieved 2026-10-25)
- [GitHub reruns](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs)
  (primary source; retrieved 2026-10-25)
- [Dependabot target branches](https://docs.github.com/en/code-security/reference/supply-chain-security/dependabot-options-reference#target-branch)
  (primary source; retrieved 2026-10-25)
- [Runner condition evaluation](https://github.com/actions/runner/blob/main/src/Runner.Worker/StepsRunner.cs)
  and [execution-context timestamps](https://github.com/actions/runner/blob/main/src/Runner.Worker/ExecutionContext.cs)
  (primary sources; retrieved 2026-10-25)
- [Dependabot NuGet version comparison](https://github.com/dependabot/dependabot-core/blob/72d5b295e35a0cd031b57f19a3d96d912fedb601/nuget/lib/dependabot/nuget/version.rb)
  and [requirement matching](https://github.com/dependabot/dependabot-core/blob/72d5b295e35a0cd031b57f19a3d96d912fedb601/nuget/lib/dependabot/nuget/requirement.rb)
  (primary sources; retrieved 2026-10-25)
