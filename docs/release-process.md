# Release process

SafeMigrations uses one manually dispatched qualification and publication
workflow for every canonical package version. The operator selects `main` in
the GitHub Actions branch selector and enters the package version without a
leading `v`. The workflow contains no push or tag trigger and no fixed release
line. It derives the permitted release line from the reviewed
`VersionPrefix` in `src/Directory.Build.props`.

The release identity is created only after all reversible work has passed:

1. qualify the exact current `main` commit while the requested tag does not
   exist;
2. attest the qualified package bytes and evidence;
3. wait at the protected `nuget` environment;
4. create one signed annotated `v<version>` tag on the qualified commit;
5. approve the waiting publication job; and
6. bind the tag, verify the attestations, stage the GitHub Release draft,
   publish the already qualified bytes, read them back, and publish the
   completed GitHub Release.

A failed qualification therefore consumes neither a Git tag nor a NuGet
version. Release candidates and stable releases exercise the same controls and
three-package topology. Their only intentional differences are the entered
version and the resulting GitHub prerelease/latest state.

## One-time repository configuration

Before the first hosted run:

1. Protect `main` and require the CI `Full qualification` result before merge.
2. Create a GitHub environment named `nuget` with:
   - deployment restricted to `main`;
   - the intended required reviewer or reviewers;
   - self-review disabled when release separation of duties is required; and
   - administrator bypass disabled.
3. Store only the NuGet account name as the `NUGET_USER` environment secret.
   Do not store a long-lived NuGet API key.
4. In nuget.org, create a Trusted Publishing policy with:
   - repository owner: `doka-labs`;
   - repository: `Doka.EntityFrameworkCore.SafeMigrations`;
   - workflow file: `release-candidate.yml`; and
   - environment: `nuget`.
5. Register the release maintainer's signing key with GitHub and require
   restricted, non-deletable `v*` tag creation through a repository ruleset.
   Add the same public key and principal to the reviewed
   `eng/release/allowed-signers` trust policy. A key rotation must update both
   GitHub and that file through a reviewed change on `main` before the next
   qualification.
6. Limit workflow dispatch permission to release operators. Keep NuGet
   publication independently protected by the `nuget` environment reviewer;
   permission to start qualification is not permission to publish.
7. Enable GitHub artifact attestations and immutable releases. Retain
   qualification, attestation, and readback artifacts according to
   organizational policy.

GitHub required reviewers are available for public repositories on current
plans. Private repositories require a plan supporting the selected environment
controls. The release must not proceed if the `nuget` job can start without an
explicit approval. Hosted repository settings are part of the release gate,
not facts established by the workflow files: read them back before every
release until the repository has enforced rules and an audited configuration.

NuGet Trusted Publishing supplies a short-lived API key only inside the
protected publication job. Checkout credentials are never persisted. A
private-repository Trusted Publishing policy can initially be active for a
limited validation window; create or renew that window only for an intended
publication.

## Candidate preconditions

Before dispatching the workflow, verify all of the following:

- the intended source is reviewed and merged into protected `main`;
- CI `Full qualification` is green for the current `main` commit;
- the complete prerelease API is recorded in every
  `PublicAPI.Unshipped.txt`, while each initial `PublicAPI.Shipped.txt` contains
  only `#nullable enable`;
- package metadata, `CHANGELOG.md`, support documentation, and the security
  policy describe the intended complete contract;
- locked restore resolves exact Doka 10.0.0 and no prerelease dependency;
- all three package IDs are owned by the configured NuGet account or are still
  available;
- the requested version is absent from every package ID; and
- `main`, the `nuget` environment, `NUGET_USER`, Trusted Publishing, signing
  identity, tag rules, attestations, and immutable releases have been read back
  from their hosted configuration.

The synchronized package IDs are:

1. `Doka.EntityFrameworkCore.SafeMigrations`;
2. `Doka.EntityFrameworkCore.SafeMigrations.MySql`; and
3. `Doka.EntityFrameworkCore.SafeMigrations.PostgreSql`.

## Start an untagged qualification

Open GitHub Actions, select `Release candidate`, and choose `Run workflow`.
Select branch `main` and enter the package version without `v`, for example
`10.0.0-rc.1`.

The preflight accepts canonical lowercase NuGet versions of at most 64
characters in the form `MAJOR.MINOR.PATCH` with an optional prerelease suffix.
The stable triplet must equal the reviewed source `VersionPrefix`, and
`CHANGELOG.md` must contain exactly one dated entry for the full requested
version. It rejects a leading `v`, leading zeroes in numeric identifiers,
uppercase prerelease identifiers, build metadata, a different source release
line, an existing requested tag, a stale `main` SHA, and any package version
whose NuGet primary endpoint is not definitively absent.

`eng/release/validate-version.sh` is the single version parser used by the
workflow and can be run locally:

```bash
package_version='10.0.0-rc.1'
eng/release/validate-version.sh "$package_version"
```

The example version is documentation only. The workflow contains no hardcoded
release tag or release line; changing the line requires a reviewed source and
changelog change rather than an arbitrary dispatch input.

## Qualification and protected wait

Before the publication job can enter the protected environment, the reusable
`.github/workflows/quality-gates.yml` workflow completes:

- locked restore, architecture and policy gates, warning-free Release build,
  Core tests, and public API analysis;
- construction, generation, fingerprint, and serialization performance and
  allocation budgets;
- six MySQL/MariaDB and five PostgreSQL live engine cells;
- EF CLI, normal, idempotent, no-transaction, and Migration Bundle paths in
  every engine cell;
- the isolated Latest EF/Npgsql dependency profile;
- deterministic double-pack, exact package contents, and package-only
  consumers; and
- Microsoft SBOM Tool generation and validation.

The workflow uploads one attempt-qualified immutable artifact containing the
six package files, `SHA256SUMS`, `SYMBOLS.json`, performance results, and SPDX
manifest. A separate job downloads and verifies those bytes, creates build
provenance and SBOM attestations, and preserves their Sigstore bundles.

Only after those jobs succeed does `publish` enter the `Waiting` state on the
protected `nuget` environment. Do not approve it yet. Verify that every
preflight, qualification, engine, package, SBOM, and attestation job is green,
and record the workflow run ID, attempt, entered version, and exact 40-character
candidate SHA shown by GitHub.

## Create the release identity

Create the tag only while that exact run is waiting. Use the requested version
and the exact candidate SHA; do not infer the SHA from a later local checkout.

```bash
package_version='10.0.0-rc.1'
candidate_sha='COPY_THE_40_CHARACTER_RUN_SHA'
run_id='COPY_THE_WORKFLOW_RUN_ID'
release_tag="v$package_version"

eng/release/pre-tag-check.sh \
  --version "$package_version" \
  --commit "$candidate_sha" \
  --run-id "$run_id"
git tag -s "$release_tag" "$candidate_sha" \
  -m "SafeMigrations $package_version"
eng/release/verify-tag.sh "$release_tag" "$candidate_sha"
git push origin "refs/tags/$release_tag"
```

The pre-tag gate requires a clean checkout at the exact current `main` commit,
the exact workflow run and current attempt in its qualified protected wait,
the two unexpired attempt-qualified artifacts, an unused local and remote tag,
and an SSH signing key authorized both by the repository trust policy and the
authenticated maintainer's GitHub signing-key registry.

Push only the exact tag. Never use `git push --tags`, never create a
lightweight tag, and never move, reuse, or delete a release tag. Confirm on
GitHub that the tag signature is shown as verified before approving
publication.

## Approve and publish

Approve the waiting `nuget` deployment only after the signed tag is visible.
The same run then:

1. fetches the requested tag and current `main`;
2. proves the checked-out SHA is still the exact current `main` commit and the
   tag resolves directly to that SHA;
3. requires an annotated tag whose SSH signature is authorized by
   `eng/release/allowed-signers`, then independently requires GitHub's Git
   database API to report a verified signature;
4. re-verifies the downloaded qualified checksums and package contents;
5. verifies the downloaded provenance and SPDX SBOM Sigstore bundles against
   the exact repository, workflow, `main` ref, and qualified commit;
6. creates or reconciles an exact draft GitHub Release and verifies every
   pre-publication asset before obtaining a publication credential;
7. performs a credential-free NuGet readback, rejecting conflicting existing
   primary packages or symbols and determining whether anything is missing;
8. only when content is missing, exchanges GitHub OIDC for a short-lived NuGet
   credential and publishes missing Core, MySQL/MariaDB, and PostgreSQL
   packages and symbols with duplicate-tolerant pushes;
9. verifies NuGet repository signatures, canonical package content, and public
   Portable PDB identity; and
10. adds the signed NuGet readback evidence to the draft, verifies the complete
    asset set, and publishes the GitHub Release as the final mutation.

The workflow never restores, rebuilds, or repacks inside `publish`. The bytes
that passed qualification are the bytes sent to NuGet.

For a prerelease version, GitHub `prerelease` is true and `make_latest` is
false. For a stable version, `prerelease` is false and `make_latest` is true.

## Candidate acceptance

Accept a release only after all of these readbacks succeed:

- every workflow job is green;
- all three NuGet package versions are visible and repository-signed;
- all public Portable PDBs match the identity and checksum sealed into their
  candidate assemblies;
- downloaded primary package content matches the qualified content after
  removal of NuGet's repository-owned `.signature.p7s` entry;
- the GitHub Release targets the qualified commit, has the correct
  prerelease/latest state, and contains the exact expected assets; and
- qualification, attestation, SBOM, checksum, and NuGet readback evidence all
  identify the same workflow run, commit, version, and exported artifact.

## Failure and recovery

- If qualification fails before tag creation, no identity is consumed. Fix the
  cause on `main` and dispatch the still-unused version again. A transient
  same-run failure can use GitHub's failed-job rerun.
- If the waiting candidate is rejected or superseded before tagging, reject or
  cancel the deployment. The version remains reusable while all three NuGet
  versions and the requested tag remain absent.
- If source, dependency, package metadata, workflow, or documentation changes
  after qualification, do not tag the old SHA. Reject the waiting deployment
  and qualify the new current `main` commit.
- After a valid tag exists, rerun only failed publication jobs in the same
  workflow run. Do not start a new full run for that tag, because preflight
  intentionally requires the requested tag to be absent.
- If a tag was created with the wrong SHA, name, or signing identity, preserve
  it as immutable evidence and use a new version after correcting the cause.
- Partial NuGet publication is resumed only by the same run. Existing primary
  and symbol content must verify exactly during the credential-free preflight
  before it is skipped; conflicting or unsigned content is terminal for that
  version. If every payload already exists exactly, the rerun does not request
  another NuGet credential.
- If public indexing, signature readback, or GitHub Release reconciliation
  times out after publication, rerun the failed `publish` job. Matching bytes
  and draft assets are reused, missing pushes tolerate a publication race, and
  any content or metadata conflict continues to fail closed.

## Stable release

The stable release uses the same manual workflow and complete qualification.
First move the accepted public API entries from `PublicAPI.Unshipped.txt` to
`PublicAPI.Shipped.txt`, add the stable changelog entry, and merge that reviewed
release change into `main`. Then enter the stable package version, allow the
workflow to reach the protected wait without its requested tag, create the
signed annotated `v<version>` tag on that run's exact SHA, and approve
publication. Prerelease archives are not promoted or renamed; stable packages
are newly qualified deterministic outputs with their own NuGet identity.

## Primary references

- [NuGet package versioning](https://learn.microsoft.com/nuget/concepts/package-versioning)
- [NuGet prerelease packages](https://learn.microsoft.com/nuget/create-packages/prerelease-packages)
- [NuGet Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing)
- [NuGet symbol packages](https://learn.microsoft.com/nuget/create-packages/symbol-packages-snupkg)
- [NuGet package signatures](https://learn.microsoft.com/nuget/reference/signed-package-verification-options)
- [dotnet nuget push](https://learn.microsoft.com/dotnet/core/tools/dotnet-nuget-push)
- [GitHub manual workflow dispatch](https://docs.github.com/actions/how-tos/write-workflows/choose-when-workflows-run/manually-run-a-workflow)
- [GitHub secure use and script injection](https://docs.github.com/actions/security-guides/security-hardening-for-github-actions#understanding-the-risk-of-script-injections)
- [GitHub deployment environments](https://docs.github.com/actions/reference/workflows-and-actions/deployments-and-environments)
- [GitHub deployment review](https://docs.github.com/actions/how-tos/deploy/configure-and-manage-deployments/review-deployments)
- [GitHub Git tag API](https://docs.github.com/rest/git/tags)
- [GitHub artifact attestations](https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations/using-artifact-attestations-to-establish-provenance-for-builds)
- [GitHub attestation verification](https://docs.github.com/actions/security-for-github-actions/using-artifact-attestations/verifying-the-authenticity-of-artifacts)
- [GitHub immutable releases](https://docs.github.com/code-security/supply-chain-security/end-to-end-supply-chain/securing-builds)
- [GitHub Releases REST API](https://docs.github.com/rest/releases/releases)
- [GitHub workflow concurrency](https://docs.github.com/actions/how-tos/write-workflows/choose-when-workflows-run/control-workflow-concurrency)
- [GitHub reusable workflow outputs](https://docs.github.com/actions/how-tos/sharing-automations/reusing-workflows#using-outputs-from-a-reusable-workflow)
- [GitHub workflow reruns](https://docs.github.com/actions/how-tos/manage-workflow-runs/re-run-workflows-and-jobs)
- [GitHub immutable workflow artifacts](https://github.com/actions/upload-artifact)
- [Git tag](https://git-scm.com/docs/git-tag)
