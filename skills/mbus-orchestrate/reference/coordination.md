# Work-item coordination

Claims prevent two cooperating orchestrators in the connected mbus workspace from owning the same
explicitly claimed tracker item. They do not lock GitHub/ADO, other mbus workspaces, related items,
or parent/child hierarchies. Ordinary `refs` remain descriptive. Clients that omit
`claimedWorkItems` still work but acquire no coordination guarantee; sends are not session-fenced.
Do not fall back to an unclaimed ensure after a claim fails or a server lacks this protocol.

## Start or inquire

Read-only inquiry (also accepts a primary `file:` project key):

```json
find_project { "key": "https://github.com/Owner/Repo/issues/42" }
```

Returns `{ "project": { ... } }` if visible, otherwise `{}` (missing and hidden are indistinguishable).
Existing mixed-case and zero-padded project keys resolve to their original project ID, settings and history.
If equivalent persisted projects already collide, discovery chooses the oldest creation time, then ordinal
project ID; other project histories remain accessible by ID. ADO project-segment aliases and organization-level
URLs share that organization-wide ID index too. Lookup may backfill the canonical key alias.
Lookup can find either the primary project key or an explicitly claimed secondary item; follow with
`status {projectId}` for tasks. It creates neither projects nor claims.

Start a batch using an arbitrary grouping key and all explicitly implemented tracker items:

```json
ensure_project {
  "key": "file:batch-42-43.md",
  "title": "Implement two backlog items",
  "executionScope": "owner-runners",
  "claimedWorkItems": ["gh:owner/repo#42", "gh:owner/repo#43"],
  "refs": [{"kind":"github.issue","repo":"owner/repo","number":42}]
}
```

Success adds `coordinationAcquired: true` and `coordinatorInboxId` to the existing `{project, created}` result. All requested
items and the project's coordinator are acquired in one Azure Table transaction. There are 1–50
input items, each at most 1024 characters (also after normalization), and at most 30,000 characters
in the serialized canonical item list; duplicates normalize to one identity. GH keys and HTTPS
issue/pull URLs normalize owner/repository casing and positive numeric IDs (leading zeros disappear).
ADO keys, project-level and organization-level dev.azure.com work-item URLs and org.visualstudio.com work-item URLs share an identity
regardless of work-item type; IDs are organization-wide, so changing the project segment does not
bypass a claim. References must identify actual items, not repository roots.

On `conflict`, report that another coordinator is underway and stop dispatch. The error contains no
private project metadata; `find_project` reveals only projects the caller may see. A failed batch
acquires no subset. It may leave an empty project grouping created by ensure, which owns no claims.

## Execution scope

`executionScope` is `"workspace"` (any eligible workspace runner) or `"owner-runners"` (only the
project owner's own runners). It is set once, when `ensure_project` creates the project, and never
changes.

- **Choose before ensure.** `find_project` first. An existing project keeps its stored scope: pass
  that value. A new project honors the user's explicit request or standing instruction (such as
  `AGENTS.md`); with none, ask once ("My runners only" or "Any eligible workspace runner"). Omitting
  it creates a `workspace` project, so always pass it. The skill does not save a new preference
  across sessions by itself.
- **Mismatch is a conflict.** Passing the other scope for an existing project fails with `conflict`
  on field `executionScope`. If the user wants the other scope, they need a new project (a new
  grouping key); never retry with the stored, wider scope on their behalf.
- **Verify, fail closed.** `ProjectInfo` from `ensure_project` and `find_project` carries
  `executionScope`, and for `owner-runners` the pinned `executionOwnerSubjectId`. Dispatch only when
  the value equals your choice and, for `owner-runners`, the owner is set. A server that ignores or
  omits the field, or returns another value, is too old: stop and tell the user it needs an
  upgrade. Never dispatch unrestricted, and never treat tags, site labels or custom roles as an
  ownership guarantee.
- **Who counts as the owner.** The authenticated person who created the project. Eligible machines
  are personal runners registered in mbus to that subject (`ownerSubjectId`). Shared machines are
  excluded even when that person enrolled them. Ownership is mbus registration, not the OS account
  or a coding agent's provider sign-in.
- **Routing is unchanged.** Send to roles as usual; the server intersects the role's holders with
  the eligible machines. No tags or role changes are needed. `directory` is not filtered by
  project scope, so don't call every online agent eligible; `send`'s available agents and waiting
  reasons are authoritative. Offline or busy owned holders: the task queues for them. Waiting
  reason `owner-runners`: no owned personal runner matches; tell the user rather than resending.
- **It stays.** Tasks, fixes, retries, replays and moves within the project keep the restriction;
  there is no fallback to other people's or shared machines.
- **Trust limit.** The server recognises descendants it can link (follow-ups, retries, replays). A
  deliberately independent message a machine sends after its run has ended cannot be recognised as
  a descendant of the project; don't claim it is restricted.

## Resume and interruption

Calling `open_inbox` before or after claiming uses the same inbox owned by that local server.
A new local server opens a fresh inbox; newest-session logs supply resume metadata only and never
select another session's inbox. Repeated ensure in the same local server reuses its coordinator inbox. Runner resumes explicitly
supplied with their saved `SessionRecord` reuse it too. An interactive MCP restart must deliberately
supply the returned `coordinatorInboxId` for its previously saved inbox, the exact original claimed item set
and the project's stored `executionScope`;
the local server requires a saved inbox for this workspace. Remote callers open an inbox themselves
and supply its ID; the service checks subject, machine and agent ownership. Inbox IDs distinguish
sessions even when the account, machine, working folder and agent are the same. Do not infer
coordination ownership from the newest session log, project starter or matching agent alone.

There is no expiry or automatic stealing of coordination claims. Advancing time, losing a connection
or an expired session inbox does not release active work. Resume the original inbox while valid;
otherwise report the interruption. Session inboxes default to 24 hours; an expired inbox is refused,
even when a saved local record exists. Explicit refusal by the user means stop: do not retry with an
unclaimed project, omit items, open another session to bypass the refusal, or cancel work to win a claim.
If the project is visible but you cannot manage/cancel it, report its status and ask its starter or
an administrator to resolve it. A completed project cannot be cancelled or reopened: use a new grouping
key for a deliberate new attempt, after its open tasks and admitted sends have drained. An authorized person can deliberately cancel the old project
(`cancel {projectId, reason}`), which stops its tasks and closes admission to new ones. After
cancellation completes, start a replacement using a new project grouping key and the desired item
list. Never cancel someone else's work merely to acquire it. Same-account sessions are distinct
coordinators, but are not separate security principals: a deliberately supplied valid inbox ID can
resume that account's coordination.

`complete_project` and project cancellation release claims once no tasks remain active, no unregistered
admitted send can still publish work, and cancellation has finished draining. Admission registers durable
per-send reservations before message work begins; closing the project fences new reservations across API
instances. Once a send has durably registered its thread and project task, the task guards ownership and
the reservation is released before incidental counters run. A registered task and its pending cleanup
reservation count only once toward fan-out.

Cancellation persists a drain fence with closing admission, and saves its messaging thread IDs before
asking delivery to stop them. A failure or restart retains that fence; repeat the authorized cancellation
to resume the drain even when task projections already look terminal. Late admitted sends drain their
own cancellation before releasing their reservations.

Interrupted sends hand their outbox to recovery after durable registration, or after the sender has
stopped writing. The existing outbox sweep repairs committed work or rolls back an incomplete send,
then finalizes its reservation. Cleanup errors are logged and leave durable recovery state; they do not
replace the send result or original failure. An ambiguous crash before this handoff stays fenced without
expiry: report it rather than clearing reservations or assuming that an old sender has stopped. Neither
an inbox expiry nor the outbox grace period authorizes taking over an active or ambiguous send.

The same tradeoff applies to a dead-letter replay claim, including a replay without a project.
Interrupted replay recovery starts only after a durable handoff. A crash before that handoff can
leave the original dead letter reserved with no automatic recovery: report the interruption.
Elapsed time and inbox expiry do not authorize stealing the claim. Discard deletes the original
dead letter and is not a recovery procedure.

Ownership acquire/release events, including project, subject, inbox, item list and time, are stored
atomically with the transition in `projectkeys` under `ownership-audit|<ownership-id>|acquired` and
`...|released`; idempotent resume emits no duplicate. Release uses conditional atomic deletes, so
concurrent cleanup cannot remove a replacement claim. Completed or cancelled projects cannot acquire
coordination or accept new tasks. No active-project takeover exists, so there is no expiring coordinator
lease to fence or renew.

File-only projects may still use `ensure_project` without claims, but file paths have no tracker
coordination guarantee. State this limitation for file-only requests; do not describe a project key
or ordinary ref as a claim. A file grouping can claim explicitly implemented tracker items as above.

Budget notices and permission refusals still apply after coordination succeeds. Claims do not grant
project visibility, management permissions or permission to change tracker assignees or states.
