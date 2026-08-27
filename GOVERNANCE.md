# Project governance

## Stewardship and roles

SafeMigrations is maintained in the Doka Labs organization. Dominic
Kalkbrenner is the project maintainer and the author identified by the package
metadata. The repository's review owner is `@doka-labs/core-maintainers`, as
declared in [CODEOWNERS](.github/CODEOWNERS).

| Responsibility | Accountable party | Required evidence |
| --- | --- | --- |
| Product scope, support, architecture, and change acceptance | Project maintainer | Reviewed issue, pull request, and ADR where applicable |
| Candidate qualification and publication | Release operator authorized by the maintainer | Current signing authority, protected environment access, and exact-run evidence |
| Private vulnerability intake and coordination | Project maintainer through the security contact | Private intake and response record; no sensitive details in public issues |
| Technical review | Reviewer assigned to the change | Recorded review of the actual revision; independence identified explicitly |
| Documentation and verification | Author of the affected change, checked by its reviewer | Updated canonical document and passing applicable checks |
| Contribution | Any participant following project policies | Focused proposal, tests, documentation, and review response |

A team membership, CODEOWNERS entry, bot review, or successful workflow does
not prove independent review or release-capable succession. This document
does not assign undisclosed people access to GitHub, NuGet, signing keys, or
private reports. Role and access changes require maintainer approval and a
readback of the affected systems.

## Decisions and change acceptance

Use public issues and pull requests after public availability. Private
vulnerability work stays in the [private reporting process](SECURITY.md#reporting-a-vulnerability).
The initial reviewed repository publication is a bootstrap operation; it is
not evidence that all historical changes passed public pull-request review.

For subsequent changes, a pull request must describe motivation, compatibility,
tests, documentation, and unresolved questions. Acceptance requires the
applicable checks in [CONTRIBUTING.md](CONTRIBUTING.md), resolved review
findings, and explicit maintainer approval of the revision being merged.
Material changes after approval require renewed review. An author cannot
count self-review or an AI review as independent human review.

Record a material choice as an [architecture decision](docs/decisions/README.md)
when it affects package ownership, public contracts, supported dependencies,
data integrity, security/privacy, migration semantics, or release authority.
Routine corrections do not need a new ADR. A proposed ADR is not authority to
implement a new exclusion, weaken a gate, or accept risk. An existing decision
changes through a reviewed amendment or a new superseding record.

When reviewers disagree, record the options and evidence. The maintainer
resolves the decision and records the rationale; unresolved objections must
not disappear from the record. If a normal review path is unavailable during
an incident, record the reason and exact actions privately as needed and
perform a follow-up review. Incident urgency does not authorize moving release
tags, replacing conflicting artifacts, or bypassing package identity checks.

## Conduct and conflicts

The [Code of Conduct](CODE_OF_CONDUCT.md) owns conduct reporting, enforcement,
conflicts of interest, and appeals. The security policy owns vulnerability
disclosure. Keep these channels separate from ordinary support.

Anyone with a material conflict must disclose it to the responsible reviewer
or private responder. Use an unconflicted responder where available. If no
independent responder exists, state that limitation rather than claiming
independent handling.

## Continuity and access

No tested succession arrangement or two-person release capability is asserted
by this repository. Two accounts in a team are not sufficient evidence.
Before claiming continuity, the maintainer must identify a successor with
legal authority and independently recoverable access to source, package
ownership, security reports, signing, and protected publication. Test that
the arrangement permits accepting changes and publishing within one week
without the unavailable person.

Keep recovery credentials and private report archives outside the repository.
Public evidence should record the arrangement, responsible roles, last drill
date, and outcome without exposing access material. Review access when a role
changes and after an incident; remove obsolete grants through an explicitly
approved administration action.

## Review cadence

Review governance at least annually and when maintainers, signing authority,
publication ownership, support commitments, or security contacts change.
Reconcile it with the [roadmap](ROADMAP.md), security policy, and
[repository-settings handoff](docs/runbooks/repository-settings.md).

OpenSSF status is maintained separately in the
[evidence matrix](docs/openssf-best-practices.md). Policies describe required
practice; operational history supplies evidence that the practice occurred.
