# Documentation

This is the task-oriented entry point for SafeMigrations documentation. The
repository contains the complete source prepared for the 10.0.2 stable
maintenance release; package availability and OpenSSF status are established
by their linked external registries, not by source documentation alone.

## Use and deploy

| Task | Canonical guide |
| --- | --- |
| Install, register a provider, and scaffold strict or legacy-convergence migrations | [Project README](../README.md) and [runnable sample](../samples/Doka.EntityFrameworkCore.SafeMigrations.Sample/README.md) |
| Compare generated strict, generated legacy-convergence, and hand-authored migration source | [Migration authoring paths](migration-authoring.md) |
| Find public inputs, outputs, and failure boundaries | [API reference](api-reference.md) |
| Check engines, dependency ranges, qualified boundaries, and evidence | [Support and qualification](support-and-qualification.md) |
| Understand MySQL/MariaDB session guards and implicit commits | [MySQL and MariaDB DDL](mysql-mariadb-ddl-behavior.md) |
| Deploy independently to heterogeneous instances and recover safely | [Deployment and recovery](runbooks/deployment-and-recovery.md) |
| Interpret a blocked report or stable error | [Failure codes](runbooks/failure-codes.md) |
| Collect metrics without exposing protected reports | [Observability](runbooks/observability.md) |
| Independently verify a downloaded release | [Release verification](security/release-verification.md) |

## Understand and contribute

- [Implementation design](implementation-design.md): control flow, ownership,
  policy, projection, fingerprints, resource bounds, and lifecycle.
- [Vertical slices](vertical-slice-architecture.md): source layout and
  Core/provider/test/benchmark dependency boundaries.
- [EF Core and provider upgrades](efcore-provider-upgrade-risk.md): versioned
  public integration points and the evidence required for updates.
- [Architecture decisions](decisions/README.md): Doka MADR Enterprise Profile
  1.0 on MADR 4.0.0, with named decision authority and explicit review status.
- [Contributing](../CONTRIBUTING.md): development setup, positive/negative
  tests, package verification, coding standards, and pull requests.
- [Security design and assurance](security/security-design.md): assets, trust
  boundaries, abuse cases, controls, and evidence limitations.
- [Secure development](security/secure-development.md): review, analysis,
  dependency handling, and incident evidence.

## Maintain and govern

- [Governance](../GOVERNANCE.md), [direction](../ROADMAP.md),
  [Code of Conduct](../CODE_OF_CONDUCT.md), and [support](../SUPPORT.md).
- [Security policy and private vulnerability reporting](../SECURITY.md).
- [Publication operations](operations/release-publication.md): maintainer setup,
  qualification, protected wait, signed tag, approval, readback, and recovery.
- [Release process](release-process.md): package/version identity, qualification
  scope, integrity requirements, and acceptance contract.
- [Repository settings](runbooks/repository-settings.md): operator-owned
  post-publication controls and read-only confirmation commands.
- [OpenSSF Best Practices evidence](openssf-best-practices.md):
  maintenance evidence for the achieved Passing self-assessment, unassessed
  Silver/Gold criteria, and the separate automated Scorecard assessment.
- [Changelog](../CHANGELOG.md): human-readable package changes.

## Ownership and evidence discipline

The README owns onboarding; the API guide owns the input/output overview;
support owns the matrix; architecture owns implementation structure; ADRs own
decision rationale; runbooks own procedures; security documents own assurance
arguments; the OpenSSF document owns evidence mapping and links to externally
hosted status. Link to the owner instead of maintaining another copy of its
contract.

The author of a behavior change updates its canonical document and tests in
the same review. Public XML documentation ships with each assembly for IDE
help. Preserve published anchors when moving a section. Use English, ASCII,
relative repository links, and dated primary sources for external claims.

Review changed documentation from the repository root with `git diff --check`
and follow every changed local link. Check local destinations/anchors, ASCII/LF
encoding, criterion evidence, and declared navigation. ADRs are included as
Markdown; their format, history,
relationships, and content are reviewed against the Doka profile, not a local
ADR-specific validator. This review does not certify the truth of prose, perform
an external security assessment, or create hosted controls. Reviewers must
trace claims to implementation and qualified execution evidence.
