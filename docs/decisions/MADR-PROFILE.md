# Doka MADR Enterprise Profile 1.0

## Purpose

SafeMigrations adopts the document format and content requirements of Doka
MADR Enterprise Profile 1.0, based on MADR 4.0.0. It uses the same metadata
keys, status vocabulary, heading hierarchy, symmetric trade-offs, confirmation,
relationships, source provenance, and decision history.

The upstream MADR structure remains recognizable. Doka's mandatory content
rules are explicit extensions to upstream MADR, not claims that upstream
requires every field. SafeMigrations does not introduce a competing profile
version or rename the `doka-profile-version` field.

The maintainer selected this format on 2026-08-26. Repository-specific Doka
validator, generated-index, and build integration requirements are not adopted;
the [tooling boundary](#tooling-boundary) explains this deliberate distinction.
It does not weaken the required content.

## Normative Language

The terms MUST, MUST NOT, SHOULD, SHOULD NOT, and MAY are normative.

## File and Identifier Contract

- Decision files MUST use `D-NNN-lowercase-version-safe-slug.md`. Slugs permit
  lowercase letters, digits, dashes, and version dots.
- Metadata, filename, and H1 identifiers MUST match.
- Identifiers MUST be unique and contiguous from `D-001`. Assign a new
  identifier against the current reviewed corpus; never renumber merged ADRs.
- Decision content MUST be ASCII-only, with LF and a final newline.
- A decision identifier is immutable after the file is merged.
- The H1 MUST use `# D-NNN -- Short decision title`.

## Metadata Contract

Every ADR MUST begin with these flat YAML keys in this exact order:

```yaml
---
id: D-NNN
status: proposed
date: YYYY-MM-DD
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Bounded decision scope"
supersedes: []
superseded-by: []
amends: []
amended-by: []
madr-version: "4.0.0"
doka-profile-version: "1.0"
---
```

Nested YAML and undeclared keys are forbidden. Do not add a `title` key to
satisfy a particular importer: the title is the H1. Do not replace the named
decision maker with a generic maintainer role.

Front matter is the single source of truth for current decision metadata.
The body MUST NOT repeat status, date, or scope as labeled metadata or retain
an original-record-metadata migration block. Historical changes belong under
Decision History; implementation detail belongs under More Information or
Implementation References.

`decision-makers` identifies the accountable decision authority; its presence
does not mean that a proposal has been approved. `consulted` names actual
consultation participants. `informed` names the maintainer-designated audience
kept up to date through one-way communication. For SafeMigrations, use the
core-maintainers team declared in [CODEOWNERS](../../.github/CODEOWNERS).
An entry does not prove notification delivery, consultation, or approval.
Do not invent participants; empty lists are valid when no consultation or
informed audience has been designated.

Allowed statuses are:

- `proposed`: open for review and not authoritative.
- `accepted`: authoritative, with implementation or a trigger-driven
  confirmation path still pending.
- `implemented`: authoritative and confirmed by repository evidence.
- `rejected`: reviewed but never made authoritative.
- `deprecated`: historically relevant but no longer recommended.
- `superseded`: replaced by another ADR and paired with `superseded-by`.

The normal status path is `proposed -> accepted -> implemented`; a proposal
MAY instead become `rejected`. An accepted or implemented decision MAY become
deprecated or superseded. Metadata represents the current state; Decision
History records the transitions.

An existing implementation is not proof that its retrospective explanation
has been accepted. Do not backdate records or infer approval from passing
tests. The initial SafeMigrations recording date is 2026-08-26; later profile
migration does not rewrite the original decision date.

## Section Contract

Every ADR MUST contain the following fixed sections in this order and at the
displayed levels. Option-specific H3 sections occur within Pros and Cons.

```text
## Context and Problem Statement
## Decision Drivers
## Considered Options
## Decision Outcome
### Consequences
### Confirmation
## Pros and Cons of the Options
### Exact considered option
## More Information
### Re-evaluation Triggers
### Decision History
### Implementation References
### Sources
```

Every Markdown heading MUST be preceded by a blank line. The decision question
must identify an actual SafeMigrations boundary and problem, not generic
architecture aspirations.

Decision Outcome MUST use:

```text
Chosen option: "Exact considered option", because ...
```

The chosen option MUST exactly match one item under Considered Options. The
outcome MUST explain the resulting contract, ownership, and important limits.
It MUST NOT silently authorize new dependencies or external operations.

## Symmetric Trade-offs

Every considered option MUST have a same-named H3 section under Pros and Cons
of the Options. Each option MUST contain at least one `Good, because ...` and
one `Bad, because ...` bullet. Consequences MUST also contain both.

Alternatives must be credible choices for the stated problem. Explain why a
rejected option is useful in some circumstances and why it does not satisfy
this decision's drivers. Repeating the selected option as a slogan or merely
calling alternatives complex is not sufficient rationale.

## Confirmation Contract

Confirmation MUST identify reproducible commands, tests, repository gates, or
inspections that can prove continued compliance, with expected outcomes.

- Link implementation and test evidence, including important negative cases.
- Separate source inspection, local execution, live database qualification,
  and hosted or human approval evidence.
- A listed command is an acceptance procedure, not proof of a completed run.
- A decision with trigger-driven implementation MUST identify how to confirm
  whether the trigger fired and what gate applies when it does.
- Review approval alone is not implementation confirmation.

Confirmation documents how to validate the architectural decision. It does
not require a dedicated ADR-validation executable.

## Relationship Contract

Relationships use ADR identifiers only:

- `supersedes` and `superseded-by`;
- `amends` and `amended-by`.

Every relationship MUST be bidirectional and resolve to an existing ADR.
`superseded-by` requires status `superseded`. An amendment changes part of a
still-valid decision and does not itself change that decision's status.

A topical link is not an amendment or supersession. Keep relationship lists
empty when records describe complementary decisions. Update both records and
their history together for a real relationship change.

## Source Provenance

External URLs MUST appear only under Sources. Every external entry MUST use:

```text
- [Source title](https://authoritative.example/path) (primary source; retrieved YYYY-MM-DD)
```

Use vendor documentation, official specifications, source repositories, or
first-party release/lifecycle policies. Verify that the cited source supports
the particular claim. A source label or retrieval date does not prove that
semantic relationship.

Repository-only decisions MUST use this exact entry:

```text
- No external sources; repository evidence only.
```

Retrieval dates record when an external claim was checked, not when the
decision was originally made. Local implementation/test links belong under
Implementation References. Keep operational instructions in their canonical
runbook and link them rather than creating a second independent procedure.

## Decision History

History entries MUST use:

```text
- YYYY-MM-DD: Description of the decision or status change.
```

The first entry MUST be `Decision recorded with status <status>.`. A status
change MUST be `Status changed from <old> to <new>.`, followed by the reason
and review evidence where available. The history must agree with current
metadata and the allowed transitions.

Record substantive corrections and profile migrations without erasing the
earlier history. A retrospective record must say that implementation predates
the record and that historical approval dates are not being reconstructed.

## Tooling Boundary

The [template](adr-template.md) is the authoring contract. The
[index](README.md) is a reviewed navigation table, not a second metadata store.

SafeMigrations does not copy Doka's `AdrValidator`, `eng/validate-adrs.sh`,
generated `decision-index.json`, or mandatory local-build ADR gate. The general
documentation check verifies links, navigation, encoding, and the OpenSSF
inventory; it does not validate ADR metadata, history, or semantic quality.

The maintainer's external MCP/CLI tooling may generate or import these
documents. It is not a build, runtime, restore, or release dependency.
Generated changes require the same review as authored changes. Before using a
new importer/exporter version, verify preservation of identity, title,
metadata, statuses, heading levels, consequences, confirmation, relationships,
and history. Import must not silently promote approval state or drop content.

## Primary Sources and Reuse

The Doka profile and template were compared at repository revision
`cb4b83b66d280dd360daf892323d7c7bd370b03b`. This document adopts that document
contract with the tooling distinction above.

- [Doka MADR Enterprise Profile 1.0](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/cb4b83b66d280dd360daf892323d7c7bd370b03b/docs/decisions/MADR-PROFILE.md) (primary source; retrieved 2026-08-26)
- [Doka ADR template](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/cb4b83b66d280dd360daf892323d7c7bd370b03b/docs/decisions/adr-template.md) (primary source; retrieved 2026-08-26)
- [MADR 4.0.0 full template](https://github.com/adr/madr/blob/4.0.0/template/adr-template.md) (primary source; retrieved 2026-08-26)
- [MADR 4.0.0 license](https://github.com/adr/madr/blob/4.0.0/LICENSE) (primary source; retrieved 2026-08-26)

Upstream MADR offers MIT or CC0-1.0; this adaptation uses the CC0-1.0
alternative. Doka and project-specific text remain under the applicable MIT
repository license. This is attribution, not an external endorsement.
