---
id: D-007
status: implemented
date: 2026-08-26
decision-makers: [Dominic Kalkbrenner]
consulted: []
informed: ["@doka-labs/core-maintainers"]
scope: "Shared MADR document quality and the boundary to external authoring tools"
supersedes: []
superseded-by: []
amends: []
amended-by: []
madr-version: "4.0.0"
doka-profile-version: "1.0"
---

# D-007 -- Adopt Doka's MADR document standard without duplicating its toolchain

## Context and Problem Statement

SafeMigrations needs durable architecture decisions that explain why its
contracts exist and can be reviewed consistently with the Doka provider.
The initial reduced local ADR draft did not meet the maintainer's expectation:
a smaller metadata schema and compressed summaries were not an agreed
replacement for the Doka enterprise document standard.

The maintainer is preparing shared MCP/CLI tooling for decision generation and
brownfield import. SafeMigrations should produce high-quality, recognizable
documents without requiring a repository-specific governance engine or making
that external tool part of a library build.

The decision is which format and content contract authors must follow and
which responsibilities remain with review and optional external tooling.

## Decision Drivers

- Use Doka MADR Enterprise Profile 1.0 on MADR 4.0.0, not another local dialect.
- Name Dominic Kalkbrenner as the accountable decision maker.
- Preserve scope, alternatives, good and bad consequences, confirmation,
  relationships, dated history, and primary-source provenance.
- Do not infer approval or historical dates from existing code or generated
  prose.
- Separate document quality from the implementation of a validator/importer.
- Keep the product build and release independent of a forthcoming external
  MCP/CLI service.

## Considered Options

- Doka document contract with review and optional external tooling
- Reduced SafeMigrations-specific MADR profile
- Copy Doka's complete ADR validator and generated-index infrastructure
- Make external MCP/CLI generation mandatory for builds

## Decision Outcome

Chosen option: "Doka document contract with review and optional external tooling",
because it establishes the requested shared quality standard while keeping
tool choice separate from the decision artifact.

All records use Doka's 13 ordered flat metadata fields, its six-state
vocabulary, `D-NNN --` titles, and the complete heading hierarchy. The
`madr-version` and `doka-profile-version` keys preserve the shared identity.
The template names Dominic Kalkbrenner. Consultation lists record actual
participants; informed lists name the maintainer-designated audience kept up
to date on progress. These entries do not prove notification delivery or
decision approval.

Each record must define a bounded question and concrete decision drivers,
evaluate credible alternatives symmetrically, explain the chosen contract and
its limits, and identify reproducible implementation confirmation. Re-evaluation
triggers, decision history, implementation references, and dated primary
sources are required content.

Retrospective records require maintainer confirmation of their rationale.
Approval of the document format alone does not approve separate architectural
decisions. Existing implementation, a passing test, or successful import does
not by itself establish decision authority.

SafeMigrations does not copy Doka's ADR validator project, generated JSON
index, or mandatory local-build ADR validation. Its Markdown index remains
reviewed navigation. General documentation review checks links, encoding,
navigation, API coverage, and the criterion inventory, not ADR status history
or semantic quality.

External authoring/import tooling is optional and outside the product
dependency graph. Generated records receive the same review. Import/export
must be qualified against the document contract before anyone claims a
lossless round trip; the document format is not weakened to accommodate a
parser that omits required content.

### Consequences

- Good, because both repositories can use the same document vocabulary and
  quality criteria without divergent metadata conventions.
- Good, because the library does not need a running governance service or a
  second repository-specific ADR implementation to build and release.
- Bad, because reviewers are responsible for profile consistency and semantic
  quality where no automatic ADR gate is installed.
- Bad, because external tool compatibility requires explicit evidence; merely
  accepting a Markdown file does not prove its relationships and history
  survived import.

### Confirmation

Compare every record and the template against MADR-PROFILE.md: all 13 keys and
their order, title/identity agreement, six-state vocabulary, exact section
hierarchy, chosen-option agreement, symmetric trade-offs, dated history,
resolvable bidirectional relationships, and source syntax must agree.

For every retrospective record, inspect the linked implementation and
positive/negative tests. Distinguish a proposed explanation from implemented
runtime behavior and from an actually executed confirmation run.

Review changed links, anchors, navigation, ASCII/LF encoding, decision
relationships, and primary-source claims. Inspect that no ADR-validator
project, generated JSON index, external CLI install, or MCP call was added to
the build/release graph.

Before adopting external generation/import, use a real Doka-format record
with consequences, confirmation, relationships, and history and compare the
complete round trip. If no such integration is in use, record that fact;
do not treat an untested future tool as current confirmation evidence.

## Pros and Cons of the Options

### Doka document contract with review and optional external tooling

- Good, because format and content quality are shared while authoring tools
  remain replaceable and independent of the runtime product.
- Bad, because human review must detect semantic or relationship drift that a
  specialized local validator could otherwise reject automatically.

### Reduced SafeMigrations-specific MADR profile

- Good, because fewer metadata fields can suit a project with a deliberately
  lighter and explicitly agreed decision-recording contract.
- Bad, because that is not the maintainer's requirement here and would create
  inconsistent quality and interoperability expectations across projects.

### Copy Doka's complete ADR validator and generated-index infrastructure

- Good, because deterministic checks can reject structure, history, and index
  drift at every local build and hosted gate.
- Bad, because SafeMigrations would own another validation implementation even
  though the requested deliverable is the shared format and content standard.

### Make external MCP/CLI generation mandatory for builds

- Good, because a single enforced generator can standardize output when its
  full contract and availability are established.
- Bad, because product builds would depend on a separate developing system and
  generated conformity would still not prove the truth of decision rationale.

## More Information

This ADR selects the document standard, not a security policy or a badge
status. OpenSSF documentation remains preparation only. Enrollment,
criterion answers, public evidence, and any awarded badge are separate
maintainer actions after the repository becomes public.

Operational details remain in canonical guides; ADRs explain choices and
link their implementation evidence. The absence of an ADR-specific build gate
is an explicit tooling decision, not permission to omit required sections or
reduce the depth of the rationale.

### Re-evaluation Triggers

- MADR or the Doka document profile changes in a way the maintainer chooses to
  adopt across projects.
- A qualified external tool can preserve the full document contract and the
  maintainer elects to use it for authoring or import.
- Repeated concrete review escapes justify reconsidering an automatic profile
  check without adding a runtime or external-service dependency.

### Decision History

- 2026-08-26: Decision recorded with status proposed.
- 2026-08-26: Maintainer rejected the reduced SafeMigrations profile and selected Doka's format/content standard with Dominic Kalkbrenner as decision maker.
- 2026-08-26: Status changed from proposed to accepted.
- 2026-08-26: Maintainer clarified that Doka's repository-specific validation infrastructure is not required; the record now separates the shared document contract from optional tooling.
- 2026-08-26: Maintainer designated @doka-labs/core-maintainers as the informed audience.
- 2026-08-26: Status changed from accepted to implemented. Dominic Kalkbrenner confirmed implementation; the adopted profile, ADR corpus, and documentation ownership are in place and reviewed.

### Implementation References

- [Adopted document profile](MADR-PROFILE.md)
- [Doka-format authoring template](adr-template.md)
- [Decision index](README.md)
- [Documentation ownership and navigation](../README.md)
- [Contribution requirements](../../CONTRIBUTING.md)
- [Governance](../../GOVERNANCE.md)
- [OpenSSF preparation, not achievement](../openssf-best-practices.md)

### Sources

- [Doka MADR Enterprise Profile 1.0](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/cb4b83b66d280dd360daf892323d7c7bd370b03b/docs/decisions/MADR-PROFILE.md) (primary source; retrieved 2026-08-26)
- [Doka ADR template](https://github.com/doka-labs/Doka.EntityFrameworkCore.MySql/blob/cb4b83b66d280dd360daf892323d7c7bd370b03b/docs/decisions/adr-template.md) (primary source; retrieved 2026-08-26)
- [MADR 4.0.0 full template](https://github.com/adr/madr/blob/4.0.0/template/adr-template.md) (primary source; retrieved 2026-08-26)
