# Repository settings and public handoff

This is an operator checklist, not an assertion that settings are enabled.
Repository files cannot activate GitHub rules, security reporting, reviewer
independence, NuGet ownership, or an OpenSSF badge. Apply hosted changes only
through an explicitly authorized administration task and read back the result.

## Before public visibility

- Review the complete intended source/history and reachable refs for secrets,
  private endpoints, sensitive artifacts, and licensing. Rotate real exposed
  credentials at their issuer; deleting history is not revocation.
- Confirm repository name/description, MIT license and document-specific
  attribution, issue routing, conduct/security contacts, and documentation.
- Confirm the maintainer accepts any author identity already in public history.
  Do not rewrite history merely to satisfy a cosmetic preference.
- Review obsolete workflow artifacts/logs separately; their deletion needs
  explicit scope and loses evidence. Retain required release/incident records.
- Keep public visibility, Actions enablement, badge registration, and release
  publication as separate decisions. Publishing source does not publish NuGet.

## After public visibility, before accepting changes

| Control | Intended configuration and readback |
| --- | --- |
| Source and discussions | Public default `main`; anonymous README/source/issues access; working issue forms and support links |
| Ownership | Visible `@doka-labs/core-maintainers` team with explicit repository write access; no CODEOWNERS parse errors |
| Main protection | Reviewed pull requests, applicable required checks, resolved conversations, stale approval handling, restricted force-push/deletion and bypass |
| Check identity | Require `Full qualification / Required` and `dependency-review` only after each real green pull-request run submitted that exact context through GitHub Actions |
| Review independence | Reviewer distinct from author where claimed; record actual available roles, not just required-review count |
| Actions permissions | Read-only default token, no workflow PR approval, approved actions, fork approval policy, no privileged execution of untrusted PR code |
| Private vulnerability reporting | Enabled, external form reachable, responder subscribed to security alerts, fallback private channel tested |
| Dependency and secret alerts | Supported Dependabot/security-update and secret-scanning/push-protection controls enabled and alerts triaged; availability verified for the repository/organization |
| Analysis | Existing .NET analysis and engineering gate actually run; any additional code scanning selected explicitly, not assumed from this document |
| Account access | Maintainer/release access reviewed, MFA enforced at the appropriate organization/account scope, recovery and succession assessed |

The reusable workflow emits detailed jobs plus one fail-closed `Required`
aggregation job. It runs after package/Core, merged coverage, every MySQL and
MariaDB matrix cell, and every PostgreSQL matrix cell. `always()` ensures that
the aggregation job still reports a result after a dependency fails, is
cancelled, or is skipped; only four aggregate `success` results pass it.

The CI caller exposes this job as `Full qualification / Required`. Do not type
that context into the ruleset before GitHub Actions has submitted it. Keep the
existing required check during the transition, run CI once, select the new
context with GitHub Actions as its expected source, then remove the obsolete
partial check. Do not select `Any source`. A matrix-version change does not
rename this stable context, but every new qualification job must be added to
the aggregation job's `needs` and result check in the same change.

Dependency Review is a separate supply-chain decision rather than another
product matrix cell. GitHub Automatic Dependency Submission owns the resolved
NuGet snapshots. The read-only `dependency-review` workflow uses the official
action's bounded retry and rejects new high-or-critical vulnerabilities and
dependencies outside the approved license set. Action v5.0.0 itself proceeds
after its retry timeout when snapshot warnings remain; the following header
verification therefore requires GitHub's warning header to be present and
empty. The public REST reference did not specify this header on 2026-08-28;
the immutable action source supplies its verified name and encoding. A missing
header intentionally fails closed and requires vendor-contract review rather
than a bypass. After its first real green pull-request run, add
`dependency-review` to the `main` ruleset with GitHub Actions as the expected
source. Keep `Full qualification / Required` and `dependency-review` required
together; neither result substitutes for the other.

## Before the first candidate

Follow [one-time release configuration](../operations/release-publication.md#one-time-configuration):
protected `nuget` environment on `main`, authorized reviewer, explicit bypass
policy, `NUGET_USER`, NuGet Trusted Publishing scope, independently controlled
signer policy, protected `v*` tags, artifact attestations, immutable releases,
and evidence retention. A file declaring `environment: nuget` does not prove
an approval wait exists. Verify a real candidate reaches the wait before
creating its tag. Never dispatch or approve a release merely to inspect settings.

If only one release-capable person exists, do not assert separation of duties
or continuity. Reconcile actual reviewer availability and environment settings
before attempting publication; document the limitation in the evidence record.

## Read-only confirmation commands

These commands require suitable existing GitHub read permissions. They do not
enable controls, change visibility, start a workflow, or request publishing
credentials. Use only fields needed for the review; keep sensitive access
details in protected evidence.

```bash
repo='doka-labs/Doka.EntityFrameworkCore.SafeMigrations'
gh api --method GET "repos/$repo" \
  --jq '{visibility,default_branch,has_issues,has_wiki,security_and_analysis}'
gh api --method GET "repos/$repo/actions/permissions"
gh api --method GET "repos/$repo/actions/permissions/workflow"
gh api --method GET "repos/$repo/rulesets?includes_parents=true"
gh api --method GET "repos/$repo/branches/main/protection"
gh api --method GET "repos/$repo/environments/nuget"
gh api --method GET "repos/$repo/private-vulnerability-reporting"
gh api --method GET "repos/$repo/codeowners/errors"
gh api --method GET "repos/$repo/dependency-graph/sbom"
gh api --method GET "orgs/doka-labs/teams/core-maintainers/repos/$repo" \
  --jq '{full_name,permissions}'
```

For the qualified revision, read exact check contexts and the corresponding
workflow run/jobs:

```bash
candidate_sha='THE_REVIEWED_40_CHARACTER_COMMIT'
gh api --method GET "repos/$repo/commits/$candidate_sha/check-runs?per_page=100" \
  --paginate --jq '.check_runs[] | {name,status,conclusion,app:.app.slug,details_url}'
```

A 403/404 may indicate permissions, feature availability, or absent
configuration; it is not by itself proof that a security feature is disabled.
Inspect each ruleset's details, inherited rules, and bypass actors. An empty
branch-protection response does not rule out rulesets. Confirm GitHub UI-only
settings and NuGet policy separately; do not expose secret values in evidence.

Record date, actor, repository, source SHA, relevant responses, discrepancies,
and the resulting decision. Repeat after a role, workflow, ruleset, credential,
or hosting-plan change. Read back controls again before release until the
audited configuration is established; do not copy historical status forward.

## OpenSSF handoff

The public [OpenSSF Best Practices project](https://www.bestpractices.dev/projects/14265)
reports Passing and is linked from the README. Maintain its answers with the
[evidence matrix](../openssf-best-practices.md): recheck current criteria,
anonymous evidence URLs, operational history, and applicable exclusions, then
verify the public entry after every material update.

Treat OpenSSF Scorecard as a separate automated assessment. After its workflow
first runs on public `main`, verify the workflow conclusion, uploaded SARIF,
GitHub code-scanning result, Scorecard API response, and public viewer before
citing a score. Documentation or badge placement is not a substitute for
missing controls, people, or measured results.

## Primary sources

- [GitHub CODEOWNERS](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-code-owners),
  retrieved 2026-08-26.
- [GitHub Actions permissions API](https://docs.github.com/en/rest/actions/permissions),
  retrieved 2026-08-26.
- [GitHub rulesets](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets),
  retrieved 2026-08-26.
- [GitHub private reporting configuration](https://docs.github.com/en/code-security/how-tos/report-and-fix-vulnerabilities/configure-vulnerability-reporting/configure-for-a-repository),
  retrieved 2026-08-26.
- [GitHub Automatic Dependency Submission](https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/secure-your-dependencies/submit-dependencies-automatically),
  retrieved 2026-08-28.
- [GitHub Dependency Review](https://docs.github.com/en/code-security/concepts/supply-chain-security/dependency-review),
  retrieved 2026-08-28.
