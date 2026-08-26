'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const {
  candidateReleaseOptions,
  reconcileRelease,
  recordReleaseEvidence,
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
    immutable: overrides.draft === false,
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
    getRelease: 0,
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
    getRelease: async () => {
      calls.getRelease++;

      return { data: currentRelease };
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
      currentRelease = { ...currentRelease, draft: request.draft, immutable: !request.draft };

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
    assert.equal(state.calls.getRelease, 2);
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

test('finalization reuses the complete staged asset set without adding readback evidence', async () =>
  withAssetFiles(async (assetPaths) => {
    const state = fixture(null);

    await stageRelease(options(state.github, assetPaths));
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

    const staged = await stageRelease(options(state.github, assetPaths));
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

test('rejects unexpected published assets during the early staging gate', async () =>
  withAssetFiles(async (assetPaths) => {
    const state = fixture(release({ draft: false }), [
      asset('first.nupkg', 'first bytes', 1),
      asset('second.snupkg', 'second bytes', 2),
      asset('SIGNED_SHA256SUMS', 'unexpected readback evidence', 3),
    ]);

    await assert.rejects(stageRelease(options(state.github, assetPaths)), /unexpected asset/);
    assert.equal(state.calls.upload, 0);
    assert.equal(state.calls.update, 0);
  }));

test('checks late asset conflicts before uploading any missing early asset', async () =>
  withAssetFiles(async (assetPaths) => {
    const state = fixture(release(), [asset('second.snupkg', 'conflict', 2)]);

    await assert.rejects(stageRelease(options(state.github, assetPaths)), /size|digest/);
    assert.equal(state.calls.upload, 0);
    assert.equal(state.calls.update, 0);
  }));

test('fails closed on duplicate assets and duplicate releases', async () =>
  withAssetFiles(async (assetPaths) => {
    const state = fixture(release(), [
      asset('first.nupkg', 'first bytes', 1),
      asset('first.nupkg', 'first bytes', 2),
    ]);

    await assert.rejects(stageRelease(options(state.github, assetPaths)), /duplicate asset/);
    state.github.paginate = async () => [release(), release({ id: 8 })];

    await assert.rejects(stageRelease(options(state.github, assetPaths)), /More than one/);
    assert.equal(state.calls.create, 0);
    assert.equal(state.calls.upload, 0);
  }));

test('rejects a published release that is not immutable', async () =>
  withAssetFiles(async (assetPaths) => {
    const state = fixture(release({ draft: false, immutable: false }), [
      asset('first.nupkg', 'first bytes', 1),
      asset('second.snupkg', 'second bytes', 2),
    ]);

    await assert.rejects(reconcileRelease(options(state.github, assetPaths)), /not immutable/);
    assert.equal(state.calls.update, 0);
  }));

test('a successful update response cannot hide conflicting publication readback', async () =>
  withAssetFiles(async (assetPaths) => {
    const state = fixture(null);
    const originalRead = state.github.rest.repos.getRelease;
    state.github.rest.repos.getRelease = async () => {
      const response = await originalRead();

      return state.calls.update === 0 ? response : {
        data: { ...response.data, name: 'unexpected release' },
      };
    };

    await assert.rejects(reconcileRelease(options(state.github, assetPaths)), /name/);
    assert.equal(state.calls.update, 1);
    assert.equal(state.calls.getRelease, 2);
  }));

test('reuses uploaded bytes after a lost upload response', async () =>
  withAssetFiles(async (assetPaths) => {
    const state = fixture(null);
    const upload = state.github.rest.repos.uploadReleaseAsset;
    state.github.rest.repos.uploadReleaseAsset = async (request) => {
      await upload(request);
      throw new Error('upload response lost');
    };

    await assert.rejects(stageRelease(options(state.github, assetPaths)), /response lost/);
    state.github.rest.repos.uploadReleaseAsset = upload;
    const result = await reconcileRelease(options(state.github, assetPaths));

    assert.equal(result.immutable, true);
    assert.equal(state.calls.create, 1);
    assert.equal(state.calls.upload, 2);
    assert.equal(state.calls.update, 1);
  }));

test('reuses a created draft after a lost creation response', async () =>
  withAssetFiles(async (assetPaths) => {
    const state = fixture(null);
    const create = state.github.rest.repos.createRelease;
    state.github.rest.repos.createRelease = async (request) => {
      await create(request);
      throw new Error('creation response lost');
    };

    await assert.rejects(stageRelease(options(state.github, assetPaths)), /response lost/);
    state.github.rest.repos.createRelease = create;
    await reconcileRelease(options(state.github, assetPaths));

    assert.equal(state.calls.create, 1);
    assert.equal(state.calls.upload, 2);
  }));

test('retains sanitized failure evidence without claiming a remote rollback', async () =>
  withAssetFiles(async (assetPaths) => {
    const file = path.join(path.dirname(assetPaths[0]), 'github-staged.json');
    const failure = Object.assign(new Error('upstream unavailable'), {
      status: 503,
      request: { headers: { authorization: 'secret credential' } },
    });

    await assert.rejects(recordReleaseEvidence(file, async () => { throw failure; }),
      (error) => error === failure);
    const evidence = fs.readFileSync(file, 'utf8');

    assert.deepEqual(JSON.parse(evidence), {
      status: 'failure', remoteState: 'unknown',
      error: { name: 'Error', message: 'upstream unavailable', httpStatus: 503 },
    });
    assert.equal(evidence.includes('secret credential'), false);
  }));

test('retries transient GitHub readback but fails immediately on authorization errors', async () =>
  withAssetFiles(async (assetPaths) => {
    for (const status of [503, 403]) {
      const state = fixture(null);
      const read = state.github.rest.repos.getRelease;
      let requests = 0;
      state.github.rest.repos.getRelease = async () => {
        requests++;
        if (requests === 1) {
          throw Object.assign(new Error('readback unavailable'), { status });
        }

        return read();
      };

      if (status === 503) {
        await reconcileRelease(options(state.github, assetPaths));
        assert.equal(requests, 3);
        assert.equal(state.calls.update, 1);
      } else {
        await assert.rejects(reconcileRelease(options(state.github, assetPaths)), /unavailable/);
        assert.equal(requests, 1);
        assert.equal(state.calls.update, 0);
      }
    }
  }));

test('bounds unsuccessful readback without declaring or retrying publication', async () =>
  withAssetFiles(async (assetPaths) => {
    const state = fixture(null);
    let requests = 0;
    state.github.rest.repos.getRelease = async () => {
      requests++;
      throw Object.assign(new Error('readback still unavailable'), { status: 503 });
    };

    await assert.rejects(reconcileRelease(options(state.github, assetPaths)), /still unavailable/);
    assert.equal(requests, 5);
    assert.equal(state.calls.create, 1);
    assert.equal(state.calls.upload, 2);
    assert.equal(state.calls.update, 0);
  }));

test('waits for uploaded asset visibility without uploading the same bytes twice', async () =>
  withAssetFiles(async (assetPaths) => {
    const state = fixture(null);
    const paginate = state.github.paginate;
    let hiddenOnce = false;
    state.github.paginate = async (endpoint, request) => {
      const result = await paginate(endpoint, request);
      if (endpoint === state.github.rest.repos.listReleaseAssets
          && state.calls.upload === 2 && !hiddenOnce) {
        hiddenOnce = true;

        return result.slice(1);
      }

      return result;
    };

    await reconcileRelease(options(state.github, assetPaths));

    assert.equal(hiddenOnce, true);
    assert.equal(state.calls.upload, 2);
    assert.equal(state.calls.update, 1);
  }));

test('waits for draft-to-published readback after an accepted publication request', async () =>
  withAssetFiles(async (assetPaths) => {
    const state = fixture(null);
    const read = state.github.rest.repos.getRelease;
    let staleOnce = false;
    state.github.rest.repos.getRelease = async () => {
      const response = await read();
      if (state.calls.update === 1 && !staleOnce) {
        staleOnce = true;

        return { data: { ...response.data, draft: true, immutable: false } };
      }

      return response;
    };

    const result = await reconcileRelease(options(state.github, assetPaths));

    assert.equal(staleOnce, true);
    assert.equal(state.calls.update, 1);
    assert.equal(result.immutable, true);
  }));

function workflowScript(stepName) {
  const workflow = fs.readFileSync(
    path.join(__dirname, '../../.github/workflows/release-candidate.yml'), 'utf8');
  const step = workflow.split(`      - name: ${stepName}\n`)[1]?.split('\n      - name: ')[0];
  const script = step?.split('          script: |\n')[1];
  assert.ok(script, `Workflow step '${stepName}' must contain an executable script.`);

  return script.split('\n').map((line) => line.replace(/^            /, '')).join('\n');
}

test('actual workflow stages and retries the same eleven assets across finalization failures', async () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'safe-migrations-workflow-'));
  const originalDirectory = process.cwd();
  const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
  const version = '10.0.0-rc.7';
  const context = { repo: { owner: 'doka-labs', repo: 'safe-migrations' }, sha: 'abc123' };
  const candidate = candidateReleaseOptions({
    ...context.repo, version, commit: context.sha, artifactRoot: path.join(directory, 'artifacts'),
  });
  for (const assetPath of candidate.assetPaths) {
    fs.mkdirSync(path.dirname(assetPath), { recursive: true });
    fs.writeFileSync(assetPath, `qualified ${path.basename(assetPath)}`);
  }

  const evidence = path.join(directory, 'artifacts/release-publication');
  fs.mkdirSync(evidence, { recursive: true });
  const load = (name) => name === './eng/release/github-release.js'
    ? require('../release/github-release.js') : require(name);
  const stage = new AsyncFunction('github', 'context', 'process', 'require',
    workflowScript('Stage and verify GitHub Release draft'));
  const publish = new AsyncFunction('github', 'context', 'process', 'require',
    workflowScript('Publish and read back immutable GitHub Release'));
  const environment = { env: { PACKAGE_VERSION: version } };

  try {
    process.chdir(directory);
    for (const responseLost of [false, true]) {
      const state = fixture(null);
      const update = state.github.rest.repos.updateRelease;
      await stage(state.github, context, environment, load);
      state.github.rest.repos.updateRelease = async (request) => {
        if (responseLost) {
          await update(request);
        }

        throw new Error('publication interrupted');
      };

      await assert.rejects(publish(state.github, context, environment, load), /interrupted/);
      const failure = JSON.parse(fs.readFileSync(path.join(evidence, 'github-published.json'), 'utf8'));
      assert.equal(failure.status, 'failure');
      assert.equal(failure.remoteState, 'unknown');
      assert.equal(failure.error.message, 'publication interrupted');
      assert.equal(state.getAssets().length, 11);
      assert.equal(state.getRelease().draft, !responseLost);

      // Retry observations change independently of the immutable candidate.
      fs.writeFileSync(path.join(evidence, 'SIGNED_SHA256SUMS'), 'new attempt observations');
      state.github.rest.repos.updateRelease = update;
      await stage(state.github, context, environment, load);
      await publish(state.github, context, environment, load);

      assert.equal(state.calls.create, 1);
      assert.equal(state.calls.upload, 11);
      assert.equal(state.calls.update, 1);
      assert.equal(state.getRelease().immutable, true);
      assert.equal(state.getAssets().some((entry) => entry.name === 'SIGNED_SHA256SUMS'), false);
      const readback = JSON.parse(fs.readFileSync(path.join(evidence, 'github-published.json'), 'utf8'));
      assert.equal(readback.status, 'success');
      assert.equal(readback.result.tag_name, `v${version}`);
      assert.equal(readback.result.immutable, true);
    }
  } finally {
    process.chdir(originalDirectory);
    fs.rmSync(directory, { recursive: true, force: true });
  }
});
