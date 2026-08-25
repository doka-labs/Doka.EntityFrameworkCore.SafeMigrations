'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const {
  reconcileRelease,
  sha256,
  stageRelease,
  verifySignedAnnotatedTag,
} = require('../release/github-release.js');

function asset(name, data, id) {
  const bytes = Buffer.from(data);

  return {
    id,
    name,
    size: bytes.length,
    digest: `sha256:${sha256(bytes)}`,
    state: 'uploaded',
    data: bytes,
  };
}

function release(overrides = {}) {
  return {
    id: 7,
    tag_name: 'v10.0.0',
    target_commitish: 'abc123',
    name: 'v10.0.0',
    body: 'release body',
    draft: true,
    prerelease: false,
    ...overrides,
  };
}

function fixture(initialRelease, initialAssets = []) {
  let currentRelease = initialRelease;
  let assets = [...initialAssets];
  const calls = {
    create: 0,
    getCommit: 0,
    update: 0,
    upload: 0,
    paginate: 0,
    makeLatest: null,
  };
  const repos = {
    listReleases: Symbol('listReleases'),
    listReleaseAssets: Symbol('listReleaseAssets'),
    getCommit: async () => {
      calls.getCommit++;

      return { data: { sha: 'abc123' } };
    },
    createRelease: async (request) => {
      calls.create++;
      currentRelease = release({
        tag_name: request.tag_name,
        target_commitish: request.target_commitish,
        name: request.name,
        body: request.body,
        prerelease: request.prerelease,
      });

      return { data: currentRelease };
    },
    uploadReleaseAsset: async (request) => {
      calls.upload++;
      assets.push(asset(request.name, request.data, 100 + calls.upload));

      return { data: assets.at(-1) };
    },
    updateRelease: async (request) => {
      calls.update++;
      calls.makeLatest = request.make_latest;
      currentRelease = { ...currentRelease, draft: request.draft };

      return { data: currentRelease };
    },
  };
  const github = {
    rest: { repos },
    paginate: async (endpoint) => {
      calls.paginate++;

      return endpoint === repos.listReleases
        ? (currentRelease ? [currentRelease] : [])
        : assets.map(({ data, ...metadata }) => metadata);
    },
    request: async (_route, request) => ({
      data: assets.find((candidate) => candidate.id === request.asset_id).data,
    }),
  };

  return { github, calls, getRelease: () => currentRelease, getAssets: () => assets };
}

async function withAssetFiles(run) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'safe-migrations-release-'));
  const first = path.join(directory, 'first.nupkg');
  const second = path.join(directory, 'second.snupkg');
  fs.writeFileSync(first, 'first bytes');
  fs.writeFileSync(second, 'second bytes');

  try {
    return await run([first, second]);
  } finally {
    fs.rmSync(directory, { recursive: true, force: true });
  }
}

function options(github, assetPaths, prerelease = false) {
  return {
    github,
    owner: 'doka-labs',
    repo: 'safe-migrations',
    tag: prerelease ? 'v10.0.0-rc.1' : 'v10.0.0',
    targetCommitish: 'abc123',
    name: prerelease ? 'v10.0.0-rc.1' : 'v10.0.0',
    body: 'release body',
    prerelease,
    assetPaths,
  };
}

function tagFixture(overrides = {}) {
  const reference = {
    type: 'tag',
    sha: 'tag-object-sha',
    ...overrides.reference,
  };
  const annotatedTag = {
    tag: 'v12.3.4-rc.5',
    object: {
      type: 'commit',
      sha: 'qualified-commit',
    },
    verification: {
      verified: true,
      reason: 'valid',
    },
    ...overrides.annotatedTag,
  };
  const calls = {
    getRef: [],
    getTag: [],
  };
  const github = {
    rest: {
      git: {
        getRef: async (request) => {
          calls.getRef.push(request);

          return { data: { object: reference } };
        },
        getTag: async (request) => {
          calls.getTag.push(request);

          return { data: annotatedTag };
        },
      },
    },
  };

  return { github, calls };
}

test('accepts a GitHub-verified annotated tag bound to the qualified commit', async () => {
  const state = tagFixture();

  const result = await verifySignedAnnotatedTag(
    state.github,
    'doka-labs',
    'safe-migrations',
    'v12.3.4-rc.5',
    'qualified-commit');

  assert.equal(result.verification.verified, true);
  assert.deepEqual(state.calls.getRef, [{
    owner: 'doka-labs',
    repo: 'safe-migrations',
    ref: 'tags/v12.3.4-rc.5',
  }]);
  assert.deepEqual(state.calls.getTag, [{
    owner: 'doka-labs',
    repo: 'safe-migrations',
    tag_sha: 'tag-object-sha',
  }]);
});

test('rejects lightweight, indirect, mismatched, and unverified release tags', async () => {
  const invalidCases = [
    {
      overrides: { reference: { type: 'commit' } },
      expected: /must be annotated/,
    },
    {
      overrides: { annotatedTag: { tag: 'v12.3.4-rc.6' } },
      expected: /tag object names/,
    },
    {
      overrides: {
        annotatedTag: {
          object: { type: 'tag', sha: 'qualified-commit' },
        },
      },
      expected: /does not directly identify/,
    },
    {
      overrides: {
        annotatedTag: {
          object: { type: 'commit', sha: 'different-commit' },
        },
      },
      expected: /does not directly identify/,
    },
    {
      overrides: {
        annotatedTag: {
          verification: { verified: false, reason: 'unsigned' },
        },
      },
      expected: /reason: unsigned/,
    },
    {
      overrides: { annotatedTag: { verification: undefined } },
      expected: /reason: missing/,
    },
  ];

  for (const invalidCase of invalidCases) {
    const state = tagFixture(invalidCase.overrides);

    await assert.rejects(
      verifySignedAnnotatedTag(
        state.github,
        'doka-labs',
        'safe-migrations',
        'v12.3.4-rc.5',
        'qualified-commit'),
      invalidCase.expected);
  }
});

test('creates a draft, uploads every asset, verifies, and publishes last', async () =>
  withAssetFiles(async (assetPaths) => {
    const state = fixture(null);

    const result = await reconcileRelease(options(state.github, assetPaths));

    assert.equal(state.calls.create, 1);
    assert.equal(state.calls.upload, 2);
    assert.equal(state.calls.update, 1);
    assert.equal(state.calls.makeLatest, 'true');
    assert.equal(state.calls.paginate, 3);
    assert.equal(result.draft, false);
    assert.equal(result.prerelease, false);
  }));

test('stages and verifies a complete draft without publishing it', async () =>
  withAssetFiles(async (assetPaths) => {
    const state = fixture(null);

    const result = await stageRelease(options(state.github, assetPaths, true));

    assert.equal(state.calls.create, 1);
    assert.equal(state.calls.upload, 2);
    assert.equal(state.calls.update, 0);
    assert.equal(result.draft, true);
    assert.equal(result.prerelease, true);
  }));

test('finalization adds post-publication evidence to the staged draft and publishes last', async () =>
  withAssetFiles(async (assetPaths) => {
    const state = fixture(null);

    await stageRelease(options(state.github, [assetPaths[0]]));
    const result = await reconcileRelease(options(state.github, assetPaths));

    assert.equal(state.calls.create, 1);
    assert.equal(state.calls.upload, 2);
    assert.equal(state.calls.update, 1);
    assert.equal(state.calls.makeLatest, 'true');
    assert.equal(result.draft, false);
  }));

test('publishes a release candidate as prerelease and never as latest', async () =>
  withAssetFiles(async (assetPaths) => {
    const state = fixture(null);

    const result = await reconcileRelease(options(state.github, assetPaths, true));

    assert.equal(state.calls.create, 1);
    assert.equal(state.calls.update, 1);
    assert.equal(state.calls.makeLatest, 'false');
    assert.equal(result.draft, false);
    assert.equal(result.prerelease, true);
  }));

test('resumes an exact partial draft without replacing existing bytes', async () =>
  withAssetFiles(async (assetPaths) => {
    const first = asset('first.nupkg', 'first bytes', 11);
    const state = fixture(release(), [first]);

    await reconcileRelease(options(state.github, assetPaths));

    assert.equal(state.calls.create, 0);
    assert.equal(state.calls.upload, 1);
    assert.equal(state.calls.update, 1);
    assert.equal(state.getAssets().length, 2);
  }));

test('fails closed on an asset digest conflict and leaves the draft unpublished', async () =>
  withAssetFiles(async (assetPaths) => {
    const conflict = asset('first.nupkg', 'different bytes', 12);
    const state = fixture(release(), [conflict]);

    await assert.rejects(
      reconcileRelease(options(state.github, assetPaths)),
      /size|digest/);
    assert.equal(state.calls.upload, 0);
    assert.equal(state.calls.update, 0);
    assert.equal(state.getRelease().draft, true);
  }));

test('treats an exact published release as an idempotent success', async () =>
  withAssetFiles(async (assetPaths) => {
    const assets = [
      asset('first.nupkg', 'first bytes', 21),
      asset('second.snupkg', 'second bytes', 22),
    ];
    const state = fixture(release({ draft: false }), assets);

    const result = await reconcileRelease(options(state.github, assetPaths));

    assert.equal(state.calls.create, 0);
    assert.equal(state.calls.upload, 0);
    assert.equal(state.calls.update, 0);
    assert.equal(result.draft, false);
  }));

test('resumes when publication completed before the action received its response', async () =>
  withAssetFiles(async (assetPaths) => {
    const assets = [
      asset('first.nupkg', 'first bytes', 23),
      asset('second.snupkg', 'second bytes', 24),
    ];
    const state = fixture(release({ draft: false }), assets);

    const staged = await stageRelease(options(state.github, [assetPaths[0]]));
    const finalized = await reconcileRelease(options(state.github, assetPaths));

    assert.equal(state.calls.create, 0);
    assert.equal(state.calls.upload, 0);
    assert.equal(state.calls.update, 0);
    assert.equal(staged.draft, false);
    assert.equal(finalized.draft, false);
  }));

test('fails closed on unexpected assets and conflicting release metadata', async () =>
  withAssetFiles(async (assetPaths) => {
    const unexpected = fixture(release(), [asset('unexpected.txt', 'value', 31)]);
    await assert.rejects(
      reconcileRelease(options(unexpected.github, assetPaths)),
      /unexpected asset/);

    const metadata = fixture(release({ name: 'other' }));
    await assert.rejects(
      reconcileRelease(options(metadata.github, assetPaths)),
      /name/);

    const published = fixture(release({ draft: false }), [
      asset('first.nupkg', 'first bytes', 32),
      asset('second.snupkg', 'second bytes', 33),
      asset('unexpected.txt', 'value', 34),
    ]);
    await assert.rejects(
      reconcileRelease(options(published.github, assetPaths)),
      /unexpected asset/);
  }));

test('fails closed when a release candidate collides with stable metadata', async () =>
  withAssetFiles(async (assetPaths) => {
    const state = fixture(release({
      tag_name: 'v10.0.0-rc.1',
      name: 'v10.0.0-rc.1',
      prerelease: false,
    }));

    await assert.rejects(
      reconcileRelease(options(state.github, assetPaths, true)),
      /prerelease/);
    assert.equal(state.calls.upload, 0);
    assert.equal(state.calls.update, 0);
  }));

test('requires the caller to choose stable or prerelease mode explicitly', async () =>
  withAssetFiles(async (assetPaths) => {
    const state = fixture(null);
    const incomplete = options(state.github, assetPaths);
    delete incomplete.prerelease;

    await assert.rejects(
      reconcileRelease(incomplete),
      /prerelease mode must be explicit/);
    assert.equal(state.calls.getCommit, 0);
    assert.equal(state.calls.create, 0);
  }));

test('fails closed when the tag does not resolve to the qualified commit', async () =>
  withAssetFiles(async (assetPaths) => {
    const state = fixture(release());
    state.github.rest.repos.getCommit = async () => ({ data: { sha: 'other' } });

    await assert.rejects(
      reconcileRelease(options(state.github, assetPaths)),
      /resolves to other/);
    assert.equal(state.calls.create, 0);
    assert.equal(state.calls.upload, 0);
    assert.equal(state.calls.update, 0);
  }));

test('fails closed while an uploaded asset is not in the uploaded state', async () =>
  withAssetFiles(async (assetPaths) => {
    const first = asset('first.nupkg', 'first bytes', 51);
    first.state = 'starter';
    const state = fixture(release(), [first]);

    await assert.rejects(
      reconcileRelease(options(state.github, assetPaths)),
      /state 'starter'/);
    assert.equal(state.calls.upload, 0);
    assert.equal(state.calls.update, 0);
  }));

test('downloads an asset when GitHub does not expose a digest', async () =>
  withAssetFiles(async (assetPaths) => {
    const first = asset('first.nupkg', 'first bytes', 41);
    delete first.digest;
    const second = asset('second.snupkg', 'second bytes', 42);
    const state = fixture(release(), [first, second]);

    await reconcileRelease(options(state.github, assetPaths));

    assert.equal(state.calls.upload, 0);
    assert.equal(state.calls.update, 1);
  }));
