// Azure-only ACI enrollment. A leased, private blob checkpoints the key BEFORE
// enrollment, then the enrolled configuration BEFORE any task may run.
import { createHash, createPrivateKey, createPublicKey, generateKeyPairSync, randomUUID } from 'node:crypto'
import { chmod, mkdir, readFile, writeFile } from 'node:fs/promises'
import { join } from 'node:path'
import { spawn } from 'node:child_process'
import { pathToFileURL } from 'node:url'
import { setTimeout as delay } from 'node:timers/promises'

export class BootstrapError extends Error {}
const fail = (message) => { throw new BootstrapError(message) }
const fingerprint = (key) => createHash('sha256').update(key.trim()).digest('hex').slice(0, 16)
const keyFingerprint = (pem) => createHash('sha256').update(createPublicKey(createPrivateKey(pem)).export({ type: 'spki', format: 'der' })).digest('hex')

export function configuration(env) {
  let blob, server
  try { blob = new URL(env.MBUS_AZURE_IDENTITY_BLOB_URL); server = new URL(env.MBUS_SERVER) } catch { fail('Configure the identity blob URL and mbus server URL.') }
  if (blob.protocol !== 'https:' || !/^[a-z0-9]{3,24}\.blob\.core\.windows\.net$/.test(blob.hostname)
    || blob.username || blob.password || blob.search || blob.hash || !/^\/[a-z0-9-]+\/[a-zA-Z0-9._-]+$/.test(blob.pathname))
    fail('Use a private Azure Blob HTTPS URL without credentials or a query string.')
  if (server.protocol !== 'https:' || server.username || server.password || server.search || server.hash) fail('Use an HTTPS mbus server URL without credentials.')
  if (!/^[0-9a-f-]{36}$/i.test(env.MBUS_AZURE_CLIENT_ID ?? '')) fail('Configure the runner managed identity client ID.')
  if (!/^\/subscriptions\/[^/]+\/resourceGroups\/[^/]+\/providers\/Microsoft.ContainerInstance\/containerGroups\/[^/]+$/i.test(env.MBUS_AZURE_RESOURCE_ID ?? ''))
    fail('Configure the container group resource ID.')
  return { blob: blob.href, server: server.href.replace(/\/+$/, ''), clientId: env.MBUS_AZURE_CLIENT_ID,
    resourceId: env.MBUS_AZURE_RESOURCE_ID.toLowerCase(), home: env.MBUS_HOME ?? '/home/runner/.mbus' }
}

export function azureStorage(config, { fetch: request = globalThis.fetch, sleep = delay, now = Date.now, signal } = {}) {
  let token, expires = 0
  async function accessToken() {
    if (token && now() < expires - 60_000) return token
    const url = new URL('http://169.254.169.254/metadata/identity/oauth2/token')
    url.search = new URLSearchParams({ 'api-version': '2018-02-01', resource: 'https://storage.azure.com/', client_id: config.clientId })
    const response = await request(url, { headers: { Metadata: 'true' }, redirect: 'error', signal: requestSignal() })
    if (!response.ok) {
      const error = new BootstrapError(`Managed identity authentication failed (${response.status}).`)
      error.retryable = true
      throw error
    }
    const value = await response.json()
    if (!value.access_token) fail('Managed identity did not return a token.')
    token = value.access_token
    expires = Number(value.expires_on) * 1000
    return token
  }
  function requestSignal() { return signal ? AbortSignal.any([signal, AbortSignal.timeout(30_000)]) : AbortSignal.timeout(30_000) }
  async function send(method, { headers = {}, body, leaseAction, expected = [200], attempts = 30 } = {}) {
    // Cold RBAC assignments and IMDS can take a few minutes. Never start the
    // runner on uncertainty; retries are bounded and never print response bodies.
    for (let attempt = 0; attempt < attempts; attempt++) {
      signal?.throwIfAborted()
      let response
      try {
        response = await request(config.blob + (leaseAction ? '?comp=lease' : ''), {
          method, body, redirect: 'error', signal: requestSignal(),
          headers: { Authorization: `Bearer ${await accessToken()}`, 'x-ms-version': '2023-11-03',
            'x-ms-date': new Date(now()).toUTCString(), ...(leaseAction ? { 'x-ms-lease-action': leaseAction } : {}), ...headers },
        })
      } catch (error) {
        if (signal?.aborted || (error instanceof BootstrapError && !error.retryable)) throw error
        if (attempt === attempts - 1) fail(error instanceof BootstrapError ? error.message : 'Azure identity storage could not be reached. The runner was not started.')
      }
      if (response && expected.includes(response.status)) return response
      if (response && ![401, 403, 408, 429, 500, 502, 503, 504].includes(response.status))
        fail(`Azure identity storage refused ${leaseAction ?? method} (${response.status}). The runner was not started.`)
      if (response?.status === 401) { token = undefined; expires = 0 }
      await response?.body?.cancel()
      if (attempt < attempts - 1) await sleep(10_000, undefined, { signal })
    }
    fail('Azure identity storage access did not become available. Check the managed identity role assignment.')
  }
  return {
    async ensure(record) {
      await send('PUT', { headers: { 'x-ms-blob-type': 'BlockBlob', 'If-None-Match': '*' }, body: JSON.stringify(record), expected: [201, 409, 412] })
    },
    async acquire() {
      const id = randomUUID()
      for (let attempt = 0; attempt < 30; attempt++) {
        const response = await send('PUT', { leaseAction: 'acquire', headers: { 'x-ms-proposed-lease-id': id, 'x-ms-lease-duration': '60' }, expected: [201, 409] })
        if (response.status === 201) return id
        await sleep(10_000, undefined, { signal })
      }
      fail('Another bootstrap still owns the identity lease. The runner was not started.')
    },
    async read() { return (await send('GET')).json() },
    async write(record, id) { await send('PUT', { headers: { 'x-ms-blob-type': 'BlockBlob', 'x-ms-lease-id': id }, body: JSON.stringify(record), expected: [201] }) },
    async renew(id) { await send('PUT', { leaseAction: 'renew', headers: { 'x-ms-lease-id': id }, expected: [200], attempts: 1 }) },
    async release(id) { await send('PUT', { leaseAction: 'release', headers: { 'x-ms-lease-id': id }, expected: [200], attempts: 1 }) },
  }
}

function validate(record, config) {
  if (record?.version !== 1 || record.resourceId !== config.resourceId || record.server !== config.server)
    fail('The saved identity belongs to a different container or mbus server. The runner was not started.')
  if (!['empty', 'pending', 'enrolled'].includes(record.state)) fail('The saved identity checkpoint is invalid.')
  if (record.state === 'empty') return
  try {
    const key = createPrivateKey(record.privateKey)
    if (key.asymmetricKeyType !== 'ec' || key.asymmetricKeyDetails.namedCurve !== 'prime256v1') throw new Error()
    if (!record.config || record.config.server !== config.server || record.config.keyStore !== 'file') throw new Error()
    if (record.state === 'pending' && (!/^[a-f0-9]{16}$/.test(record.config.pendingRunnerEnrollment ?? '') || record.config.machineId)) throw new Error()
    if (record.state === 'enrolled' && (!record.config.machineId || !record.config.workspaceId || record.config.pendingRunnerEnrollment
      || record.config.keyFingerprint !== keyFingerprint(record.privateKey))) throw new Error()
  } catch { fail('The saved identity checkpoint is invalid. Restore the identity blob; do not replace it with a new identity.') }
}

async function restore(config, record) {
  await mkdir(config.home, { recursive: true, mode: 0o700 })
  await writeFile(join(config.home, 'config.json'), JSON.stringify(record.config), { mode: 0o600 })
  await writeFile(join(config.home, 'machine-key.pem'), record.privateKey, { mode: 0o600 })
  if (process.platform !== 'win32') {
    await chmod(config.home, 0o700)
    await chmod(join(config.home, 'config.json'), 0o600)
    await chmod(join(config.home, 'machine-key.pem'), 0o600)
  }
}

export async function bootstrapIdentity(config, env, { storage, login, sleep = delay, signal } = {}) {
  const cancellation = new AbortController()
  const linked = signal ? AbortSignal.any([signal, cancellation.signal]) : cancellation.signal
  const original = { version: 1, resourceId: config.resourceId, server: config.server, state: 'empty' }
  await storage.ensure(original)
  const lease = await storage.acquire()
  let renewalFailure
  const renewer = (async () => {
    try {
      while (true) { await sleep(20_000, undefined, { signal: linked }); await storage.renew(lease) }
    } catch (error) {
      if (!linked.aborted) { renewalFailure = error; cancellation.abort() }
    }
  })()
  try {
    let record = await storage.read()
    validate(record, config)
    if (record.state === 'empty') {
      if (!env.MBUS_API_KEY?.trim()) fail('Supply the Sign in runners key as secure MBUS_API_KEY for first enrollment.')
      const { privateKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1', privateKeyEncoding: { type: 'pkcs8', format: 'pem' } })
      record = { ...original, state: 'pending', privateKey,
        config: { server: config.server, keyStore: 'file', pendingRunnerEnrollment: fingerprint(env.MBUS_API_KEY) } }
      await storage.write(record, lease)
    }
    if (record.state === 'pending' && (!env.MBUS_API_KEY || record.config.pendingRunnerEnrollment !== fingerprint(env.MBUS_API_KEY)))
      fail('Finish pending enrollment with the original Sign in runners key.')
    await restore(config, record)
    if (record.state === 'pending') {
      await login(linked)
      linked.throwIfAborted()
      const enrolledConfig = JSON.parse(await readFile(join(config.home, 'config.json'), 'utf8'))
      const privateKey = await readFile(join(config.home, 'machine-key.pem'), 'utf8')
      if (keyFingerprint(privateKey) !== keyFingerprint(record.privateKey)) fail('Enrollment replaced the checkpointed machine key. The runner was not started.')
      record = { ...record, state: 'enrolled', config: enrolledConfig, privateKey }
      validate(record, config)
      await storage.write(record, lease)
    }
    linked.throwIfAborted()
    if (renewalFailure) throw renewalFailure
    // No work is allowed until the final identity has been durably checkpointed.
    return record.config.machineId
  } finally {
    cancellation.abort()
    await renewer
    await storage.release(lease)
  }
}

function command(file, args, env, signal, capture = false) {
  return new Promise((resolve, reject) => {
    const child = spawn(file, args, { env, signal, stdio: capture ? 'ignore' : 'inherit' })
    child.once('error', () => { if (!signal?.aborted) reject(new BootstrapError('The mbus process could not be started.')) })
    child.once('exit', (code) => code === 0 ? resolve() : reject(new BootstrapError(capture
      ? 'Runner enrollment failed. Check the server URL, Sign in runners key and workspace approval.' : `The runner exited (${code ?? 'signal'}).`)))
  })
}

async function main() {
  const cancellation = new AbortController()
  for (const signal of ['SIGTERM', 'SIGINT']) process.once(signal, () => cancellation.abort())
  try {
    if (process.getuid?.() !== 10001) fail('The ACI runner image must use UID 10001.')
    process.umask(0o077)
    const config = configuration(process.env)
    const env = { ...process.env, MBUS_HOME: config.home, MBUS_KEY_STORE: 'file' }
    const storage = azureStorage(config, { signal: cancellation.signal })
    await bootstrapIdentity(config, env, { storage, signal: cancellation.signal,
      login: (signal) => command('/usr/local/bin/mbus-entrypoint', ['login'], env, signal, true) })
    // The enrollment key is never needed by the running agents or later restores.
    delete env.MBUS_API_KEY
    delete env.MBUS_API_KEY_FILE
    delete env.MBUS_AZURE_CLIENT_ID
    delete env.MBUS_AZURE_IDENTITY_BLOB_URL
    console.log('Azure runner identity restored; starting mbus runner.')
    await command('/usr/local/bin/mbus-entrypoint', ['runner'], env, cancellation.signal)
  } catch (error) {
    if (!cancellation.signal.aborted) console.error(error instanceof BootstrapError ? error.message : 'Azure runner bootstrap failed; the runner was not started.')
    process.exitCode = cancellation.signal.aborted ? 143 : 1
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main()
