'use strict';

const crypto = require('crypto');
const fs = require('fs');
const path = require('path');
const { setTimeout: delay } = require('timers/promises');

// Candidate identity is complete before publication. Attempt-specific readback
// files deliberately never become immutable release assets.
function candidateReleaseOptions({ github, owner, repo, version, commit, artifactRoot = 'artifacts' }) {
  const packageIds = [
    'Doka.EntityFrameworkCore.SafeMigrations',
    'Doka.EntityFrameworkCore.SafeMigrations.MySql',
    'Doka.EntityFrameworkCore.SafeMigrations.PostgreSql',
  ];
  const tag = `v${version}`;
  const assetPaths = packageIds.flatMap((id) => ['nupkg', 'snupkg'].map(
    (extension) => path.join(artifactRoot, 'packages', `${id}.${version}.${extension}`)));
  assetPaths.push(
    path.join(artifactRoot, 'packages', 'SHA256SUMS'),
    path.join(artifactRoot, 'packages', 'SYMBOLS.json'),
    path.join(artifactRoot, 'sbom', '_manifest', 'spdx_2.2', 'manifest.spdx.json'),
    path.join(artifactRoot, 'attestations', 'build-provenance.sigstore.json'),
    path.join(artifactRoot, 'attestations', 'sbom-attestation.sigstore.json'),
  );

  return {
    github,
    owner,
    repo,
    tag,
    targetCommitish: commit,
    name: tag,
    body: `See [CHANGELOG.md](https://github.com/${owner}/${repo}/blob/${tag}/CHANGELOG.md).`,
    prerelease: version.includes('-'),
    assetPaths,
  };
}

class PendingReleaseReadback extends Error {}

async function recordReleaseEvidence(filePath, operation) {
  let evidence;
  try {
    const release = await operation();
    evidence = { status: 'success', result: release };

    return release;
  } catch (error) {
    // A failed response cannot establish whether the remote mutation happened.
    // Keep only diagnostic fields, never Octokit's request or credential headers.
    evidence = {
      status: 'failure',
      remoteState: 'unknown',
      error: { name: error.name, message: error.message, httpStatus: error.status ?? null },
    };

    throw error;
  } finally {
    fs.writeFileSync(filePath, JSON.stringify(evidence, null, 2) + '\n');
  }
}

async function awaitReadback(read) {
  for (let attempt = 0; ; attempt++) {
    try {
      return await read();
    } catch (error) {
      const transient = error instanceof PendingReleaseReadback
        || [404, 408, 429].includes(error.status)
        || (error.status >= 500 && error.status <= 599);
      if (!transient || attempt === 4) {
        throw error;
      }

      await delay(2000);
    }
  }
}

function sha256(data) {
  return crypto.createHash('sha256').update(data).digest('hex');
}

function loadAssets(assetPaths) {
  const assets = assetPaths.map((assetPath) => {
    const data = fs.readFileSync(assetPath);

    return {
      name: path.basename(assetPath),
      data,
      size: data.length,
      digest: `sha256:${sha256(data)}`,
    };
  });
  const names = new Set();
  for (const asset of assets) {
    if (names.has(asset.name)) {
      throw new Error(`Release asset name '${asset.name}' is not unique.`);
    }

    names.add(asset.name);
  }

  return assets;
}

async function listReleaseByTag(github, owner, repo, tag) {
  const releases = await github.paginate(github.rest.repos.listReleases, {
    owner,
    repo,
    per_page: 100,
  });
  const matches = releases.filter((release) => release.tag_name === tag);
  if (matches.length > 1) {
    throw new Error(`More than one GitHub Release exists for tag '${tag}'.`);
  }

  return matches[0] ?? null;
}

function validateRelease(release, expected) {
  const conflicts = [];
  if (release.tag_name !== expected.tag) {
    conflicts.push('tag_name');
  }

  if (release.name !== expected.name) {
    conflicts.push('name');
  }

  if (release.body !== expected.body) {
    conflicts.push('body');
  }

  if (release.prerelease !== expected.prerelease) {
    conflicts.push('prerelease');
  }

  if (conflicts.length > 0) {
    throw new Error(
      `GitHub Release '${expected.tag}' conflicts in: ${conflicts.join(', ')}.`);
  }
}

async function validateTagTarget(github, owner, repo, tag, expectedCommit) {
  const response = await github.rest.repos.getCommit({
    owner,
    repo,
    ref: `tags/${tag}`,
  });
  if (response.data.sha !== expectedCommit) {
    throw new Error(
      `Git tag '${tag}' resolves to ${response.data.sha}; expected ${expectedCommit}.`);
  }
}

async function verifySignedAnnotatedTag(github, owner, repo, tag, expectedCommit) {
  const referenceResponse = await github.rest.git.getRef({
    owner,
    repo,
    ref: `tags/${tag}`,
  });
  const reference = referenceResponse.data.object;
  if (reference.type !== 'tag') {
    throw new Error(`Git tag '${tag}' must be annotated.`);
  }

  const tagResponse = await github.rest.git.getTag({
    owner,
    repo,
    tag_sha: reference.sha,
  });
  const annotatedTag = tagResponse.data;
  if (annotatedTag.tag !== tag) {
    throw new Error(
      `Annotated tag object names '${annotatedTag.tag}'; expected '${tag}'.`);
  }

  if (annotatedTag.object.type !== 'commit'
      || annotatedTag.object.sha !== expectedCommit) {
    throw new Error(
      `Git tag '${tag}' does not directly identify qualified commit ${expectedCommit}.`);
  }

  if (annotatedTag.verification?.verified !== true) {
    const reason = annotatedTag.verification?.reason ?? 'missing';

    throw new Error(
      `GitHub did not verify the signature of tag '${tag}' (reason: ${reason}).`);
  }

  return annotatedTag;
}

async function readAssetDigest(github, owner, repo, asset) {
  if (typeof asset.digest === 'string' && asset.digest.startsWith('sha256:')) {
    return asset.digest.toLowerCase();
  }

  const response = await github.request(
    'GET /repos/{owner}/{repo}/releases/assets/{asset_id}',
    {
      owner,
      repo,
      asset_id: asset.id,
      headers: { accept: 'application/octet-stream' },
    });
  const data = Buffer.isBuffer(response.data)
    ? response.data
    : Buffer.from(response.data);

  return `sha256:${sha256(data)}`;
}

async function verifyAsset(github, owner, repo, actual, expected) {
  if (actual.state !== 'uploaded') {
    throw new PendingReleaseReadback(`Release asset '${expected.name}' is in state '${actual.state}'.`);
  }

  if (actual.size !== expected.size) {
    throw new Error(
      `Release asset '${expected.name}' has size ${actual.size}; expected ${expected.size}.`);
  }

  const digest = await readAssetDigest(github, owner, repo, actual);
  if (digest !== expected.digest) {
    throw new Error(
      `Release asset '${expected.name}' has digest ${digest}; expected ${expected.digest}.`);
  }
}

function indexAssets(assets, expectedByName) {
  const actualByName = new Map();
  for (const asset of assets) {
    if (actualByName.has(asset.name)) {
      throw new Error(`GitHub Release contains duplicate asset '${asset.name}'.`);
    }

    actualByName.set(asset.name, asset);
    if (!expectedByName.has(asset.name)) {
      throw new Error(`GitHub Release contains unexpected asset '${asset.name}'.`);
    }
  }

  return actualByName;
}

async function listAssets(github, owner, repo, releaseId) {
  return github.paginate(github.rest.repos.listReleaseAssets, {
    owner,
    repo,
    release_id: releaseId,
    per_page: 100,
  });
}

async function verifyCompleteAssets(github, owner, repo, release, expectedAssets) {
  const expectedByName = new Map(expectedAssets.map((asset) => [asset.name, asset]));
  const observed = indexAssets(await listAssets(github, owner, repo, release.id), expectedByName);
  for (const expected of expectedAssets) {
    const actual = observed.get(expected.name);
    if (!actual) {
      throw new PendingReleaseReadback(`GitHub Release is missing asset '${expected.name}'.`);
    }

    await verifyAsset(github, owner, repo, actual, expected);
  }
}

async function reconcileAssets(github, owner, repo, release, expectedAssets) {
  const expectedByName = new Map(expectedAssets.map((asset) => [asset.name, asset]));
  const actualByName = indexAssets(await listAssets(github, owner, repo, release.id), expectedByName);

  // Validate all existing content before the first upload, including a conflict
  // late in the list. A retry never repairs conflicting bytes by replacement.
  for (const expected of expectedAssets) {
    const actual = actualByName.get(expected.name);
    if (actual) {
      await verifyAsset(github, owner, repo, actual, expected);
    }
  }

  for (const expected of expectedAssets) {
    if (actualByName.has(expected.name)) {
      continue;
    }

    if (!release.draft) {
      throw new Error(`Published GitHub Release is missing asset '${expected.name}'.`);
    }

    await github.rest.repos.uploadReleaseAsset({
      owner,
      repo,
      release_id: release.id,
      name: expected.name,
      data: expected.data,
    });
  }

  await awaitReadback(() => verifyCompleteAssets(github, owner, repo, release, expectedAssets));
}

async function readRelease(options, releaseId, expectedAssets, published) {
  const { github, owner, repo } = options;

  return awaitReadback(async () => {
    const { data: release } = await github.rest.repos.getRelease({
      owner,
      repo,
      release_id: releaseId,
    });
    validateRelease(release, options);
    if (typeof release.draft !== 'boolean') {
      throw new Error('GitHub Release readback has no explicit draft state.');
    }

    if (published && release.draft) {
      throw new PendingReleaseReadback('GitHub Release is still a draft after publication.');
    }

    if (!release.draft && release.immutable !== true) {
      throw new Error('Published GitHub Release is not immutable.');
    }

    await validateTagTarget(github, owner, repo, options.tag, options.targetCommitish);
    await verifyCompleteAssets(github, owner, repo, release, expectedAssets);

    return release;
  });
}

async function stageRelease(options) {
  const {
    github,
    owner,
    repo,
    tag,
    targetCommitish,
    name,
    body,
    prerelease,
    assetPaths,
  } = options;
  if (typeof prerelease !== 'boolean') {
    throw new Error('GitHub Release prerelease mode must be explicit.');
  }

  const expected = { tag, name, body, prerelease };
  const expectedAssets = loadAssets(assetPaths);
  await validateTagTarget(github, owner, repo, tag, targetCommitish);
  let release = await listReleaseByTag(github, owner, repo, tag);
  if (!release) {
    const response = await github.rest.repos.createRelease({
      owner,
      repo,
      tag_name: tag,
      target_commitish: targetCommitish,
      name,
      body,
      draft: true,
      prerelease,
    });
    release = response.data;
  }

  validateRelease(release, expected);
  await reconcileAssets(github, owner, repo, release, expectedAssets);

  return readRelease(options, release.id, expectedAssets, !release.draft);
}

async function reconcileRelease(options) {
  const release = await stageRelease(options);
  const {
    github,
    owner,
    repo,
    prerelease,
    assetPaths,
  } = options;
  if (release.draft) {
    await github.rest.repos.updateRelease({
      owner,
      repo,
      release_id: release.id,
      draft: false,
      make_latest: prerelease ? 'false' : 'true',
    });
  }

  return readRelease(options, release.id, loadAssets(assetPaths), true);
}

module.exports = {
  candidateReleaseOptions,
  loadAssets,
  reconcileRelease,
  recordReleaseEvidence,
  sha256,
  stageRelease,
  verifySignedAnnotatedTag,
};
