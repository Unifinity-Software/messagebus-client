const assert = require('node:assert/strict');
const { createHash } = require('node:crypto');
const { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const { test } = require('node:test');

const workflow = readFileSync(join(__dirname, '../.github/workflows/release-mbus.yml'), 'utf8');
const scripts = [...workflow.matchAll(/^          script: \|\n((?: {12}[^\n]*\n|\n)+)/gm)]
  .map(match => match[1].replace(/^ {12}/gm, ''));
assert.equal(scripts.length, 2);
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
const run = scripts.map(script => new AsyncFunction('github', 'context', 'core', 'process', 'require', script));
const sha = 'a'.repeat(40);
const version = '1.2.3-preview.1';
const tag = `mbus-v${version}`;
const env = { SHA: sha, VERSION: version, GITHUB_RUN_ATTEMPT: '1' };
const context = { sha, repo: { owner: 'example', repo: 'fixture' } };

test('prepare requires the latest Build and the latest disclosure status on the exact commit', async () => {
  const state = { build: 'success', statuses: [] };
  const refs = [];
  const github = {
    rest: {
      git: { getRef: async () => { throw Object.assign(new Error(), { status: 404 }); } },
      pulls: { get: async () => ({ data: { merged: true, base: { ref: 'main' }, merge_commit_sha: sha } }) },
      actions: { listWorkflowRuns: async ({ head_sha }) => {
        refs.push(head_sha);
        return { data: { workflow_runs: [{ head_sha, status: 'completed', conclusion: state.build }] } };
      } },
      repos: {
        compareCommitsWithBasehead: async () => ({ data: { merge_base_commit: { sha } } }),
        listPullRequestsAssociatedWithCommit: async () => [{ number: 1, base: { ref: 'main' }, state: 'closed' }],
        listCommitStatusesForRef: async function* ({ ref }) {
          refs.push(ref);
          yield { data: [{ context: 'other', state: 'success' }] };
          yield { data: state.statuses };
        }
      }
    },
    paginate: Object.assign((method, params) => method(params), { iterator: (method, params) => method(params) })
  };
  const outputs = {};
  const execute = () => run[0](github, context, { setOutput: (key, value) => { outputs[key] = value; } }, { env });
  await assert.rejects(execute, /org-strings/);
  state.statuses = ['failure', 'success'].map(state => ({ context: 'org-strings', state }));
  await assert.rejects(execute, /org-strings/);
  state.statuses.shift();
  state.build = 'failure';
  await assert.rejects(execute, /Build/);
  state.build = 'success';
  await execute();
  assert.deepEqual(outputs, { attempt: '1', sha, version });
  assert.ok(refs.every(ref => ref === sha));
});

test('release resumes a partial draft and verifies a completed retry without writes', async (t) => {
  const root = mkdtempSync(join(process.env.RUNNER_TEMP || tmpdir(), 'release-assets-'));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const directory = join(root, 'artifacts/packages');
  mkdirSync(directory, { recursive: true });
  for (let i = 0; i < 15; i++) {
    writeFileSync(join(directory, `package-${i}.${i < 8 ? 'nupkg' : 'snupkg'}`), `contents ${i}`);
  }

  const wrapRequire = name => {
    if (name !== 'node:fs') return require(name);
    const fs = require(name);
    const resolve = method => (path, ...args) => method(join(root, path), ...args);
    return {
      createReadStream: resolve(fs.createReadStream), readFileSync: resolve(fs.readFileSync),
      readdirSync: resolve(fs.readdirSync), statSync: resolve(fs.statSync)
    };
  };

  const assets = [];
  const releases = [];
  const writes = { tags: 0, drafts: 0, uploads: 0, publications: 0 };
  let tagSha;
  let failUpload = true;
  const github = {
    rest: {
      git: {
        getRef: async () => {
          if (!tagSha) throw Object.assign(new Error(), { status: 404 });
          return { data: { object: { type: 'commit', sha: tagSha } } };
        },
        createRef: async ({ sha }) => { writes.tags++; tagSha = sha; }
      },
      repos: {
        listReleases: async () => releases,
        createRelease: async ({ tag_name, draft, prerelease, target_commitish }) => {
          writes.drafts++;
          assert.equal(tag_name, tag);
          assert.ok(draft);
          const release = { id: 1, tag_name, draft, prerelease, target_commitish };
          releases.push(release);
          return { data: release };
        },
        listReleaseAssets: async () => assets,
        uploadReleaseAsset: async ({ name, data }) => {
          if (assets.length === 3 && failUpload) throw new Error('Mock upload failure');
          assert.ok(!assets.some(asset => asset.name === name));
          writes.uploads++;
          assets.push({
            name, state: 'uploaded', size: data.length, digest: `sha256:${createHash('sha256').update(data).digest('hex')}`
          });
        },
        updateRelease: async ({ draft }) => { writes.publications++; releases[0].draft = draft; }
      }
    },
    paginate: (method, params) => method(params)
  };
  const execute = () => run[1](github, context, {}, { env }, wrapRequire);
  await assert.rejects(execute, /Mock upload failure/);
  const originalDigest = assets[0].digest;
  assets[0].digest = 'sha256:mismatch';
  await assert.rejects(execute, /assets differ/);
  assert.deepEqual(writes, { tags: 1, drafts: 1, uploads: 3, publications: 0 });
  assets[0].digest = originalDigest;
  failUpload = false;
  await execute();
  assert.deepEqual(writes, { tags: 1, drafts: 1, uploads: 15, publications: 1 });
  await execute();
  assert.deepEqual(writes, { tags: 1, drafts: 1, uploads: 15, publications: 1 });
  tagSha = 'b'.repeat(40);
  await assert.rejects(execute, /different commit/);
});
