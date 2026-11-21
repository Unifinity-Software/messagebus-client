# mbus tool calls for the orchestrator

Argument names are exactly as the tools take them (camelCase JSON). Targets are text:
`role:<name>`, `agent:<agt_id>`, `capabilities:<cap>,<cap>`.

## Project

```json
find_project { "key": "file:samples/work-items/add-due-dates.md" }

ensure_project { "key": "file:samples/work-items/add-due-dates.md",
                 "title": "Add due dates to to-dos",
                 "executionScope": "owner-runners",
                 "refs": [ { "kind": "file", "path": "samples/work-items/add-due-dates.md" } ] }
```

- Keys: `file:<path>`, `gh:<owner>/<repo>#<n>`, `ado:<org>/<project>#<id>`. No spaces.
- Returns `{project: {projectId, status, executionScope, executionOwnerSubjectId?, budgetCapUsd,
  totals, ...}, created}`. Keep `projectId`; every `send` defaults to it, but pass it explicitly
  anyway.
- `executionScope`: `"owner-runners"` (only the owner's personal runners) or `"workspace"`. Always
  pass it: the stored value for an existing project (from `find_project`), otherwise the user's
  choice. Omitted, a new project gets `workspace`. The other scope for an existing project is a
  `conflict` on field `executionScope`: that needs a new project.
- Before any send, check the returned `executionScope` equals your choice and, for
  `owner-runners`, `executionOwnerSubjectId` is set. Otherwise stop: the server needs an upgrade.
  Rules: [coordination.md](coordination.md#execution-scope).
- Pass `budgetCapUsd` only when the user asked for a cap.

## Directory

`directory {}` returns `roles[]`: `name`, `description`, `delivery` (`mode`: request-reply,
compete, broadcast; `count`; `distinctVendors`), `requiredCapabilities`, `agents[]` (`agentId`,
`machineId`, `displayName`, `location`, `kind`, `vendor`, `presence`, `slotsFree`,
`ownerSubjectId`), `agentsOnline`, `freeSlots`, `queueDepth`. Keep a map
`machineId -> displayName/location` and `agentId -> kind@machine` for the report.

- `agentsOnline: 0` with agents listed: the task waits until one connects. Say so in the plan.
- No agents at all: a send dead-letters at once. Don't send; tell the user.
- The directory is the role's whole capacity and is not filtered by project scope. For an
  `owner-runners` project, don't describe every online agent as eligible; `send`'s
  `availableAgents` and waiting reasons decide.

## Verified send and provenance

After [selecting an approved environment](workflow-steps.md#approved-frontend-verification),
include the required checks and the pinned commit in the local `send` request:

```json
{
  "verification": { "environment": "frontend", "requiredChecks": ["test", "typecheck", "build"] },
  "refs": [
    {
      "kind": "github.commit",
      "repo": "example-org/example-web",
      "sha": "1234567890abcdef1234567890abcdef12345678",
      "url": "https://github.com/example-org/example-web/commit/1234567890abcdef1234567890abcdef12345678"
    }
  ]
}
```

The normal task arguments still apply. The local MCP tool routes this request through the service's
verified send. The ref tells the service which commit to resolve. The service must independently
authorize the repository and commit through a trusted source before it can pin the execution target.
Labels, encrypted task text and bundle metadata carry no such authorization. During rollout,
`requiredTaskRequestsSupported` must stay false until the service can resolve and authorize
the pinned target. The remaining integration point is the hosted verified-send resolver's accepted
target input and its validation of the remote, input SHA and base SHA. Treat an unsupported or
rejected target as a verification blocker. The orchestrator does not publish commits to make
them resolvable.

## Task body

Every task message:

```json
send {
  "to": "role:backend-developer",
  "type": "task.implement",
  "title": "API and API tests: due dates",
  "projectId": "prj_…",
  "expect": "reply",
  "dedupeKey": "file:samples/work-items/add-due-dates.md/T2/1",
  "labels": { "task": "T2", "round": "1", "role": "backend-developer" },
  "refs": [ { "kind": "file", "path": "samples/work-items/add-due-dates.md" },
            { "kind": "git.branch", "repo": "messagebus-todo-app", "id": "add-due-dates-api" } ],
  "body": { "summary": "<one line: the goal>", "details": "<markdown, below>", "data": { … } },
  "attach": [ { "format": "git-bundle", "path": "<repo>", "branch": "add-due-dates-api" } ]
}
```

`details` (Markdown) always has these sections, filled from the work item, never invented:

1. **Goal** and **Behaviour**: the work item's own words for this task.
2. **Repository**: the attached bundle, the branch to work on, the commit it starts from
   (`git rev-parse HEAD`), and the paths this task may change.
3. **Acceptance checks**: the exact commands to run and what must pass.
4. **Out of scope**: the work item's list, plus "anything outside the paths above".
5. **Return**: "If something got in the way (missing context, code that didn't match the brief, a build or test problem, a refusal, a merge conflict), add it to settle's `friction`: one kind and one sentence per note. Commit on branch `<branch>`. Run the checks. Settle with a one-paragraph summary,
   the check results, and refs to the branch and head commit. Attach your commits as a git bundle
   of `<branch>` with `base` `<start commit>`."

`data` carries the same facts for machines: `{workItem, task, branch, baseCommit, checks: [...],
returns: "git-bundle"}`.
For verification-only tasks, use the return instructions below and omit `returns: "git-bundle"`.

- `type`: `task.plan` (to the architect: reply with the approach, the tasks in order and which can
  run in parallel, and the risks; no code), `task.implement`, `task.test`, `task.docs`,
  `task.build`, `task.review.design`, `task.review.code`, `task.fix`.
- `labels.role`: the role responsible for this work, matching the role target. Preserve it on
  fixes and machine checks. A direct `task.*` send returns a `notice` when the labelled role
  requires capabilities the target lacks; it still sends. No role label means no role notice.
  Follow-ups to an agent that held the referenced task suppress this notice when that task's
  role is absent or matches the labelled role.
- `dedupeKey`: `<project key>/<task>/<round>`. A resumed session that sends the same task again
  gets the earlier thread back (`deduplicated: true`) instead of a second task. `retry` moves it to
  the next round for you.
- Don't attach a bundle whose history holds secrets or large build output. Bundles carry
  committed history only; uncommitted files never travel.
- Leave `base` out of a task's bundle: mbus starts it after the commit it last sent that role for
  the repository (the whole history the first time). A runner without the earlier commits asks for
  them, and this session's mbus sends them by itself; the task's progress reads "Waiting for the
  code from the sender" meanwhile. If this session closes first, the task fails after 30 minutes
  saying so: `retry` it from an open session.

## Remote verification tasks

- Use `directory` and `describe_role` to find a remote tester within the project's execution
  scope. Confirm the runner has the required OS, toolchain, dependencies and access, and that
  its role profile and sandbox allow the commands. Check any required host or release gate.
  Use a role send when the eligible pool meets the check's requirements. If the check needs a
  specific runner, use the direct machine-check exception with an eligible agent that holds the
  role, and keep `labels.role` set to `tester` (or the role the workflow requires). A role label
  alone does not enforce eligibility.
- For a verification-only `task.test`, record `git rev-parse HEAD` on the integrated branch.
  Put that full SHA, the environment requirements and the exact commands and working directories
  in the task body. Attach that branch as a Git bundle with `expectedHead` set to the same SHA.
  Ask the tester to verify the checkout before running commands and return evidence without
  editing or committing code. Keep any required approved-environment fields on the send.
- Ask for the tested SHA, actual OS and toolchain versions, each command and its exit status,
  and pass/fail/skip counts or relevant log output. Label smoke checks and full suites clearly.
  Ask what cleanup was verified and for the path and reason for each artifact that must remain.
  Ask for the reason and any failure output for each blocked or failed check. The coordinator
  matches each result to the expected SHA and environment and verifies that the commands and
  results cover the required checks. A smoke check provides evidence only for its own scope.

## Parallel tasks

```json
send_many { "projectId": "prj_…", "messages": [ { …task A… }, { …task B… } ] }
```

Returns `results[]` in order (`threadId`, `status`, `availableAgents`). Keep `threadId -> task`.
Add `"join": {"mode": "all"}` only when you want one combined result thread; usually wait on the
thread ids instead.

## Reviews

Every design or code review goes out with `excludeAuthors: true`:

```json
send { "to": "role:code-reviewer", "type": "task.review.code", "projectId": "prj_…", "expect": "reply",
       "excludeAuthors": true, … }
```

The service then keeps every agent that finished a non-review task of the project (or of
`parentThreadId`) from getting a copy; add `excludeAgents: ["agt_…"]` for anyone else who must not
review it. The result lists `excludedAgents`.
Route reviews to the reviewer role too; vendor preferences belong in its settings, not an
`agent:` workaround. A task of type `task.review` or `task.review.*` never counts as authorship,
however it was addressed.
Send work that also changes code under another type, so its agent counts as an author.

- `notice` set (for example "Sent 1 copy instead of 2"): fewer independent reviewers hold the role
  than it asks for. Go on with the one you have and tell the user.
- Error `not-found` saying every holder is excluded: no independent agent can review it. Don't
  resend without the exclusion on your own; tell the user (they can add an agent to the role, or
  agree to a review by the author) and record the step as not passed until they decide.

## Waiting

```json
wait { "threadIds": ["thr_A", "thr_B"], "mode": "any" }
```

Pass the threads you sent that you haven't handled yet. Don't wait on `projectId` alone: it only
covers threads still open when you call, so a thread that finished while you were busy (merging,
building) would never be returned.

Each call returns within the client's time limit: the default `timeoutSeconds` is 100, and the
local server returns by 110 s under Claude Code (which ends tool calls at about 120 s) and by 55 s
under Codex (60 s), whatever you ask. `timedOut: true` only means "nothing yet": call again at once.
Don't move waits to a background shell.

Returns `finished[]` (threads with `status`, `agentId`, `machineId`, `attempts`, `result`
{`status`, `summary`, `refs`, `usage` {`agentKind`, `vendor`, `model`, `tokensIn`, `tokensOut`,
`estimatedCostUsd`, `activeSeconds`}, `error`}, `lastError`), `pending[]`, `timedOut`, and
`replies[]` (each `{envelope, body}`; `envelope.threadId`, `envelope.attachments[]` with `id`,
`format`, `branch`; `body` is already decrypted). `finished` lists every finished thread among
those you passed: handle each once and drop it from the next call's `threadIds`. `replies` come
only with finished threads.

A task sent to several agents (a broadcast role: design-reviewer and code-reviewer send one copy to
each of two vendors) finishes once every copy has ended. Its `copies[]` has one entry per agent
(`agentId`, `machineId`, `status`, `attempts`, the result's `summary` or `error`, `agentKind`,
`model`, `lastError`), and its `result.summary` starts "N of M copies succeeded.". Read `copies`
for each reviewer's outcome and report a failed copy; don't assume `replies` holds one answer per
agent: a copy can settle without posting one. `status {threadIds: [<that thread>]}` shows the same
`copies` later.

Thread statuses: `replied` / `settled` (done; check `result.status`), `failed`, `dead-lettered`
(no agent could take it, or it ran out of attempts), `cancelled`.

For live progress between results, `watch {projectId, after: <cursor>, timeoutSeconds: 60}`
returns at most 50 events (each thread's latest progress only), a new cursor, and `more: true`
when there are more to read. `status {projectId}` returns the project totals and `tasks`: one
short line per thread (status, agent, machine, summary or latest progress, usage, `lastError`),
newest first, 50 at most. `status {threadIds: [...]}` returns those threads in full.

Decisions on approvals don't come through `wait`: use `wait_approval` (below).

## Late answers

```json
open_inbox {}
```

Call it once before the first send. It returns `{inboxId, replyTo, expiresAt, resumable,
sessionId?, notice?}`, and from then on the local server sends every task with `replyTo` set to
this session's inbox (don't set `replyTo` yourself). Keep using `wait` while this session is open;
answers you have read that way never wake it again. An answer that arrives after this session has
closed resumes this same session on this machine, in this folder, through `mbus runner` (when this
machine runs one): it can read files and use the mbus tools, not edit or run commands. It starts
with a short note naming the message; read it with `inbox`, then carry on the plan. With
`resumable: false`, tell the user what `notice` says: such an answer starts a new session instead.
Calling it again in the same session returns the same inbox.

## Approvals

Only for a step a person must decide: a risky one (deleting data, a migration, anything hard to
undo), a design sign-off the work item or workflow asks for, or an approval a worker asks for.

```json
request_approval { "title": "Run the migration that drops the archived_tasks table",
                   "detail": "Deletes 1,200 archived rows the app no longer reads. If rejected, the table stays and T4 is skipped.",
                   "projectId": "prj_…", "threadId": "thr_<the task it is about>" }
```

- `title` says exactly what will happen; `detail` why, and what you will do if it is rejected. The
  approver reads it in the dashboard, unencrypted: no secrets (it is refused if it looks like one).
- `approver` defaults to the person who started the project (the user). Pass `member:<email>` or
  `role:<name>` only when the work item or workflow names someone else. `highRisk: true` makes the
  approver sign in again first.
- Returns `{approvalId, status: "pending", approvers, expiresAt}`. Tell the user in one line that it
  waits for them in the dashboard (Notifications), then loop:

```json
wait_approval { "approvalId": "apr_…" }
```

It returns within the client's time limit, like `wait`; `timedOut: true` with `status: "pending"`
means call again. While waiting, keep collecting results of tasks that don't depend on it.
`approved`: do exactly what was asked, nothing more. `rejected` (read `comment`) or `expired`: don't;
carry on without it if the plan allows, else stop that line of work and tell the user. Record the
decision and who made it (`decidedBy`) in the report.

## Choose the bundle history and branch

Omit `base` to let mbus send commits since the last send to that target. Set `base: ""`
to send the whole history. An explicit commit sends commits after that commit.

To send a fetched worker result directly, use `branch: "mbus/<branch>"` or
`branch: "refs/remotes/mbus/<branch>"`; the bundle carries `refs/heads/<branch>`.
If the local branch is behind its fetched mbus copy, send returns a notice; use the
fetched branch or update the local branch before sending the next task.

## Bringing code back

For each attachment with `format: "git-bundle"` on a reply:

```json
fetch_attachment { "threadId": "thr_…", "attachmentId": "att_…", "repository": "<repo>" }
```

It lands at `refs/remotes/mbus/<branch>` and returns `{commit, ref}`. For a coding task with
required verification, compare the returned `commit` to the runner's `verification.revision`.
Only that commit can carry the reported check results. If the commit does not match, stop
integration for that task and report the mismatch.

Then, in the repository:

```
git log --oneline <start>..<ref>          # what came back
git diff --stat <start>..<ref>            # only the task's paths?
git diff <start>..<ref>                   # read it before building it
git switch <work item branch> && git merge --no-ff <ref> -m "Merge <task> from <agent>"
```

If a code task finished without a git-bundle attachment, its code is still on the worker's
machine. Don't redo the task: send one `task.fix` to the same agent asking it to settle again with
the bundle attached, and if that also comes back without one, tell the user the machine and branch.

## Retrying a task

When a task failed, was cancelled or dead-lettered for a reason outside its own work (the agent
wasn't signed in, the machine lacked a tool, a runner restarted), and the cause is fixed:

```json
retry { "threadId": "thr_…", "note": "The Mac now has the .NET SDK; run the checks again.", "repository": "<repo>" }
```

It sends the original task again as a new thread (same role, body, refs and exclusions; the note
goes first in the details; the next dedupe round; its bundles packaged again from their branches in
`repository`, the project's execution scope) and returns it like `send`. Retry keeps the local branch when it exists, with a notice if it is behind the fetched copy. Only when the local branch is missing does it send `refs/remotes/mbus/<branch>`, with a notice explaining that choice. Track the new `threadId` instead of the old one and mark
the checklist line `round 2`. Retrying the same thread twice returns the first retry
(`deduplicated: true`); retrying a thread that is still open fails with `conflict`. Never re-send a
task body by hand with a bumped dedupe key.

## Declines

A task that stays `queued` while its role has agents online may have been declined by the machine
(the runner refused it before starting). On a `timedOut` wait, check the pending threads with
`status {threadIds: [...], compact: true}`: a `lastError` (or, in `watch`, a status event `reason`)
that contains `machine-not-ready` means that machine is missing something the task needs (for
example git, the .NET SDK, Node.js, or a signed-in agent CLI). Tell the user, in one line, which
machine and what to install or fix there; don't try to fix their machine with a task. When they say
it is fixed, `retry` the task (or let it be offered again if it is still queued) and keep waiting.

A waiting reason `owner-runners` on an `owner-runners` project means none of the project owner's
personal runners matches the target. Tell the user in one line to give one of their own machines
the required role or capabilities; don't resend to other machines or widen the scope. Owned holders that are only offline
or busy just keep the task queued.

## Follow-ups and fixes

A follow-up bundle gets a notice when its tip lacks the parent's returned commits: call `fetch_attachment` for the attachment it names and send `mbus/<branch>`.

Send a new task in the same project to the original author. Use the original implementation thread
as `parentThreadId` so the author keeps the role settings, and include the review findings in the task body.
This example fixes back-end work.

```json
send { "to": "agent:agt_…", "type": "task.fix", "parentThreadId": "thr_<original_implementation>",
       "labels": { "role": "backend-developer" },
       "title": "Fix: <finding>", "body": { … what to change, why, the checks … },
       "attach": [ { "format": "git-bundle", "path": "<repo>", "branch": "<work item branch>" } ] }
```

## Finishing

```json
complete_project { "projectId": "prj_…", "status": "completed",
                   "summary": "<what was done, which checks passed, where the result is>",
                   "refs": [ { "kind": "git.branch", "repo": "<repo name>", "id": "<work item branch>" } ],
                   "usage": { "model": "<your model>", "tokensIn": 0, "tokensOut": 0, "activeSeconds": 0 } }
```

`usage` is this orchestrating session's own tokens and time. Pass it only with numbers your client
showed you; never estimate them. Leave it out otherwise: under Claude Code the local server reads
them from this session's log. The project shows it as its own line ("Orchestrator session") and
adds it to the tokens and cost, not to the task count.

Returns the project with final `totals` (`tasks`, `tasksDone`, `tasksFailed`, `tasksCancelled`
(tasks you or someone cancelled; not failures), `wallSeconds`, `agentSeconds`, `tokensIn`, `tokensOut`, `estimatedCostUsd`, `tasksCostUnknown`, `agents`,
`machines`, `models`). Use `status: "failed"` when a required step or check didn't pass.

## Errors

A failed call returns `{code, message, retryable, retryAfterSeconds?, field?}`.

| Code | Do this |
|---|---|
| `invalid-input` | Fix `field` as `message` says and call once more. A secret finding: take the secret out. |
| `not-found` | The role, thread or project doesn't exist. Re-read `directory` or `status`; don't guess ids. |
| `forbidden` | Stop that action and tell the user (missing permission, or this machine can't decrypt a key it holds no grant for). |
| `conflict` | It changed underneath you: `status` the thread, then decide. From `retry`: the thread is still open; wait for it or cancel it first. |
| `too-large` | Move the long content into a file attachment and send a shorter body. |
| `limit-reached` | Budget cap: stop sending, let running tasks finish, ask the user to raise the cap in the dashboard. Fan-out or rate: wait `retryAfterSeconds`, retry up to 3 times. |
| `unavailable` | Wait `retryAfterSeconds` (or 5, 15, 45 s) and retry; after 3 tries stop and tell the user. |
| `internal` | Retry once; then stop and report the message. |

Never loop on an error. Stop gracefully: say what finished, what is still running (the user can
see it in the dashboard), and what you would do next.

A task sent under the wrong project is moved with `move {threadId, projectId}` instead of resent; use `projectId: null` for no project and `dryRun: true` to preview.

A send without `projectId` uses the project this MCP session last ensured (or its assigned runner task’s project). With neither, it sends without a project and returns a notice. Other sessions on the same machine do not supply a default. This also applies to `send_many`; an item’s `projectId` takes precedence over the batch’s.
