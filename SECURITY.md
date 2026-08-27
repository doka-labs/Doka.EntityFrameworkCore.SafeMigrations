# Security Policy

For [supported versions](#supported-versions), [private reporting](#reporting-a-vulnerability),
and [coordinated disclosure](#response-and-coordinated-disclosure), see below.
Do not report an exploitable vulnerability in a public issue, pull request, or discussion.

## System and scope

This policy covers the SafeMigrations repository, its Core, MySQL/MariaDB,
and PostgreSQL packages, and its build and release tooling. SafeMigrations is
an in-process EF Core library, not an authentication service or database
server. The consuming application selects the database endpoint, credentials,
migration assembly, configuration, and execution authority.

The [security design and assurance](docs/security/security-design.md) records
the concrete boundaries, controls, tests, and residual limitations. Those
tests are evidence surfaces, not a guarantee that all vulnerabilities are absent.

## Threat model and trust boundaries

Application-authored migrations and ordinary provider SQL are executable,
privileged deployment inputs. SafeMigrations is not a sandbox for malicious
C# migrations or a substitute for application authorization. Database names,
catalog metadata, and rows may be adversarial even when the migration author
is trusted; they must not become unencoded SQL or authorize an unproven repair.

Release contributors and pull-request content are not trusted publication
operators. Report consumers and telemetry exporters have different access
boundaries. A report can contain schema/object identity and caller metadata;
it is protected deployment evidence, not a public or inherently redacted log.

## Security invariants

- Unknown or unproven equivalence must not be accepted as a semantic match.
  Existence-only behavior must remain explicit and must not stand in for a
  complete table-definition check.
- Missing or conflicting safe-operation ownership must stop before target
  DDL and history success; ordinary EF operations remain provider-owned.
- Catalog data stays parameterized in analysis. Runtime migration scripts
  encode identifiers and literals through their owning provider services;
  opaque SQL must not authorize structural equivalence.
- Repair is an allowlist with exact prerequisites. Unexpected legacy objects
  are preserved unless an explicit reviewed operation owns their removal.
- Analysis must not execute target DDL or mark history applied. Runtime guards
  and postflight remain necessary; a read-only snapshot is not a write fence.
- Query chunks, cancellation, connection ownership, migration locks, and scoped
  cleanup must preserve their documented bounds and failure semantics.
- SafeMigrations-owned telemetry must exclude credentials, SQL, raw object or
  database names, row values, exception payloads, and caller instance IDs.
- Design-time scaffolding must rewrite only its documented table/index boundary,
  freeze the selected mode into source, and stop on an unrecognized generator
  shape or provider annotation instead of emitting ambiguous migration code.
- Release identity, authorized signing, provenance, qualified package content,
  and NuGet readback must agree before a release is accepted. A checksum alone
  is not proof of origin, and a model fingerprint is not authentication.

## Reportable findings and severity context

Report violations of these invariants with a reachable input-to-effect path:
SQL injection, guard or history bypass, unsafe repair, unintended object
mutation, cross-context confusion, sensitive telemetry, unbounded resource use,
poisoned session reuse, or release artifact substitution. Include the affected
package/engine, prerequisites, and realistic data-integrity, confidentiality,
or availability impact. Severity depends on that evidence, not the label of
the component or whether a test currently passes.

## Exclusions and unresolved ownership

No vulnerability class is automatically suppressed by this policy. Direct
upstream defects and application-owned authorization/network configuration
have different owners, but they may still expose a SafeMigrations integration
defect. Triage the actual boundary before routing a report. Coordinate private
upstream disclosure with the reporter; do not forward sensitive reports or
change another repository without permission.

## Limitations and compensating controls

MySQL/MariaDB DDL can commit before a later failure. Ordinary migration SQL
is not classified by read-only analysis, and a malicious database can lie
about its own catalog. Operators need least privilege, reviewed migrations,
a write fence, current backups with a restore drill, and per-instance
postflight/history checks. These limits do not excuse a broken guard within
the supported contract.

No blanket security certification, independent audit, badge, globally bounded
catalog size, or enabled hosted control is asserted. Review this policy when
the API, provider, SQL, resource, diagnostic, or release trust boundary changes.

## Supported Versions

This policy covers all three SafeMigrations packages. The initial complete
release is being prepared; a dated changelog entry is not proof of publication.

| Release state | Security support |
| --- | --- |
| 10.0.0 release-candidate line, once published | Report defects against the latest candidate; superseded candidates are not independently serviced |
| Stable 10.0.x, once published | Supported release line; fixes may require updating to its latest patch |
| Earlier proof-of-concept or other release lines | No separate servicing promise; reports are still triaged for impact on the supported contract |

Candidates qualify the same complete feature contract. This policy does not
schedule later feature releases. Dependency and engine support is defined in
[Support and qualification](docs/support-and-qualification.md).

## Reporting a Vulnerability

**Do not report vulnerabilities through public issues, pull requests, or discussions.**

Use [GitHub private vulnerability reporting](https://github.com/doka-labs/Doka.EntityFrameworkCore.SafeMigrations/security/advisories/new)
when it is available. Until the public repository has that feature enabled,
or if you cannot use it, email `doka-labs@tuta.com` with the subject
`SafeMigrations security report`. Absence of the GitHub form is not a request
to disclose publicly. If sensitive attachments require additional encryption,
first agree on a secure transfer channel; no public PGP key is advertised here.

Include as much of the following as possible:

- The affected package versions and exact MySQL, MariaDB, or PostgreSQL version.
- A minimal synthetic reproducer, starting schema/data shape, migration path,
  and the expected versus observed security boundary.
- Required privileges, configuration, attacker control, and potential impact.
- Known mitigations and any intended disclosure schedule.

Do not send production dumps, live credentials, private signing keys, customer
data, or unredacted connection strings. Ask before sending sensitive catalog
or report details. The repository-wide [security policy](#system-and-scope)
describes scope and invariants; uncertain ownership is still worth reporting.

## Response and coordinated disclosure

The maintainer handles intake, reproduction, severity/ownership assessment,
remediation, regression tests, release coordination, and advisory preparation.
The targets below are response objectives, not a paid support SLA or evidence
that past reports met them:

| Stage | Target |
| --- | --- |
| Acknowledgement | Within 5 business days |
| Initial triage | Within 10 business days; explain any information still needed |
| Fix and advisory | Within 90 days of confirmation, coordinated with the reporter; prioritize critical or actively exploited defects immediately |

Track public disclosure dates separately. A 90-day private coordination target
is not permission to leave a publicly known medium-or-higher vulnerability
unpatched beyond the OpenSSF 60-day criterion. Record actual response and fix
dates rather than deriving compliance from this table.

Keep the reporter informed if a target cannot be met. If no acknowledgement
arrives, follow up through the alternate private channel. Agree on disclosure
timing and credit; credit reporters unless they request anonymity. Do not
promise an embargo on behalf of a reporter without their agreement.

Fixes need positive and negative regression evidence and the normal release
identity checks. Publish affected/fixed versions, impact, mitigations, and
CVE/GHSA identifiers when available. Release notes identify publicly known
vulnerabilities fixed by the release. Preserve confidential reproduction
details until coordinated disclosure permits publication.

For upstream-owned findings, agree on private routing with the reporter.
A dependency boundary does not automatically exclude a SafeMigrations defect.
Review this policy when contacts, maintainers, release support, or security
boundaries change. [Secure development](docs/security/secure-development.md)
describes the corresponding engineering process.
