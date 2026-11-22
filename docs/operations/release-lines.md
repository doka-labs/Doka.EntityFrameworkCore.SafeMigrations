# Release lines

SafeMigrations supports independently maintained source lines through the existing
five-package release pipeline. [D-018](../decisions/D-018-release-lines-and-monotonic-publication.md)
records the contract. Framework migration and hosted line activation are
separate operations.

## Maintained lines

The authority is [lines-policy.json](../../eng/release/lines-policy.json) on
protected main. Version 11.0.0 uses this paired target:

| Branch | Framework | Package major | State |
| --- | --- | --- | --- |
| main | .NET / EF Core 11 | 11 | active |
| maintenance/10.x | .NET / EF Core 10 | 10 | active |

A feature checkout grants no authority. Only one active line owns each major.
Each line has NuGet, GitHub Actions and SDK Dependabot targets with framework-major
bounds; the existing five-package publisher and nuget environment remain shared.

As of 2026-11-22, the hosted authority still has main/10 active and maintenance
inactive. The protected maintenance branch points to signed v10.7.3
(`edb6114f90b24402b6e16af798d111e9844d5af9`); nuget permits main only.
Its creation audit (rule suite `5290054122`, bypass) is retained separately and
is not qualification evidence. No further bypass is part of this procedure.

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

Qualification-only may select an unused canonical version of the source major
without changing that snapshot's `VersionPrefix` or adding release notes for a
version that will not be published. For example, qualify an unchanged 10.7.3
snapshot with `version=10.7.4-rc.1` and `qualification-only=true`. The selected
version binds all candidate packages and evidence. Foreign majors, existing
candidate tags and any version already present for any of the five packages remain
rejected. The protected source, current main authority, SDK/EF graph, full
qualification and attestation checks remain mandatory. The frozen result is
nonpublishable; later activation cannot turn it into publication permission.
Ordinary publication still requires the exact source version and dated
changelog entry, followed by a new complete qualification.

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

## Main/11 and maintenance/10 cutover

1. Keep the protected maintenance source on v10.7.3 and its .NET 10 graph.
   Do not recreate the branch, rewrite history or copy .NET 11 dependencies.
2. Merge one reviewed cutover PR containing .NET 11, the paired target policy,
   six Dependabot targets and the 11.0.0 release notes. All existing Required
   Checks, reviews, native x64 SQL Server, coverage and package gates remain.
   No separate bootstrap PR or intermediate policy merge is required.
3. After merge, read back current authority and both protected heads. Keep the
   maintenance nuget grant closed while running Release candidate on
   `maintenance/10.x` with `qualification-only=true`, an empty
   `finalize-run-id` and an unused same-major version such as `10.7.4-rc.1`.
   This qualifies the exact unchanged source without tagging or publication.
   Until both exact active-branch grants agree with authority, the existing
   publisher environment gate rejects ordinary publication on either line.
4. After successful full maintenance qualification and retained evidence,
   separately approve its exact branch grant in the existing nuget environment.
   Preserve reviewers, Trusted Publishing identity and package permissions.
5. Use the ordinary publication procedure for 11.0.0 from main. It requires a
   fresh complete release qualification, personal signed tag and explicit
   environment approval; neither the PR nor a rehearsal authorizes publication.

Authority comes from protected main, not the candidate's local policy.
Qualification-only cannot produce a publication admission. Scheduled maintenance
and every later release still use their own branch-bound full qualification.
Missing checks, permissions or source identity stop execution, not justify bypass.

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
EF, its `dotnet-ef` NuGet CLI tool, Doka, Npgsql EF and framework-coupled
Microsoft.Extensions dependencies. The SDK ecosystem and EF package-name
wildcard do not cover `dotnet-ef` in `.config/dotnet-tools.json`.
Main uses relative SDK/framework `semver-major` exclusions, including while it
still owns .NET 10. These rules continue protecting the new main major after
a deliberate cutover; routine servicing and independently versioned tools or
transport packages remain eligible. Additional maintenance/preview targets use
absolute NuGet ceilings, which exclude the next major's prereleases too, not only
its GA version. Paired prerelease floors cover canonical numeric identifiers
and Dependabot's accepted leading-hyphen ordering; current-major servicing
remains eligible. The paired configuration has six targets and no preview line.
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
at most two minutes within the existing workload deadline. An initial display
title may also finish resolving during `in_progress` or after successful
completion when all immutable source fields are complete. No placeholder-title
allowlist or pending title authorizes success. Contradictory source metadata,
authorization failures, malformed titles and title regressions after the first
exact identity fail immediately. Unsuccessful completion never waits for a title.
An unresolved initial title remains blocking when its original visibility bound
expires; repeated observations never reset either deadline. Every successful
completion still requires the exact source, workflow, correlation title and
complete successful producer inventory.

`completed` with a `null` conclusion is a missing result, not a confirmed
failure. On initial discovery it shares the original visibility bound. For an
already proven execution, the first such observation starts one fixed result
window of at most two minutes, capped by the original workload deadline. Neither
repeated observations nor status changes reset that window. A missing result
expires as `child_result_timeout`; reaching the overall workload limit remains
`child_wait_timeout`. Explicit unsuccessful conclusions still reject immediately,
and no producer evidence is accepted until the conclusion is `success`.

Retained observations include `status`, `conclusion` and `titleState`, including
before rejection. Title states are `missing`, `empty`, `invalid_type`, `different`
or `matching`; invalid status/conclusion values become `unknown`. Reports retain
bounded failure codes, HTTP status and identity-field categories, never raw remote
titles, API error bodies or command diagnostics. The older failed coordinators
did not retain the first-response details; their precise transient cause cannot
be reconstructed from those artifacts.

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
