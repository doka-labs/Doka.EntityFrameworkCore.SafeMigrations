# OpenSSF Best Practices preparation

## Status and scope

Preparation date: 2026-08-26. **No OpenSSF badge, project registration,
achievement percentage, or independent certification is claimed.** The
maintainer will handle registration and assessment after the repository is
public. This document prepares evidence; it does not submit answers, change
GitHub settings, or claim undocumented operational history.

The tables cover all **67 Passing, 55 Silver, and 23 Gold** criteria in the
[official snapshot](https://github.com/ossf/best-practices-badge/blob/27491e9c882d28612f1ca2d93a9d35263187e263/criteria/criteria.yml).
That immutable upstream revision is the inventory and review baseline; the
repository does not duplicate it in a generated local snapshot or parser.
Repeated IDs at different levels are intentional because requirements may
become stronger. Always re-read the live criteria before assessment.

Best Practices is a project self-assessment with evidence. Scorecard is a
separate set of automated checks. The OSPS Baseline is another maturity-control
framework (current release checked: 2026.02.19). The BadgeApp can host different
assessment series; a Passing/Silver/Gold answer does not establish Baseline
compliance or a Scorecard result. This page prepares the Best Practices series,
not a second full Baseline assessment or a claim of SLSA certification.

The repository-root [Scorecard annotations](../.scorecard.yml) document why
library dependency patch ranges are not applicable to the pinned-dependency
finding. They do not alter the published Scorecard score. Central Package
Management, committed lock files, locked restore, SHA-pinned Actions, and
digest-pinned container images remain the enforceable build controls.

## Reading the tables

- **Prepared:** a repository document/control exists; review and public evidence
  URLs are still needed. This is not an official `Met` answer.
- **Evidence:** execution results or observed history must be supplied.
- **External:** a hosted control or publication must be confirmed by its owner.
- **People:** knowledge, authority, continuity, or human review must be evidenced.
- **Open:** the stated practice is not currently established.
- **Assess:** decide applicability against the official wording; this is not an
  automatic `N/A`, exemption, or accepted risk.

Owners: **M** project maintainer/security responder; **R** authorized release
operator; **C** change author with reviewer. They are responsibilities, not a
claim that distinct people currently hold them. [Governance](../GOVERNANCE.md)
records the known authority and continuity limits.

Evidence keys below link to canonical documents rather than repeat their
contracts:

- [Guide](README.md), [API](api-reference.md), [Contributing](../CONTRIBUTING.md),
  [Support](../SUPPORT.md), [Conduct](../CODE_OF_CONDUCT.md).
- [Security intake](../SECURITY.md#reporting-a-vulnerability), [Design](security/security-design.md),
  [Development](security/secure-development.md), [Verification](security/release-verification.md).
- [Qualification](support-and-qualification.md), [Release](release-process.md),
  [Settings](runbooks/repository-settings.md), [Governance](../GOVERNANCE.md),
  [Roadmap](../ROADMAP.md), [Changelog](../CHANGELOG.md).

The repository-root [security policy](../SECURITY.md) is the canonical policy
and private intake procedure. Its presence does not establish that hosted
reporting is enabled, the contact has been tested, or response targets have
been met; those operational facts still need assessment evidence.

## Passing

| Criterion | Class | Readiness | Repository evidence | Remaining assessment evidence | Owner |
| --- | --- | --- | --- | --- | --- |
| `description_good` | MUST | Prepared | [README](../README.md) problem and package overview | Confirm anonymous public readability | M |
| `interact` | MUST | Prepared | [Guide](README.md), installation and support links | Confirm actual download and feedback routes after publication | M |
| `contribution` | MUST | Prepared | [Contributing](../CONTRIBUTING.md) PR process | Public URL to accepted process | M |
| `contribution_requirements` | SHOULD | Prepared | [Contributing](../CONTRIBUTING.md) tests/style/API rules | Public URL and review use | C |
| `floss_license` | MUST | External | [MIT license](../LICENSE), package metadata | Actual public release under stated license | R |
| `floss_license_osi` | SUGGESTED | Prepared | [MIT license](../LICENSE) | Check code license separately from attributed documentation | M |
| `license_location` | MUST | Prepared | [LICENSE](../LICENSE), [Conduct attribution](../CODE_OF_CONDUCT.md#attribution), [MADR reuse](decisions/MADR-PROFILE.md#primary-sources-and-reuse) | Confirm public license locations and scope | M |
| `documentation_basics` | MUST | Prepared | [README](../README.md), [deployment](runbooks/deployment-and-recovery.md) | Verify installation and safe-use examples on published packages | R |
| `documentation_interface` | MUST | Prepared | [API](api-reference.md), packaged XML, JSON Schema | Confirm exact-version IDE docs/package contents; comments alone are insufficient | R |
| `sites_https` | MUST | External | HTTPS repository/NuGet URLs | Read back public sites and download routes | M |
| `discussion` | MUST | External | [Support](../SUPPORT.md) issue/PR channels | Anonymous searchable archive and public participation | M |
| `english` | SHOULD | Prepared | English docs and issue forms | Accept reports/reviews in English | M |
| `maintained` | MUST | People | [Governance](../GOVERNANCE.md), [Roadmap](../ROADMAP.md) | Actual maintenance and response activity | M |
| `repo_public` | MUST | External | Repository is prepared for public visibility | Maintainer changes visibility and verifies anonymous source access | M |
| `repo_track` | MUST | Evidence | Git history | Public attribution/time/change history for the assessed revision | M |
| `repo_interim` | MUST | Evidence | Development commits and PR process | Public interim revisions, not only final archives | M |
| `repo_distributed` | SUGGESTED | Prepared | Git repository | Public clone access | M |
| `version_unique` | MUST | Prepared | [Release](release-process.md), version contract tests | Completed unique immutable release identities | R |
| `version_semver` | SUGGESTED | Prepared | [Changelog](../CHANGELOG.md), version parser | Actual released version/compatibility record | R |
| `version_tags` | SUGGESTED | External | [Release](release-process.md) signed tag procedure | Real tag after successful qualification, never before | R |
| `release_notes` | MUST | Prepared | [Changelog](../CHANGELOG.md) and release assets | Published version-specific human-readable notes | R |
| `release_notes_vulns` | MUST | Assess | [Development](security/secure-development.md) advisory handling | Check known CVE/GHSA fixes; justify no-applicable-fix case only from actual history | M |
| `report_process` | MUST | Prepared | [Support](../SUPPORT.md), issue forms | Public reporting URL and working form | M |
| `report_tracker` | SHOULD | External | Repository issue templates | Enabled accessible issue tracker | M |
| `report_responses` | MUST | Evidence | [Support](../SUPPORT.md) handling process | Acknowledge a majority of bug reports in the inclusive 2-12-month window; no invented N/A | M |
| `enhancement_responses` | SHOULD | Evidence | [Support](../SUPPORT.md) enhancement routing | Respond to more than half of qualifying requests in the inclusive 2-12-month window; no invented N/A | M |
| `report_archive` | MUST | External | Issue/PR archive design | Public URL and accessible historical reports | M |
| `vulnerability_report_process` | MUST | Prepared | [Security intake](../SECURITY.md#reporting-a-vulnerability) | Verify public discoverability and reporting instructions after publication | M |
| `vulnerability_report_private` | MUST | External | [Security intake](../SECURITY.md#reporting-a-vulnerability), [Settings](runbooks/repository-settings.md) | Enable/test private form and test the approved fallback contact | M |
| `vulnerability_report_response` | MUST | Evidence | [Development](security/secure-development.md) private response record | Every report in the last six months receives an initial response within 14 days; upstream permits N/A if none were received | M |
| `build` | MUST | Prepared | [Contributing](../CONTRIBUTING.md) locked SDK/build | Successful reproducible invocation from reviewed source | C |
| `build_common_tools` | SUGGESTED | Prepared | .NET SDK/MSBuild, shell, Python | Document required tools and execute build | C |
| `build_floss_tools` | SHOULD | Prepared | SDK and repository-owned scripts | Verify tool licensing; Rider is not required to build | C |
| `test` | MUST | Prepared | [Qualification](support-and-qualification.md) three suites | Complete results for the assessed commit/engine matrix | R |
| `test_invocation` | SHOULD | Prepared | [Contributing](../CONTRIBUTING.md) test commands | New contributor reproduces documented invocation with Docker | C |
| `test_most` | SUGGESTED | Evidence | [Coverage](support-and-qualification.md#coverage-gate), behavioral matrix | Actual scoped coverage and boundary tests | R |
| `test_continuous_integration` | SUGGESTED | External | [CI](../.github/workflows/ci.yml) | Enabled hosted runs on real proposed changes | M |
| `test_policy` | MUST | Prepared | [Contributing](../CONTRIBUTING.md) positive/negative tests | Review enforces test additions | C |
| `tests_are_added` | MUST | Evidence | [Development](security/secure-development.md) regression process | Recent changes demonstrate tests were added in practice | C |
| `tests_documented_added` | SUGGESTED | Prepared | [Contributing](../CONTRIBUTING.md#change-requirements) requires tests for major new functionality | Publish the change-proposal test policy and verify its use | C |
| `warnings` | MUST | Prepared | [Build props](../Directory.Build.props) | Warning-enabled build output | C |
| `warnings_fixed` | MUST | Evidence | Warnings-as-errors in build props | Clean assessed build and reviewed warning remediation | C |
| `warnings_strict` | SUGGESTED | Prepared | Recommended .NET analysis and strict build | Actual enabled rule set/build evidence | C |
| `know_secure_design` | MUST | People | [Design](security/security-design.md) review material | Primary developer demonstrates secure-design knowledge | M |
| `know_common_errors` | MUST | People | [Development](security/secure-development.md) and abuse cases | Primary developer demonstrates relevant weakness knowledge | M |
| `crypto_published` | MUST | Evidence | Framework SHA-256 fingerprints; release signing delegated | Inventory actual cryptographic use and supported algorithms | C |
| `crypto_call` | SHOULD | Prepared | [CanonicalHashWriter](../src/Doka.EntityFrameworkCore.SafeMigrations/Internal/CanonicalHashWriter.cs) uses framework crypto | No custom crypto implementation; assess tooling/providers too | C |
| `crypto_floss` | MUST | Evidence | .NET cryptography and Git/NuGet verification tools | Record implementation/toolchain provenance | C |
| `crypto_keylength` | MUST | Evidence | SHA-256 fingerprints, [Verification](security/release-verification.md) | Review current signing/TLS algorithms and strengths; not all crypto is N/A | R |
| `crypto_working` | MUST | Evidence | Fingerprint implementation and release trust boundaries | Inventory default security algorithms/modes and assess their suitability; functional tests alone do not prove cryptographic safety | R |
| `crypto_weaknesses` | SHOULD | Evidence | [Development](security/secure-development.md) dependency review | Check current advisories and actual algorithms | M |
| `crypto_pfs` | SHOULD | Assess | Transport delegated to connectors and hosting | Evaluate applicable TLS/key-agreement behavior, not fingerprint hashing | M |
| `crypto_password_storage` | MUST | Assess | No inbound user-authentication/password store | Distinguish outgoing DB credentials; justify applicability in the form | M |
| `crypto_random` | MUST | Assess | No product secret/key generation API | Inspect any security-sensitive randomness in dependencies/tooling before answering | C |
| `delivery_mitm` | MUST | Prepared | HTTPS sources, [Verification](security/release-verification.md) | Public download/signature/provenance verification | R |
| `delivery_unsigned` | MUST | Prepared | Checksums plus signed identity/attestations | Confirm no HTTP-only hash trust path | R |
| `vulnerabilities_fixed_60_days` | MUST | Evidence | [Development](security/secure-development.md) advisory dates | Current public vulnerability inventory and actual patched release dates | M |
| `vulnerabilities_critical_fixed` | SHOULD | Evidence | Private escalation process | Demonstrate prompt critical-fix handling, not a generic 90-day target | M |
| `no_leaked_credentials` | MUST | Evidence | [Development](security/secure-development.md) secret handling | Controlled scan of current reachable history/tree plus triage and revocation if needed | M |
| `static_analysis` | MUST | Evidence | SDK .NET analyzers beyond compiler warnings | Record enabled rules, SDK, assessed commit and successful analysis; justify coverage | C |
| `static_analysis_common_vulnerabilities` | SUGGESTED | Evidence | [Development](security/secure-development.md) analysis review | Prove relevant security rules actually enabled; no assumed CodeQL coverage | C |
| `static_analysis_fixed` | MUST | Evidence | Analysis findings and review records | Close applicable medium/high findings before release | C |
| `static_analysis_often` | SUGGESTED | External | Shared quality workflow | Actual run frequency and triggers | M |
| `dynamic_analysis` | SUGGESTED | Evidence | [Dynamic evidence](security/secure-development.md#dynamic-and-coverage-evidence) | Measured qualifying analysis; 75% MySQL branch floor alone is insufficient | R |
| `dynamic_analysis_unsafe` | SUGGESTED | Assess | Managed product source; provider/runtime boundaries | Inspect memory-unsafe/native scope and applicable analysis tools | C |
| `dynamic_analysis_enable_assertions` | SUGGESTED | Evidence | Release-mode tests and explicit runtime guards | Check required assertions/guards remain active during analysis | C |
| `dynamic_analysis_fixed` | MUST | Evidence | [Development](security/secure-development.md) regression discipline | Resolve relevant dynamic-analysis findings with tests | C |

## Silver

| Criterion | Class | Readiness | Repository evidence | Remaining assessment evidence | Owner |
| --- | --- | --- | --- | --- | --- |
| `achieve_passing` | MUST | External | Passing matrix above | Actual achieved Passing state | M |
| `contribution_requirements` | MUST | Prepared | [Contributing](../CONTRIBUTING.md) | Public enforceable contribution requirements | M |
| `dco` | SHOULD | Open | Contribution terms, MIT license | DCO or equivalent legal mechanism not adopted by this task; owner decision needed | M |
| `governance` | MUST | Prepared | [Governance](../GOVERNANCE.md) | Maintainer review and public availability | M |
| `code_of_conduct` | MUST | Prepared | [Conduct](../CODE_OF_CONDUCT.md) | Confirm private enforcement contact | M |
| `roles_responsibilities` | MUST | Prepared | [Governance](../GOVERNANCE.md#stewardship-and-roles) | Confirm actual authority/access for role holders | M |
| `access_continuity` | MUST | People | [Continuity](../GOVERNANCE.md#continuity-and-access) | Prove issue handling, change acceptance, and releases can resume within one week after confirmed loss of any one maintainer | M |
| `bus_factor` | SHOULD | People | [Governance](../GOVERNANCE.md) | At least two capable people or criterion-permitted justified disposition | M |
| `documentation_roadmap` | MUST | Prepared | [Roadmap](../ROADMAP.md) one-year direction/non-goals | Maintainer review; no invented second feature release | M |
| `documentation_architecture` | MUST | Prepared | [Implementation](implementation-design.md), [ADRs](decisions/README.md) | Reconcile accepted decisions and implemented behavior | C |
| `documentation_security` | MUST | Prepared | [Design](security/security-design.md) | Complete policy review and publish supported security requirements | M |
| `documentation_quick_start` | MUST | Prepared | [README](../README.md), [sample](../samples/Doka.EntityFrameworkCore.SafeMigrations.Sample/README.md) | Reproduce with published package graph | C |
| `documentation_current` | MUST | Prepared | [Guide ownership](README.md#ownership-and-evidence-discipline), doc gate | Per-change semantic review, not link validation alone | C |
| `documentation_achievements` | MUST | External | No achievement link is invented | Link actual achievements within 48 hours of public recognition; do not show an unearned badge | M |
| `accessibility_best_practices` | SHOULD | Evidence | Text-first docs, headings and named links | Review rendered public documentation accessibility | C |
| `internationalization` | SHOULD | Assess | English diagnostic codes/docs, Unicode identifier tests | Assess relevant user-facing localization requirements | M |
| `sites_password_security` | MUST | External | No project-owned login website; GitHub/NuGet hosting | Assess host authentication controls for applicable sites | M |
| `maintenance_or_update` | MUST | Prepared | [Support](../SUPPORT.md), [Roadmap](../ROADMAP.md) | Actual maintenance/update route and support-line handling | M |
| `report_tracker` | MUST | External | Issue forms | Public working tracker and justification | M |
| `vulnerability_report_credit` | MUST | Evidence | [Development](security/secure-development.md) credit process | Actual advisory credit/anonymity decisions or justified no-report case | M |
| `vulnerability_response_process` | MUST | Prepared | [Development](security/secure-development.md), security intake | Confirm the responsible responder and evidence of the documented private response process | M |
| `coding_standards` | MUST | Prepared | [Contributing](../CONTRIBUTING.md), [.editorconfig](../.editorconfig) | Review C#, Python, shell, YAML, and documentation conventions | C |
| `coding_standards_enforced` | MUST | Evidence | Build/style/import/doc gates | Verify enforced FLOSS-tool coverage; Rider-only layouts remain manually reviewed | C |
| `build_standard_variables` | MUST | Assess | Managed SDK build and package outputs | Inspect native-binary scope and CC/CFLAGS/CXX/CXXFLAGS/LDFLAGS propagation; upstream permits N/A if no native binaries are generated | C |
| `build_preserve_debug` | SHOULD | Prepared | Portable PDB/source-symbol packages | Qualified package and public symbol readback | R |
| `build_non_recursive` | MUST | Prepared | Solution/project-reference MSBuild graph | Inspect actual build graph and absence of recursive source-build misuse | C |
| `build_repeatable` | MUST | Evidence | Locked restore, deterministic double-pack | Repeat build evidence; double-pack alone is not independent rebuild | R |
| `installation_common` | MUST | Prepared | [README](../README.md#installation) NuGet packaging | Verify real installation and uninstallation using the package manager | R |
| `installation_standard_variables` | MUST | Prepared | NuGet/.NET standard restore mechanisms | Verify applicable SDK package-cache/source configuration | C |
| `installation_development_quick` | MUST | Evidence | [Contributing](../CONTRIBUTING.md) setup | Timed clean developer setup; Docker/image downloads accounted for | C |
| `external_dependencies` | MUST | Prepared | [Support](support-and-qualification.md), locks, SBOM | Exact release dependency graph including tool dependencies | R |
| `dependency_monitoring` | MUST | External | NuGet audit and Dependabot configuration | Enabled alert monitoring, responsible owner and actual triage | M |
| `updateable_reused_components` | MUST | Prepared | [Upgrade contract](efcore-provider-upgrade-risk.md) | Demonstrated reviewed dependency update with required evidence | C |
| `interfaces_current` | SHOULD | Evidence | PublicAPI baselines and public provider boundaries | Review deprecations/support for current released dependencies | C |
| `automated_integration_testing` | MUST | External | [CI](../.github/workflows/ci.yml) main-push trigger and automated suite | Prove tests and success/failure reporting for each check-in on at least one shared branch; one matrix run is insufficient | R |
| `regression_tests_added50` | MUST | Evidence | Regression policy and test corpus | Demonstrate regression tests for at least 50% of bugs fixed in the last six months | M |
| `test_statement_coverage80` | MUST | Evidence | [Coverage](support-and-qualification.md#coverage-gate) | Actual statement coverage or justified line-to-statement measure, not only configured floors | R |
| `test_policy_mandated` | MUST | Prepared | [Contributing](../CONTRIBUTING.md) acceptance requirements | Review enforcement | M |
| `tests_documented_added` | MUST | Prepared | [Contributing](../CONTRIBUTING.md#change-requirements) requires tests for major new functionality | Verify the documented change-proposal policy, not merely individual PR test descriptions | C |
| `warnings_strict` | MUST | Prepared | Strict build and analyzer settings | Successful release analysis output | R |
| `implement_secure_design` | MUST | Evidence | [Design assurance cases](security/security-design.md#assurance-cases) | Human trace of principles to controls and results | C |
| `crypto_weaknesses` | MUST | Evidence | Dependency/algorithm review process | Current weakness/advisory assessment | M |
| `crypto_algorithm_agility` | SHOULD | Assess | Versioned fingerprint contract; delegated signing/TLS | Assess upgrade paths without claiming a configurable hash algorithm | C |
| `crypto_credential_agility` | MUST | Evidence | Caller-owned credentials and release-signing configuration | Prove credential/key storage in separate files from other configuration, databases, and logs, and replacement without recompilation | R |
| `crypto_used_network` | SHOULD | Assess | HTTPS publication; connector-owned database transport | Verify required transport for the actual deployment; no universal TLS claim | M |
| `crypto_tls12` | SHOULD | Assess | Host/connector transport settings | Current applicable protocol/cipher evidence | M |
| `crypto_certificate_verification` | MUST | Assess | Connector/host owns certificate validation | Confirm no disabling of applicable validation in production setup | C |
| `crypto_verification_private` | MUST | Assess | Product TLS and HTTP dependency boundary | Determine applicability and prove certificate verification precedes private HTTP headers; release-signature verification is not this criterion | R |
| `signed_releases` | MUST | External | [Verification](security/release-verification.md), release gates | Actual signed release and user-verifiable trust bootstrap | R |
| `version_tags_signed` | SUGGESTED | External | Authorized SSH-signed tag procedure | Actual valid release tag | R |
| `input_validation` | MUST | Evidence | [Design](security/security-design.md) S1/S2/S4 | Positive/negative boundary tests on assessed revision | C |
| `hardening` | SHOULD | Evidence | Managed runtime and platform/dependency boundary | Identify and verify mechanisms that reduce exploitability of defects; upstream excludes least privilege as hardening evidence | C |
| `assurance_case` | MUST | Prepared | [Security design and assurance](security/security-design.md) | Maintainer/human review of complete arguments and evidence | M |
| `static_analysis_common_vulnerabilities` | MUST | Evidence | SDK analyzers and analysis policy | Actual enabled security-rule coverage and results | C |
| `dynamic_analysis_unsafe` | MUST | Assess | Managed source with external runtime/connector dependencies | Document applicable unsafe-code boundary and analysis evidence | C |

## Gold

| Criterion | Class | Readiness | Repository evidence | Remaining assessment evidence | Owner |
| --- | --- | --- | --- | --- | --- |
| `achieve_silver` | MUST | External | Silver matrix above | Actual achieved Silver state | M |
| `bus_factor` | MUST | People | [Governance](../GOVERNANCE.md) continuity limits | At least two capable maintainers; no assumption from two accounts | M |
| `contributors_unassociated` | MUST | People | Public contribution history once available | At least two unassociated contributors with nontrivial contributions in the past year; verify affiliation and contribution evidence | M |
| `copyright_per_file` | MUST | Open | Root/package copyright metadata | Complete language/generated-file inventory and per-file policy, not a partial header sweep | M |
| `license_per_file` | MUST | Open | MIT license and explicit document attributions | Complete per-file license evidence, including non-commentable/generated formats | M |
| `repo_distributed` | MUST | Prepared | Git repository | Public distributed clone/history access | M |
| `small_tasks` | MUST | External | Contribution process | Actual current contributor-friendly issues with acceptance criteria | M |
| `require_2FA` | MUST | External | [Settings](runbooks/repository-settings.md) access review | Enforced organization/release account MFA and scoped evidence | M |
| `secure_2FA` | SHOULD | External | Access/recovery procedure | Verify cryptographic 2FA; TOTP qualifies, SMS alone does not, and phishing resistance is stronger than the requirement | M |
| `code_review_standards` | MUST | Prepared | [Contributing](../CONTRIBUTING.md), [Governance](../GOVERNANCE.md) | Public review requirements and their use | M |
| `two_person_review` | MUST | People | Review process | Measured independent human review of at least half of changes before release; bots/AI do not supply independence | M |
| `build_reproducible` | MUST | Evidence | Deterministic settings and double-pack | Independent clean-environment bit-for-bit rebuild; same-build double-pack is insufficient | R |
| `test_invocation` | MUST | Prepared | [Contributing](../CONTRIBUTING.md) | Reproducible test invocation by a new contributor | C |
| `test_continuous_integration` | MUST | External | Shared CI workflow | Actual continuous execution and protected acceptance | M |
| `test_statement_coverage90` | MUST | Evidence | Product line floors above 90% | Measured statement evidence or justified mapping; exclude only legitimate non-product scope | R |
| `test_branch_coverage80` | MUST | Evidence | Core 80%, MySQL/MariaDB 75%, PostgreSQL 84% floors | Actual applicable branch coverage; current floor cannot guarantee this criterion | R |
| `crypto_used_network` | MUST | Assess | Hosting/connector transport boundaries | Actual required production transport evidence | M |
| `crypto_tls12` | MUST | Assess | Delegated TLS configuration | Verify applicable protocol/cipher settings | M |
| `hardened_site` | MUST | External | GitHub/NuGet hosted sites | Check applicable site headers/host controls; no project site is invented | M |
| `security_review` | MUST | People | [Design review record requirements](security/security-design.md#review-triggers-and-record) | Dated human security review within five years with scope/findings/resolution | M |
| `hardening` | MUST | Evidence | [Design](security/security-design.md) controls | Relevant platform/dependency/runtime hardening evidence | C |
| `dynamic_analysis` | MUST | Evidence | [Dynamic evidence](security/secure-development.md#dynamic-and-coverage-evidence) | Qualifying major-release analysis with measured scope/results | R |
| `dynamic_analysis_enable_assertions` | SHOULD | Evidence | Explicit runtime guards and tests | Assertion-enabled analysis configuration verified | C |

## Assessment handoff and maintenance

1. Make source and documentation publicly readable through a separate approved
   action. Run the [settings checklist](runbooks/repository-settings.md).
2. Re-read the live criterion wording and permitted dispositions. Do not infer
   N/A merely because evidence is absent; some response criteria do not permit it.
3. For every intended answer collect the exact public URL, commit/release,
   tool/run identity, measurement window, and responsible reviewer. Private
   evidence must be summarized without disclosing reports or credentials.
4. Review security-policy commitments, continuity, developer knowledge, and
   human-review claims with the actual owner. Documentation cannot create people.
5. Register and fill the chosen assessment after publication. Verify saved
   answers and public achievement state before adding an actual badge/link.
6. Reassess on release, criteria change, relevant finding, role change, or hosted
   configuration change. Update the pinned upstream revision only after a full
   table reconciliation; do not delete a difficult row to make the assessment
   appear more complete.

No criterion is marked officially met here. `Open`, `External`, `People`, and
`Evidence` are honest preparation outcomes, not planned missing product features
or permission to waive a release/security requirement.

## Primary sources

- [Passing criteria](https://www.bestpractices.dev/en/criteria/0),
  [Silver criteria](https://www.bestpractices.dev/en/criteria/1), and
  [Gold criteria](https://www.bestpractices.dev/en/criteria/2), retrieved 2026-08-26.
- [Official criterion snapshot](https://github.com/ossf/best-practices-badge/blob/27491e9c882d28612f1ca2d93a9d35263187e263/criteria/criteria.yml),
  retrieved 2026-08-26; IDs/classes were extracted independently of these tables.
- [Scorecard checks](https://github.com/ossf/scorecard/blob/main/docs/checks.md),
  retrieved 2026-08-26; automated signals are a distinct assessment.
- [OSPS Baseline release index](https://baseline.openssf.org/), retrieved
  2026-08-26; a separate baseline assessment is not implied by this mapping.
