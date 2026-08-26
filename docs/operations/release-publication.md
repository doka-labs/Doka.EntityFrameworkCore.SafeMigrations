# Release publication operations

Use this page for each RC or stable release, in this order:

1. Update reviewed `main` and remember its commit.
2. Run the local pre-tag check.
3. Start **Release candidate** on **main** with the version; wait for green
   qualification and the **nuget** approval pause.
4. Create, verify and push the signed tag with the commands below.
5. Approve that same waiting run, then confirm successful publication.

All three terminal blocks are on this page. Run them from the repository root
in the same terminal session. Execute each command separately, in order, and
check its output and exit status before continuing. Stop on any failure; these
are independent commands, not an automatically stopping script. Keep the
checkout unchanged while GitHub runs. RC and stable use the same procedure.

Complete [one-time setup](#one-time-setup) before the first release. The
[release process](../release-process.md) owns the qualification/integrity
contract; [consumer verification](../security/release-verification.md) owns
independent verification. This guide is the single home for operator commands.

## Normal publication

### 1. Prepare reviewed main

Merge the complete release preparation into protected `main` and require green
CI. The reviewed source must contain the intended `VersionPrefix`, exactly one
dated [changelog](../../CHANGELOG.md) entry for the full version, and the
correct API baselines, package metadata, dependencies and support/security
documentation. For stable, first apply [stable preparation](#stable-preparation).

Use a clean checkout of the intended repository. From its root, run:

```bash
git fetch origin main --tags
git switch main
git merge --ff-only origin/main
git status --short

release_commit="$(git rev-parse HEAD)"
test "${release_commit}" = "$(git rev-parse origin/main)"
```

If any command fails or status lists changes, stop. Do not discard local work
or force synchronization for a release. Keep `release_commit` in this terminal;
it is the commit the workflow and tag must identify, even if `main` later advances.

### 2. Check local preparation

Run:

```bash
./eng/pre-tag-check.sh
```

Require success. It checks the clean current-`main` checkout and the configured
SSH signing identity against the reviewed signer policy and GitHub signing-key
registration. It refreshes `origin/main`, without fetching or overwriting local
tags. It creates no tag, starts no workflow and requests no NuGet credential.

This is a local command, not a workflow input. It cannot qualify a workflow
run that does not exist yet. Its output shows the commit to compare with the
workflow in the next step.

### 3. Start qualification and wait

Open **Actions > Release candidate > Run workflow**. Select **main**, enter
the reviewed **version without `v`**, and start the workflow. Its run title
includes that version. Do not create a tag yet.

In that run, wait for **Validate candidate identity and unused version**, every
**Full reversible qualification** job, and **Attest qualified candidate** to
succeed. **Verify tag, publish, and read back** must then wait for review of
environment **nuget**. Do not approve it yet.

Before creating the tag, check in this same run:

- Its version is the one you intend to publish and its commit matches the
  commit printed by step 2 (`release_commit`).
- All qualification and attestation jobs succeeded; publication is still
  waiting for **nuget** approval.
- Its `safe-migrations-release-<version>-<attempt>` and
  `safe-migrations-attestations-<version>-<attempt>` artifacts are available,
  not expired. After a retry, the successful producer attempts may differ.

Keep this run open for step 5. If its commit differs, stop and prepare/qualify
the intended commit again; do not change `release_commit` to make a mismatch
disappear. A failed qualification consumes no release tag or NuGet version.

### 4. Create and push the tag

Only after step 3 is green and waiting, return to the same terminal. Replace
`<release_version>` with exactly the version entered in the workflow, without
`v`. Do not recalculate `release_commit` from a newer checkout.

```bash
release_version="<release_version>"
release_tag="v${release_version}"

release_status="$(git status --porcelain --untracked-files=all)"
test -z "${release_status}"
./eng/release/validate-version.sh "${release_version}"
./eng/release/verify-main-source.sh "${release_commit:-}"

git tag -s "${release_tag}" "${release_commit}" \
  -m "Doka.EntityFrameworkCore.SafeMigrations ${release_version}"
./eng/release/verify-tag.sh "${release_tag}" "${release_commit}"
git push --no-follow-tags origin "refs/tags/${release_tag}:refs/tags/${release_tag}"
```

Create the tag only after all preceding checks succeed, and push only after
tag verification succeeds. The source check requires the unchanged candidate
and refreshes its protected-main ancestry. The tag check verifies both its
commit and signature against the repository's authorized signers. The chosen
run's qualified/waiting state is your check in step 3, not something this
terminal block discovers.

Do not rerun the block blindly after an error: a local or remote tag might
already exist. Follow [recovery](#recovery). Never move, delete, reuse or
force-push a release tag. Push only this tag, never `git push --tags`.
Pushing the tag starts no second release workflow.

### 5. Approve the same waiting run

After the tag block succeeds, the authorized reviewer returns to **that same
run**, checks its version/commit/tag and selects **Review deployments > nuget >
Approve and deploy**. Record the release identity in the approval comment.
Do not bypass protection rules or start a separate publishing run.

The existing job verifies the tag and original qualified bytes again, stages
the complete GitHub draft, submits missing NuGet packages/symbols, publishes
the immutable GitHub Release and completes public NuGet verification. It does
not rebuild or repack. Approval permits these external publication actions.

### 6. Confirm completion

Require the **entire run to finish successfully**, including the final NuGet
signature, package-content and symbol readback. A visible GitHub Release or an
accepted upload alone is not completion.

On the release page, confirm prerelease/latest presentation: an RC is a
prerelease and must not replace latest stable; a stable release must be latest.
The workflow sets this policy, but the actual latest selection still needs
this UI check. Coordinate concurrent releases so another publication does not
change latest during acceptance.

The run retains its qualification and publication evidence automatically; no
manual artifact inventory is required to create the tag. Apply the
[evidence-retention policy](#evidence-and-independent-verification), including
any required archive before expiry. Failed runs remain incomplete: use
[recovery](#recovery), not a manual NuGet upload.

## One-time setup

Complete the [repository settings handoff](../runbooks/repository-settings.md)
with the responsible administrator. Configuration changes require their own
approval; this guide does not authorize them.

- Protect `main` with review and the actual successful CI check contexts.
  `Full qualification` is a caller-job label, not a guaranteed aggregate
  required check. Include matrix, coverage and dependency-profile jobs.
- Configure environment `nuget` with deployment restricted to `main`, required
  reviewers and administrator bypass disabled. When self-review is prevented,
  ensure another authorized reviewer is available.
- Store the NuGet **profile name**, not an email address, as environment secret
  `NUGET_USER`. The selected account/owner must cover all three package IDs.
  Do not configure a long-lived `NUGET_API_KEY`.
- Verify this exact active NuGet Trusted Publishing policy and its owner:

| NuGet policy field | Required value |
| --- | --- |
| Repository owner | `doka-labs` |
| Repository | `Doka.EntityFrameworkCore.SafeMigrations` |
| Workflow file | `release-candidate.yml` (file name only) |
| Environment | `nuget` |

An organization name alone does not establish package ownership. Pending
policies can have a limited validation window; inspect their actual status.
Do not trigger OIDC login or publication to inspect configuration. See
[NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).

- Register the maintainer's SSH public signing key with GitHub and the same
  principal/key in [allowed-signers](../../eng/release/allowed-signers).
  Key rotation must land on `main` before qualification. Configure local
  `gpg.format=ssh`, `tag.gpgSign=true`, matching `user.email` and
  `user.signingKey` pointing to that public-key file. The private key must be
  available through the approved signing mechanism; never copy it into CI.
- Inspect both fetch and push destinations of `origin`; both must identify
  the intended repository. Keep the local GitHub CLI repository selection
  aligned with that origin. Recheck after remote/configuration changes.
- Restrict creation of `v*` tags and prevent updates/deletion through a
  repository ruleset. Limit dispatch access to release operators and keep
  publication independently protected by environment approval.
- Confirm artifact attestation availability and enable release immutability
  under **Settings > General > Releases**, or enforce it at organization level.
  It protects future published releases, not merely drafts. See
  [immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases)
  and [configuration](https://docs.github.com/en/code-security/how-tos/secure-your-supply-chain/establish-provenance-and-integrity/prevent-release-changes).
- Permit the requested 90-day Actions artifact retention and arrange longer
  organizational retention where required. Record actual expiry.

Local helpers need Git with SSH signing support, authenticated GitHub CLI,
Bash, Python 3 and `jq`. Build prerequisites remain in
[CONTRIBUTING.md](../../CONTRIBUTING.md). Independent NuGet signature readback
also needs the supported host described in the consumer guide.

Read back these controls before release until an audited configuration exists,
and after relevant role, policy, key or workflow changes. YAML
`environment: nuget` does not establish required reviewers. Availability
depends on repository visibility and GitHub plan. If publication can start
without approval, stop. See
[deployment review](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/review-deployments).

## Automated checks and source identity

There is one [pre-tag helper](../../eng/release/pre-tag-check.sh), reached
through [eng/pre-tag-check.sh](../../eng/pre-tag-check.sh):

- With no arguments it checks preparation for an untagged current-`main`
  dispatch. It cannot check an unused version until the version is selected.
- Its optional diagnostic mode accepts all three arguments:
  `--version <version> --commit <sha> --run-id <id>`. It additionally checks
  the unused local/remote tag, the exact waiting workflow, all successful
  prerequisites and unexpired package/attestation artifacts through GitHub's
  API. Partial arguments fail rather than falling back to preparation.

The normal path uses the zero-argument preparation check and the operator's
explicit run/version/commit, green-job and available-artifact checks in step 3.
It does not discover a run or automatically perform the optional live-run
diagnostic before tagging. Publication independently rechecks source, tag and
qualified artifact integrity before publishing. The command sequence has one
maintenance location: this page, not generated workflow instructions.

Initial hosted preflight requires the dispatched SHA to equal current `main`.
Afterwards, unrelated commits may advance `main`: the documented source check
and publication require the exact candidate checkout and its continued
ancestry on freshly fetched protected `origin/main`. They do not require the old candidate
to remain the tip. Never update the release checkout while tagging it. A
candidate removed from protected history fails closed. Candidate changes
require a new reviewed qualification, not a SHA override.

The helper refreshes only the main remote-tracking ref without fetching or
overwriting local tags; it is not a zero-mutation command. Signing checks bind
both the reviewed allowed-signers file and the maintainer's GitHub signing-key
registry. `git tag -s` creates an annotated signed tag on the explicit commit;
a signed commit is not a substitute. The documented push uses an exact refspec
and `--no-follow-tags` so `push.followTags` cannot send unrelated tags. See
[git-tag](https://git-scm.com/docs/git-tag) and
[git-push](https://git-scm.com/docs/git-push).

Qualification, attestation and publication may have different producing
attempts after failed-job retries. Publication consumes each successful
producer's original artifact rather than requiring all suffixes to equal the
current attempt. The optional live-run diagnostic checks those identities too.
Missing, expired or ambiguous evidence is a reason to stop before tagging and
blocks publication if first detected there.
Dispatch does not lock `main`; concurrency is version-scoped. See
[GitHub concurrency](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency).

Publication verifies exact source/tag/signature, package checksums/contents,
provenance and SPDX 2.2 SBOM attestations before any NuGet write. The complete
fixed [eleven-asset draft](../security/release-verification.md#qualified-assets-and-provenance)
is read back before OIDC login. Credential-free NuGet preflight reconciles
existing primary payloads and public symbols; only missing content permits
login/push. The job finalizes and reads back the immutable GitHub Release,
then completes full public NuGet signature/content/PDB verification. See
[the publication contract](../release-process.md#approve-and-publish).

Do not edit/publish the draft manually. After finalization, the GitHub Release
can already be public while NuGet validation remains pending. Cancelling the
job cannot undo accepted uploads or an immutable release. Only complete
successful verification accepts the coordinated three-package release.

## Evidence and independent verification

The workflow retains these artifacts with their actual producer attempts:

| Artifact | Contents |
| --- | --- |
| `safe-migrations-release-<version>-<qualification-attempt>` | Three `.nupkg`, three `.snupkg`, `SHA256SUMS`, `SYMBOLS.json`, performance results, and `sbom/_manifest/spdx_2.2/manifest.spdx.json`. These are qualified pre-NuGet bytes. |
| `safe-migrations-attestations-<version>-<attestation-attempt>` | `build-provenance.sigstore.json` and `sbom-attestation.sigstore.json` for the qualified downloads. |
| `safe-migrations-publication-<version>-<publication-attempt>` | Available attempt identity, step outcomes, logs, GitHub results and downloads, including failure diagnostics. Successful readback adds repository-signed packages, public PDBs and `SIGNED_SHA256SUMS`. |

Publication evidence under `artifacts/release-publication` contains:

- `attempt.json`: repository, commit, version, tag, run, attempt, and original
  qualification/attestation artifact names;
- `source.log`, `tag.log`, `qualified-bytes.log`, `attestations.log`;
- `github-tag.json`, `github-staged.json`, `github-published.json`: successful
  results or failure diagnostics with unknown remote state, without credential
  headers;
- `nuget-preflight.log`, `nuget-push.log`, `nuget-readback.log`;
- `outcome.json`: explicit step outcomes including setup/download failures;
- `nuget-readback/`: downloaded payloads and, after verification, `symbols/`
  and `SIGNED_SHA256SUMS`.

Stages not reached cannot supply results. Uploaded diagnostics do not prove
verification success. Retain failed-attempt evidence alongside later success.

For audit and long-term retention, retain the version/tag/commit, run URL/ID,
all relevant attempts, artifact IDs/names/expiry, reviewer/approval record,
qualification logs, coverage, performance, engine/tooling results, SBOM,
bundles, manifests and successful public readback. Not all qualification
evidence is a GitHub Release asset. Preserve Actions artifacts before expiry;
the requested 90 days is not a long-term archive. Manual transcription is not
a pre-tag gate; retaining the actual run/artifacts is the evidence obligation.

Use [consumer verification](../security/release-verification.md) for an
independent audit of the published packages, symbols, assets and attestations.
All three IDs must expose the same selected version. NuGet adds repository
signatures, so signed archive hashes differ from qualified hashes; canonical
comparison permits only `.signature.p7s`. `SIGNED_SHA256SUMS` records signed
package/PDB hashes and is not a detached signature. It and other observations
remain attempt evidence, never extra immutable GitHub Release assets.

## Recovery

First inspect the failing run/attempt/step and actual tag, draft/release and
NuGet state. A failed response does not prove its remote write did not happen.

| Observed state | Safe next action |
| --- | --- |
| Qualification failed; tag and all three NuGet versions are absent | Fix through reviewed `main` and qualify the still-unused version again. A transient same-source failure may rerun failed jobs; repeat every check in step 3 before tagging. |
| `main` advanced after initial preflight | Keep the original candidate checkout/run/artifacts; continue only while its exact SHA remains on freshly fetched protected `main` history. |
| Waiting candidate was superseded or removed from protected history | Reject/cancel the deployment and qualify current reviewed `main`. Reuse the version only while its tag and every NuGet version remain absent. |
| Terminal session was lost while qualification ran | Recover `release_commit` from the exact commit shown in that same run, not from current `main`. Use a clean checkout of that commit, repeat the checks in step 3, then continue with step 4. Its source check must pass. |
| Tag block failed before tag creation | Resolve the failed command. If the cleanliness check failed, inspect `git status --short`. Repeat step 3 and retry step 4 only while the same candidate is qualified, waiting and untagged. |
| Local tag exists but push failed or its response was lost | Inspect local and remote state and verify the existing tag against the candidate/signature. If absent remotely, push only that verified existing tag. If already present remotely, verify it and continue the original run. Do not rerun tag creation. |
| Valid tag exists; only publication failed; source/evidence remain valid | Use **Re-run failed jobs** in the same run and satisfy environment review again if requested. Do not rerun all jobs or dispatch a full replacement run: initial preflight rejects the existing tag. |
| Wrong SHA/key/version, invalid ancestry, or source/workflow/tool fix required | Stop that attempt. Preserve existing tags/payloads; qualify corrected reviewed `main` under a new unused version. A retry keeps the original SHA/ref and cannot import a fix. |
| Draft/asset/publication response was lost | Same-run failed-publication retry reconciles actual state and the same eleven assets. Matching partial drafts receive only missing assets; conflicts or ambiguity stop recovery. |
| Some NuGet primary packages or symbols already exist | Same-run retry compares canonical primary content and exact public symbols without credentials. Matching content is not pushed again; missing content may be pushed. Full final verification remains mandatory. |
| Exact primary payload is visible without its repository signature | Pending validation, not completion or a content conflict. Let bounded readback continue or retry the failed publication job with usable original evidence. Invalid signatures and different payloads are terminal. |
| All packages/symbols already exist exactly | Same-run retry reconciles the draft or verifies the matching immutable release read-only and completes public readback without another NuGet login. Never repack/replace assets. |
| Immutable GitHub Release is public but final NuGet verification failed | Preserve the release unchanged; retry only failed publication in the same run. Exact metadata/assets must still match before completing pending checks. |
| Package/PDB/draft/asset/signature/attestation conflict | Stop, preserve evidence and investigate corruption/substitution; use the private security channel if appropriate. Check bypasses or forced replacement are not recovery. |
| Public indexing/validation or GitHub availability delays completion | Inspect service status/logs. Public readback has one shared 3,600-second deadline, not a per-package deadline or external SLA. Retry only with the same identity and usable evidence. |
| Evidence expired/deleted, retry limit reached, or run cancelled irrecoverably | Stop for maintainer recovery. A local rebuild is not the original qualified artifact. Preserve existing identity/evidence; qualify a new reviewed candidate/version if same-run recovery is unavailable. An untagged entirely unused version may be qualified afresh. |

GitHub **Re-run failed jobs** reruns failed jobs and their dependents, not a
replacement source revision. Verify that only expected publication work is
retried. Reruns are limited to 30 days after the initial run and 50 reruns;
90-day artifact retention does not extend that window. See
[failed-job semantics](https://docs.github.com/en/rest/actions/workflow-runs#re-run-failed-jobs-from-a-workflow-run)
and [original SHA/ref retention and limits](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs).

NuGet upload acceptance and public validation/indexing are distinct. Existing
package ID/version pairs cannot be overwritten, and unlisting does not free
them for reuse. See
[NuGet publishing API](https://learn.microsoft.com/en-us/nuget/api/package-publish-resource)
and [validation/indexing](https://learn.microsoft.com/en-us/nuget/nuget-org/publish-a-package#package-validation-and-indexing).

## Stable preparation

For initial RCs, public API entries remain in `PublicAPI.Unshipped.txt` and
initial `PublicAPI.Shipped.txt` files contain only `#nullable enable`. After
accepting the complete RC, review and merge stable preparation: move accepted
entries to shipped baselines, preserve nullable markers, add the dated stable
changelog entry and verify package/support/security metadata. The stable
version must match reviewed `VersionPrefix`; locked dependencies include
stable Doka 10.0.0, with the whole graph reviewed.

Repeat [normal publication](#normal-publication) with that stable version and
its newly qualified current-`main` SHA. Do not rename RC packages, move the RC
tag or merely change a prerelease flag. Stable is a distinct package identity
with the same complete qualification, not a planned feature subset.

## Maintenance and source verification

Platform semantics were checked against the linked GitHub, Git and Microsoft
primary documentation on 2026-08-26. Source, artifact and retry contracts come
from the repository implementation. Doka's provider-specific stages are not
SafeMigrations requirements. Local tests and documentation do not establish
the hosted environment settings or replace the first authorized RC run.
