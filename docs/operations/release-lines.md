# Release lines

SafeMigrations supports independently maintained source lines through the existing
five-package release pipeline. [D-018](../decisions/D-018-release-lines-and-monotonic-publication.md)
records the contract. This capability does not migrate the product to .NET 11.

## Current configuration

The authority is [lines-policy.json](../../eng/release/lines-policy.json) on
protected main. Main/10 is active; maintenance/10.x is inactive. The x is literal,
not a branch wildcard. No preview branch or additional publication route is
activated. The SDK, EF graph, package identities and existing nuget environment
remain unchanged.

| State | Allowed behavior |
| --- | --- |
| inactive | Registered preparation PR checks, no release qualification or scheduling |
| prepared | Own-line qualification-only, no publisher environment or NuGet credential |
| active | Own-line qualification, scheduling and authorized publication |

Only one active publisher may own a package major. When main eventually adopts a
different major, a reviewed policy change may transfer the old major to its
maintenance branch. Framework adoption and branch activation are separate work.

## Normal candidate and publication

Use [the existing publication procedure](release-publication.md). Select the
authorized branch in Release candidate, enter the canonical unused version and
leave qualification-only false for publication. The version suffix selects
stable or prerelease. Every line qualifies its own current source, SDK, EF graph,
full engine matrix, package bytes and portable provenance before the signed tag.

For a prepared line, or a nonpublishing rehearsal of an active line, set
qualification-only true. It runs the complete reversible qualification and
attestation but skips the environment-bearing publisher job itself. Do not
create a tag or approve/request NuGet publication for such a run.

After the ordinary protected wait, create the personally signed annotated tag on
the qualified SHA and approve that same run. The publisher independently reads
current protected-main policy, checks the selected protected source and existing
nuget environment, then binds the actual original candidate artifacts and bytes.
It rechecks current permission before public draft state, NuGet credentials and
final publication. Candidate-local policy is never publication authority.

All versions and lines share a non-cancelling publication queue. It serializes
publishers, not maintainer policy edits. Complete an affected publication before
disabling/reassigning a line. New validators cannot revoke old-SHA workflow code
or override repository administrators.

## Prepare and activate a maintenance line

This is a separately reviewed operation, not something the feature does now:

1. Prepare the exact registered branch from its reviewed source, with its own
   unchanged compatible SDK/dependencies and line-aware workflows. Preserve full
   Required Checks, branch protection and the existing signature policy.
2. Change its policy state to prepared through review. Preparation PRs must pass
   that branch's full CI and exact-base Dependency Review, including forks and
   Dependabot. Qualify its own current commit with qualification-only true.
3. Separately approve activation and the exact branch's deployment permission in
   the existing nuget environment. Retain reviewers, self-review policy,
   administrator bypass and Trusted Publishing identity; do not create a new
   environment, credential generation or long-lived key.
4. Make a reviewed policy change to active, retaining one active publisher per
   major, and update/check the maintained-line Dependabot targets. Verify real
   hosted schedules, exact child commits, approvals and readbacks independently.

Use the repository-owned renderer/check for a reviewed target update:

```bash
python3 -m eng.release.maintenance render-dependencies --output .github/dependabot.yml
python3 -m eng.release.maintenance check-dependencies
```

The renderer reads the canonical [.github/dependabot-template.yml](../../.github/dependabot-template.yml),
which retains update settings independently of the currently active targets.
This also permits retirement of every line and later resumption without losing
group/cooldown policy or leaving inactive update targets enabled.
The renderer does not activate policy or open PRs. It reuses the existing three
ecosystems, groups and cooldowns, retaining framework-major boundaries for SDK,
EF, Doka, Npgsql EF and framework-coupled Microsoft.Extensions dependencies.
The generated NuGet ceilings exclude the next major's prereleases too, not only
its GA version. Paired prerelease floors cover canonical numeric identifiers
and Dependabot's accepted leading-hyphen ordering; current-major servicing
remains eligible. Neither these generated targets nor preview lines are active
in the current configuration.
CI checks the result without writing it back to a branch. Do not resurrect
unrelated Docker-image update targets as part of line setup.

No command in this document grants permission to bypass Required Checks,
activate a branch, change hosted protection or publish a package.

## Recovery and immutable history

Use Re-run failed jobs on the original run. The successful upstream jobs retain
the original package, provenance and frozen-line artifact identities. Publication
restores its original admission and verifies unchanged identities and bytes
against current authorization. Do not construct producer names from the retry's
new run-attempt number.

A completed step explicitly concluded as skipped remains non-executed even
when the runner records timestamps before evaluating its condition. Missing,
duplicate or non-skipped write history still cannot mint replacement admission.

Re-run all jobs is not an exact-byte recovery if it rebuilds the candidate.
Missing, expired, replaced or contradictory indispensable evidence cannot justify
new bytes under an existing tag. Reconcile partial packages, symbols and release
assets through the existing bounded publisher; never move a tag or replace an
immutable public asset. There is no second release database or exceptional
admission allowlist.

The twelve portable provenance subjects and thirteen public release assets are
unchanged. Line and admission receipts are separate immutable same-run workflow
artifacts. Retain/download their evidence before artifact expiry if needed for
operations. Historical public verification uses the original signed tag,
workflow/source ref and asset inventory, not today's active policy.

## Stable status and Latest

A maintenance release may be stable without being GitHub Latest. Prereleases
never become Latest. The common publisher compares canonical SemVer before
promotion: a lower major or a patch that finishes after a higher patch does not
displace it. A historical immutable retry verifies its release without promoting
it again. Unknown or contradictory remote state is a failure, not an empty
Latest result.

## Ongoing qualification and dependency servicing

The default-branch coordinator schedules branch-native transitive vulnerability
audits daily and full qualification weekly for active lines only. It dispatches
each selected line once and validates exact ref, SHA, workflow, correlation and
the actual successful producer jobs, including every qualification matrix cell.
Dispatch success or a green wrapper with missing/skipped producers is not proof.
The bounded native matrix reader recognizes every declared include row before
accepting an inventory. Mapping key order is irrelevant; incomplete or unsupported
matrix forms fail instead of silently removing a required producer.

The coordinator follows only the immutable run ID returned by dispatch. Its
initial API visibility and explicitly unfinished queued identity may settle for
at most two minutes within the existing workload deadline. Contradictory source
metadata, authorization failures and completed mismatched identities fail
immediately. Every successful completion still requires the exact source,
workflow, correlation title and complete producer inventory. Retained reports
include bounded failure codes, HTTP status or identity-field categories, never
raw API error bodies or command diagnostics. The older failed coordinators did
not retain the first-response details; their precise transient cause cannot be
reconstructed from those artifacts.

The three existing Dependabot ecosystems retain their groups and cooldowns.
Maintained-line version-update targets follow reviewed active policy. Security
updates still use GitHub's default branch, so explicit branch-native audits are
mandatory. Audit command success alone is insufficient: vulnerability JSON and
advisory/restore errors must be checked. Scorecard remains repository-default
evidence rather than being duplicated for each line.

Fresh advisory HTTP caches use unique disposable directories beside, not
inside, the retained audit directory. This keeps NuGet's colon-bearing cache
filenames out of GitHub artifact uploads, including when `TMPDIR` points into
the evidence directory. Success and failure release only the run-owned cache;
restore/audit logs and validated summaries remain retained. Cache cleanup must
succeed before a clean summary is written.

Ordinary feature/manual CI remains nonpublishing and does not inherit release
version-major or activation requirements. Extra-line activation, real hosted
dispatch, OIDC and publication need separately observed evidence; local tests
cannot establish them.

The initial feature PR can still obtain Dependency Review before the policy
exists on main. The existing PR-only startup recognizes only an authenticated
missing policy on independently protected main and keeps the exact legacy
main-only base contract. Malformed/inaccessible policy and maintenance targets
are not allowed by that bridge. Publication has no corresponding fallback.
