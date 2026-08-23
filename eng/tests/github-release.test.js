'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const { reconcileRelease, sha256 } = require('../release/github-release.js');

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
    tag_name: 'v1.2.3',
    target_commitish: 'abc123',
    name: 'v1.2.3',
    body: 'release body',
    draft: true,
    prerelease: false,
    ...overrides,
  };
}

function fixture(initialRelease, initialAssets = []) {
  let currentRelease = initialRelease;
  let assets = [...initialAssets];
  const calls = { create: 0, getCommit: 0, update: 0, upload: 0, paginate: 0 };
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
      assert.equal(request.make_latest, 'true');
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

function options(github, assetPaths) {
  return {
    github,
    owner: 'doka-labs',
    repo: 'safe-migrations',
    tag: 'v1.2.3',
    targetCommitish: 'abc123',
    name: 'v1.2.3',
    body: 'release body',
    assetPaths,
  };
}

test('creates a draft, uploads every asset, verifies, and publishes last', async () =>
  withAssetFiles(async (assetPaths) => {
    const state = fixture(null);

    const result = await reconcileRelease(options(state.github, assetPaths));

    assert.equal(state.calls.create, 1);
    assert.equal(state.calls.upload, 2);
    assert.equal(state.calls.update, 1);
    assert.equal(state.calls.paginate, 3);
    assert.equal(result.draft, false);
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
