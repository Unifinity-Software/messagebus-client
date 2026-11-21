import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdtemp, readFile, rm, writeFile, stat } from 'node:fs/promises'
import { join } from 'node:path'
import { tmpdir } from 'node:os'
import { createHash, createPrivateKey, createPublicKey } from 'node:crypto'
import { azureStorage, bootstrapIdentity, configuration } from './aci-bootstrap.mjs'

const env = {
  MBUS_SERVER: 'https://mbus.example.test', MBUS_API_KEY: 'fixture-enrollment',
  MBUS_AZURE_CLIENT_ID: '11111111-1111-1111-1111-111111111111',
  MBUS_AZURE_RESOURCE_ID: '/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/example/providers/Microsoft.ContainerInstance/containerGroups/runner',
  MBUS_AZURE_IDENTITY_BLOB_URL: 'https://examplerunner.blob.core.windows.net/runner-identity/identity.json',
}

function memoryStorage() {
  let record, held = false
  return {
    get record() { return structuredClone(record) },
    get held() { return held },
    async ensure(value) { record ??= structuredClone(value) },
    async acquire() { assert.equal(held, false); held = true; return 'lease' },
    async read() { assert.equal(held, true); return structuredClone(record) },
    async write(value, lease) { assert.equal(held, true); assert.equal(lease, 'lease'); record = structuredClone(value) },
    async renew() { assert.equal(held, true) },
    async release() { held = false },
  }
}

async function home(t) {
  const path = await mkdtemp(join(tmpdir(), 'mbus-aci-'))
  t.after(() => rm(path, { recursive: true, force: true }))
  return path
}

async function enroll(config) {
  const file = join(config.home, 'config.json')
  const saved = JSON.parse(await readFile(file, 'utf8'))
  const { pendingRunnerEnrollment, ...rest } = saved
  const pem = await readFile(join(config.home, 'machine-key.pem'), 'utf8')
  const keyFingerprint = createHash('sha256').update(createPublicKey(createPrivateKey(pem)).export({ type: 'spki', format: 'der' })).digest('hex')
  await writeFile(file, JSON.stringify({ ...rest, machineId: 'mch_fixture', workspaceId: 'w_fixture', keyFingerprint, enrolledWithApiKey: pendingRunnerEnrollment }))
}

test('first boot checkpoints before enrollment and restores the same identity into a new filesystem without the enrollment key', async t => {
  const storage = memoryStorage()
  const config = configuration({ ...env, MBUS_HOME: await home(t) })
  let enrolledKey
  await bootstrapIdentity(config, env, { storage, login: async () => {
    assert.equal(storage.record.state, 'pending')
    enrolledKey = await readFile(join(config.home, 'machine-key.pem'), 'utf8')
    assert.equal(enrolledKey, storage.record.privateKey)
    await enroll(config)
  } })
  assert.equal(storage.record.state, 'enrolled')
  assert.equal(storage.held, false)
  const restarted = { ...config, home: await home(t) }
  assert.equal(await bootstrapIdentity(restarted, {}, { storage, login: () => assert.fail('must not enroll again') }), 'mch_fixture')
  assert.equal(await readFile(join(restarted.home, 'machine-key.pem'), 'utf8'), enrolledKey)
  if (process.platform !== 'win32') {
    assert.equal((await stat(restarted.home)).mode & 0o777, 0o700)
    assert.equal((await stat(join(restarted.home, 'machine-key.pem'))).mode & 0o777, 0o600)
  }
})

test('a lost enrollment response recovers using the checkpointed key instead of making another identity', async t => {
  const storage = memoryStorage()
  const config = configuration({ ...env, MBUS_HOME: await home(t) })
  let acceptedKey
  await assert.rejects(bootstrapIdentity(config, env, { storage, login: async () => {
    acceptedKey = await readFile(join(config.home, 'machine-key.pem'), 'utf8')
    throw new Error('simulated response loss')
  } }), /response loss/)
  assert.equal(storage.record.state, 'pending')
  const retry = { ...config, home: await home(t) }
  await bootstrapIdentity(retry, env, { storage, login: async () => {
    assert.equal(await readFile(join(retry.home, 'machine-key.pem'), 'utf8'), acceptedKey)
    await enroll(retry)
  } })
  assert.equal(storage.record.privateKey, acceptedKey)
  assert.equal(storage.record.config.machineId, 'mch_fixture')
})

test('PEM formatting changes do not count as replacing the checkpointed key', async t => {
  const storage = memoryStorage()
  const config = configuration({ ...env, MBUS_HOME: await home(t) })
  await bootstrapIdentity(config, env, { storage, login: async () => {
    const key = join(config.home, 'machine-key.pem')
    await writeFile(key, (await readFile(key, 'utf8')).trim())
    await enroll(config)
  } })
  assert.equal(storage.record.state, 'enrolled')
})

test('failure to save the pending or completed checkpoint fails closed', async t => {
  for (const stage of ['pending', 'enrolled']) {
    const storage = memoryStorage()
    const config = configuration({ ...env, MBUS_HOME: await home(t) })
    const write = storage.write
    let logins = 0
    storage.write = async (record, lease) => { if (record.state === stage) throw new Error('storage unavailable'); await write(record, lease) }
    await assert.rejects(bootstrapIdentity(config, env, { storage, login: async () => { logins++; await enroll(config) } }), /storage unavailable/)
    assert.equal(logins, stage === 'pending' ? 0 : 1)
    assert.equal(storage.held, false)
    if (stage === 'enrolled') assert.equal(storage.record.state, 'pending')
  }
})

test('a pending identity refuses a changed enrollment key and an existing identity refuses another Azure resource', async t => {
  const storage = memoryStorage()
  const config = configuration({ ...env, MBUS_HOME: await home(t) })
  await assert.rejects(bootstrapIdentity(config, env, { storage, login: async () => { throw new Error('interrupted') } }))
  await assert.rejects(bootstrapIdentity(config, { ...env, MBUS_API_KEY: 'different-fixture-key' }, { storage, login: () => assert.fail() }), /original Sign in runners key/)
  await assert.rejects(bootstrapIdentity({ ...config, resourceId: config.resourceId + '-other' }, env, { storage, login: () => assert.fail() }), /different container/)
})

test('losing the lease aborts enrollment', async t => {
  const storage = memoryStorage()
  const config = configuration({ ...env, MBUS_HOME: await home(t) })
  let renew
  const sleep = (_, __, { signal }) => new Promise((resolve, reject) => {
    renew = resolve
    signal.addEventListener('abort', () => reject(signal.reason), { once: true })
  })
  storage.renew = async () => { throw new Error('lease lost') }
  await assert.rejects(bootstrapIdentity(config, env, { storage, sleep, login: async signal => {
    renew()
    await new Promise((_, reject) => signal.addEventListener('abort', () => reject(new Error('enrollment aborted')), { once: true }))
  } }), /enrollment aborted/)
  assert.equal(storage.record.state, 'pending')
  assert.equal(storage.held, false)
})

test('Azure storage uses managed identity, conditional creation and lease IDs, with bounded RBAC propagation retries', async () => {
  const calls = [], sleeps = []
  let attempts = 0
  const storage = azureStorage(configuration(env), {
    sleep: async milliseconds => sleeps.push(milliseconds),
    fetch: async (url, options) => {
      calls.push({ url: String(url), ...options })
      if (String(url).startsWith('http://169.254.169.254/')) {
        assert.equal(options.headers.Metadata, 'true')
        return Response.json({ access_token: 'fixture-token', expires_on: Date.now() / 1000 + 3600 })
      }
      assert.equal(options.redirect, 'error')
      assert.equal(options.headers.Authorization, 'Bearer fixture-token')
      if (options.headers['If-None-Match']) return new Response('', { status: ++attempts < 3 ? 403 : 201 })
      if (options.headers['x-ms-lease-action'] === 'acquire') return new Response('', { status: 201 })
      if (options.method === 'GET') return Response.json({ fixture: true })
      return new Response('', { status: options.headers['x-ms-lease-action'] ? 200 : 201 })
    },
  })
  await storage.ensure({ fixture: true })
  const lease = await storage.acquire()
  assert.deepEqual(await storage.read(), { fixture: true })
  await storage.write({ fixture: 'saved' }, lease)
  await storage.renew(lease)
  await storage.release(lease)
  assert.deepEqual(sleeps, [10_000, 10_000])
  assert.equal(calls.filter(c => c.headers['If-None-Match'] === '*').length, 3)
  assert.equal(calls.find(c => c.headers['x-ms-lease-action'] === 'acquire').headers['x-ms-lease-duration'], '60')
  assert.equal(calls.find(c => c.body === '{"fixture":"saved"}').headers['x-ms-lease-id'], lease)
})

test('terminal storage failures omit Azure response bodies and untrusted URLs cannot receive identity tokens', async () => {
  assert.throws(() => configuration({ ...env, MBUS_AZURE_IDENTITY_BLOB_URL: 'https://attacker.example.test/blob' }), /private Azure Blob/)
  const storage = azureStorage(configuration(env), { fetch: async url => String(url).startsWith('http:')
    ? Response.json({ access_token: 'fixture-token', expires_on: Date.now() / 1000 + 3600 })
    : new Response('sensitive-fixture-do-not-log', { status: 400 }) })
  await assert.rejects(storage.ensure({}), error => error.message.includes('(400)') && !error.message.includes('sensitive-fixture'))
})

test('transient IMDS failure retries and permanent RBAC denial stops after a bounded number of attempts', async () => {
  let tokenCalls = 0, blobCalls = 0, sleeps = 0
  const storage = azureStorage(configuration(env), {
    sleep: async () => { sleeps++ },
    fetch: async url => {
      if (String(url).startsWith('http:')) return ++tokenCalls === 1 ? new Response('', { status: 503 })
        : Response.json({ access_token: 'fixture-token', expires_on: Date.now() / 1000 + 3600 })
      blobCalls++
      return new Response('sensitive-fixture-do-not-log', { status: 403 })
    },
  })
  await assert.rejects(storage.ensure({}), error => error.message.includes('role assignment') && !error.message.includes('sensitive-fixture'))
  assert.equal(tokenCalls, 2)
  assert.equal(blobCalls, 29)
  assert.equal(sleeps, 29)
})

test('a corrupt persisted key cannot trigger fresh enrollment', async t => {
  const storage = memoryStorage()
  const config = configuration({ ...env, MBUS_HOME: await home(t) })
  await bootstrapIdentity(config, env, { storage, login: () => enroll(config) })
  const read = storage.read
  storage.read = async () => ({ ...await read(), privateKey: 'corrupt-fixture' })
  await assert.rejects(bootstrapIdentity(config, env, { storage, login: () => assert.fail('must not replace a persisted identity') }), /checkpoint is invalid/)
})
