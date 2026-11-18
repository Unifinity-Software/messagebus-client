# Security model: end-to-end encryption

Message bodies and attachments are encrypted on the sending machine and decrypted on the receiving
machine. The messagebus.ai service stores and routes them but cannot read them. This page says what is
encrypted, how, which fields the service can see, and what the scheme does not protect against.

The code is `src/MessageBus.Crypto`, used by the `mbus` CLI, its local MCP server and the runner. The
service side only handles public keys and sealed grants.

## In one paragraph

Every role has a key pair, and so does the workspace. A sender encrypts each body or file with a fresh
random key and locks that key with the target role's public key. Only machines that hold the role's
private key can unlock it. A machine gets a role's private key through a **key grant**: an admin's
machine encrypts the role key to that machine's own key and signs the result. The service stores public
keys and grants, neither of which lets it read anything.

## Keys

| Key | Who generates it | Private half lives | Public half |
|---|---|---|---|
| Machine device key (P-256) | `mbus login` on the machine | Only on that machine | Registered with the service (scope `machine`) |
| Workspace key (P-256) | A workspace admin's machine | Machines with a grant for it | Registered with the service (scope `workspace`) |
| Role key (P-256), one per role | A workspace admin's machine | Machines whose agents hold the role, through grants | Registered with the service (scope `role`) |

- The **machine device key** does three jobs: it signs the machine's requests (ES256, see
  `MachineRequestSigning`), it signs grants when the machine belongs to an admin, and role keys are
  sealed to it. Using one P-256 key for both ECDSA and ECDH keeps one key per machine, one file and one
  fingerprint to check.
- Keys are stored as PKCS#8 PEM, optionally passphrase-encrypted (`KeyPair.ExportPem`,
  `ExportEncryptedPem`). The format is the same on Windows and macOS. Where the file lives and how it is
  protected (OS keychain, DPAPI, file permissions) is the CLI's decision.
- A key's **fingerprint** is the lowercase hex SHA-256 of its public key (SubjectPublicKeyInfo DER).
  Fingerprints are what a person compares to check a key out of band.
- RSA keys (2048 bits or more) are accepted as recipients, wrapped with RSA-OAEP-256, for keys held in an
  external KMS or HSM later. Everything messagebus.ai generates is P-256.

## How a body is encrypted

1. A random 256-bit content key and a random 96-bit nonce.
2. The body (`MessageBody` JSON) is encrypted with **AES-256-GCM**. The 16-byte tag is appended to the
   ciphertext. The associated data binds the algorithms, the recipient key id and the payload kind, so
   none of them can be swapped on the stored record without the check failing.
3. The content key is wrapped to the recipient public key with **ECDH-ES+A256KW** (RFC 7518): an
   ephemeral P-256 key, the Concat KDF with SHA-256, then AES key wrap (RFC 3394). For an RSA recipient,
   **RSA-OAEP-256**.
4. The result travels as `EncryptionInfo` (algorithms, key id, wrapped key, nonce, ephemeral public key)
   next to the ciphertext.

Which key a message is sealed to (`KeyDirectory.RecipientFor`): a role target uses the role's current
key; agent, capability and inbox targets use the workspace key, because any machine in the workspace
may serve them.

Known-answer tests pin the primitives to published vectors: RFC 3394 §4.6 (key wrap), RFC 7518
appendix C (Concat KDF) and the GCM specification's test case 14.

## How an attachment is encrypted

Attachments (git bundles, patches, files) can be large, so they are encrypted as a stream: an 8-byte
header (`MBS1` and the chunk size), then the content in 64 KiB chunks, each encrypted with AES-256-GCM
under the same content key. Each chunk's nonce is the random base nonce combined with the chunk's index
and a "last chunk" flag. A full-size chunk is never the last one; the stream always ends with a short (or
empty) last chunk. So reordered chunks, a stream cut at any point (including exactly at a chunk
boundary) and bytes added at the end all fail to decrypt. The size of the encrypted file is known in
advance (`MessageCrypto.SealedLength`), and sealing reports its SHA-256 for `AttachmentRef`.

When decrypting, plaintext is written chunk by chunk as each chunk checks out. If decryption fails part
way, what was written is incomplete: the receiver writes to a temporary file and keeps it only on success.

## Key grants

A grant gives one machine one version of a role key (or the workspace key).

1. The admin's machine encrypts the role's private key (PKCS#8) to the receiving machine's device key,
   using the same body scheme above.
2. It signs, with its own device key (ES256), the text built by `GrantCrypto.CanonicalString`: workspace
   id, role, key version, the role key's fingerprint, machine id, the SHA-256 of the sealed key, the
   granting person and the signing key id.
3. The service accepts the grant only from a workspace admin, signed by a machine key that same person
   registered, sealed to the receiving machine's current key, for a key version that exists. It stores
   it and records `keys.granted`.
4. The receiving machine fetches its grants, checks the signature, decrypts the role key with its own
   device key and checks the decrypted key matches the role key's published fingerprint before using it.

## Rotation and removal

- Publishing a new version of a workspace or role key retires the previous version. A retired version
  is no longer used for new messages but still decrypts for `MessageLimits.MessageRetentionDays`
  (30 days), the time messages sealed to it are kept. `keys.rotated` is recorded.
- Machine keys rotate the same way: the machine publishes a new version of its own key, and admins grant
  role keys to it again. Only a machine enrolled in the workspace has a machine key: `POST /keys` refuses a
  machine id that isn't one.

### When a machine leaves a role or is removed

Private keys exist only on machines, so the service can't replace a key itself. It works out, every time
it is asked (`GET /keys/rotation`), who may hold each key, so every way of losing
access is covered the same way (leaving the role, being taken out of it, the machine being removed, its
owner leaving the workspace or losing the admin role) and rejoining before the key is replaced needs no
clean-up:

- the workspace key: every machine enrolled in the workspace;
- a role key: enrolled machines with an agent that holds the role, plus personal machines whose owner
  manages keys (an admin's computer makes and grants role keys, so it keeps them).

A key **needs replacing** when any grant of its current version, revoked or not, went to a machine that may
no longer hold it. The answer lists those machines and why (`left-role`, `machine-removed`) and the machines
that should hold the next version.

1. At once: the service stops handing that machine the role's grants (`GET /keys/grants` leaves them out).
   A removed machine can't sign in at all.
2. On the next send to that role (or any send sealed to the workspace key) from an admin's computer, its
   `mbus` replaces the key before sealing anything: revokes the grants of the machines that lost access
   (`keys.grant-revoked`), makes and signs the next version, publishes it (`keys.rotated`) and grants it to
   the machines that should hold it and to itself. `mbus keys rotate <role|workspace>` does the same on
   demand.
3. Until then, `mbus` on any other computer refuses to seal to that key, with a message that names the
   machines and the command to run. So no message sent through `mbus` is sealed to a key a machine that lost
   access still holds.
4. Remaining holders find the new version when a message needs it (a missing key makes `mbus` fetch its
   grants again). The machine that lost access can still read what it could read before; nothing sealed to
   the new version.

**The remaining window.** The service itself doesn't refuse a sealed body addressed to a key that needs
replacing: a client other than this `mbus` (or an `mbus` from before this change) could still seal to the old
version until an admin's computer replaces it. Those messages are readable by the machine that left, if it
can get the ciphertext (a machine that only left a role is still a signed-in member and can read thread
messages through the API; a removed machine can't sign in). Closing it means the messaging service checking
`Encryption.KeyId` against `GET /keys/rotation` on send.

## Recovery kit

The service cannot recover keys. An admin's machine can write a **recovery kit**: every workspace and
role private key it holds, in one text file encrypted with a passphrase (PBKDF2-SHA256, 600,000
iterations, then AES-256-GCM, bound to the workspace id). The kit is made and opened on the admin's
machine (`mbus keys recovery-kit [--out file]`: every role and workspace key version the machine holds
that hasn't expired); the service only records that one was made (`keys.recovery-kit-downloaded`). If every machine
holding a role key is lost and there is no kit, messages sealed to that key are unreadable.

## What the service can see

The envelope stays readable so the service can route, deliver, retry, show the dashboard and keep totals.

- Message id, thread id, parent thread, project id
- Message type (for example `task.implement`) and title
- Target (role, agent, capability or inbox)
- Sender (person, agent, machine), as the service verified it
- Expectation, priority, hop count, dedupe key, expiry
- Refs (PR, issue, work item, commit links) and labels
- Body size and SHA-256 of the **ciphertext**
- Attachment id, file name, format, branch, base commit, size and SHA-256 of the **ciphertext**
- Timing: sent, delivered, leased, settled
- Settle status, usage numbers (tokens, seconds, cost) and progress updates
- W3C trace id
- Encryption details: algorithms, recipient key id, wrapped key, nonce, ephemeral public key
- Public keys, fingerprints, versions, retirement and expiry times
- Grants: role, version, machine, sealed private key, signer and signature

Titles, refs, labels and progress text are written by agents and are **not** encrypted. Agents are told
to keep secrets and code out of them.

## What the service never sees

- Message bodies (summary, details, structured data) of messages sent through `mbus`.
- Attachment contents.
- Any private key: machine, workspace or role. The key endpoints refuse anything that isn't a public key.
- Recovery kits and their passphrases.

## Cloud agents

A cloud agent is an agent hosted somewhere else that takes its role's tasks over HTTPS (Integrations, "Cloud agents").

- **Requests to it are signed.** Each task is a POST signed with the workspace's ES256 signing key
  (`X-Mbus-Signature: t={unix seconds},kid={key id},sig={signature of "{t}.{body}"}`); the receiver checks it with the
  published public key. The service never sends it a workspace secret it could replay elsewhere.
- **It calls back with a per-delivery token.** The task carries `Authorization: MbusDelivery <token>`, which the
  heartbeat, progress and settle routes accept for that one delivery and attempt, while the delivery is still with that
  agent, for at most 24 hours. The token names the workspace, target, agent, delivery and
  attempt. Every other endpoint refuses it, and removing the target or its creator leaving the workspace stops it at
  once.
- **It reads sealed bodies with a machine key of its own.** A cloud agent can't hold a role key without a device key,
  so, like a runner container, it enrolls itself once as a shared machine with a "Sign in runners" API key
  (keeping the machine's private key in its own secret store) and the target is linked to that machine
  (`keyMachineId`). Its agent then sits on that machine, so it counts as a holder machine for key grants and
  rotation exactly like a runner. A target with no key machine is offered only unsealed messages.

### Inbound webhooks

Inbound webhooks are authenticated per hook: GitHub and Azure DevOps by their signed payloads, generic webhooks by an
HMAC signature or a bearer secret, each refused and counted when wrong. Client certificates (mTLS) are not supported.

## Secret scan before sending

Because the service can't read bodies, it can't scan them. The sending machine runs `SecretScanner`
before encrypting and refuses to send a body that contains an obvious credential (private keys; AWS,
GitHub, Slack, Azure Storage, Anthropic, OpenAI, Google, Stripe and messagebus.ai keys; JWTs), unless
the sender passes the override flag. It only recognises well-known formats. Attachments (compressed git
bundles) are not scanned.

## Limits and trust

- **Protects against** a breach of the service's storage, backups or logs, and against an operator who
  reads stored data.
- **Fingerprints are pinned on each machine.** Public keys come from the service, so a dishonest
  service could list a role key of its own and read messages sealed to it, or swap an admin's machine key
  and hand out grants of its own. Each machine remembers, per server and workspace, every role and workspace key version
  it accepted and each signing admin computer's machine key (`trusted-keys.v2.json` in mbus's settings folder,
  `MessageBus.Mcp/KeyTrust.cs`). Every role and workspace key is signed by the admin computer that made it
  (`KeySignatures`: workspace, scope, owner and fingerprint; the service checks the signer is the publisher's
  own current machine key). Then:
  - the first key seen for a role is accepted (trust on first use), with its signer;
  - a version already accepted must keep its fingerprint; an older version, or a fingerprint seen before
    under another version, is refused;
  - a newer version is accepted without asking only when it is signed by an admin computer already trusted
    (a normal rotation); otherwise sending to it stops;
  - a grant signed by an admin computer whose machine key changed, or holding a version whose fingerprint
    changed, isn't used, and opening a message sealed to that key stops with the reason.

  A refusal says what changed and how to accept it after checking: `mbus keys list` shows every key's
  fingerprint and trust; `mbus keys trust <role|workspace|mch_…>` accepts the current one.
- **Does not protect against** someone who can make the service lie to a machine that has never seen the
  key (trust on first use), or a workspace admin acting in bad faith:
  - A machine's first view of a role key, and of an admin computer it has never seen, comes from the
    service. Comparing `mbus keys list` with the Security page or with an admin is the check.
  - An admin who signs in again on a computer gets a new machine key; other machines then refuse grants and
    rotations signed by it until someone runs `mbus keys trust <that machine's id>`.
  - Anyone with a role's public key can encrypt a message to the role. The ciphertext doesn't prove who
    wrote it; the sender identity on the envelope comes from the service's sign-in checks.
  - An admin can grant a role key to any machine. Roles that anyone can join are protected against a
    storage breach, not against a party who can add members. Keys rotate when a member is removed.
- **Remote `/mcp` endpoint.** Clients that can't run `mbus` use the remote endpoint, send plaintext
  bodies and are protected in transit (TLS) and at rest (storage encryption) only, not end to end.
  The workspace policy `requireEndToEndEncryption` refuses such messages.
