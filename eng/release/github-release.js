'use strict';

const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

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
    throw new Error(`Release asset '${expected.name}' is in state '${actual.state}'.`);
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

async function reconcileAssets(
  github,
  owner,
  repo,
  release,
  expectedAssets,
  allowAdditionalPublishedAssets = false,
) {
  const existingAssets = await github.paginate(github.rest.repos.listReleaseAssets, {
    owner,
    repo,
    release_id: release.id,
    per_page: 100,
  });
  const expectedByName = new Map(expectedAssets.map((asset) => [asset.name, asset]));
  const actualByName = new Map();
  for (const asset of existingAssets) {
    if (actualByName.has(asset.name)) {
      throw new Error(`GitHub Release contains duplicate asset '${asset.name}'.`);
    }

    actualByName.set(asset.name, asset);
    if (!expectedByName.has(asset.name)
        && !(allowAdditionalPublishedAssets && !release.draft)) {
      throw new Error(`GitHub Release contains unexpected asset '${asset.name}'.`);
    }
  }

  for (const expected of expectedAssets) {
    const actual = actualByName.get(expected.name);
    if (actual) {
      await verifyAsset(github, owner, repo, actual, expected);
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

  const reconciled = await github.paginate(github.rest.repos.listReleaseAssets, {
    owner,
    repo,
    release_id: release.id,
    per_page: 100,
  });
  if (!allowAdditionalPublishedAssets
      && reconciled.length !== expectedAssets.length) {
    throw new Error(
      `GitHub Release has ${reconciled.length} assets; expected ${expectedAssets.length}.`);
  }

  for (const actual of reconciled) {
    const expected = expectedByName.get(actual.name);
    if (!expected) {
      if (allowAdditionalPublishedAssets && !release.draft) {
        continue;
      }

      throw new Error(`GitHub Release contains unexpected asset '${actual.name}'.`);
    }

    await verifyAsset(github, owner, repo, actual, expected);
  }
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
  await reconcileAssets(
    github,
    owner,
    repo,
    release,
    expectedAssets,
    !release.draft,
  );

  return release;
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
    const response = await github.rest.repos.updateRelease({
      owner,
      repo,
      release_id: release.id,
      draft: false,
      make_latest: prerelease ? 'false' : 'true',
    });

    return response.data;
  }

  await reconcileAssets(
    github,
    owner,
    repo,
    release,
    loadAssets(assetPaths),
  );

  return release;
}

module.exports = {
  loadAssets,
  reconcileRelease,
  sha256,
  stageRelease,
  verifySignedAnnotatedTag,
};
