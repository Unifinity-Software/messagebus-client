# Worked example: `orchestrate samples/work-items/add-due-dates.md`

A dry run of the procedure against messagebus.ai's sample work item and the sample repository's
`.messagebus/workflow.md`, in the order the calls are made. Ids (`prj_…`, `thr_…`) are made up;
everything else follows the sample files. Use it as the pattern for any work item. The sample to-do app and its
work items are not shipped with mbus or in the client repository.

Setup assumed: the repository was made with `samples/todo-app/init-repo.ps1`, so it is at
`$env:TEMP\messagebus-todo-app` (Windows) on `main` at tag `start`, clean.

## 0. Read the work item and the repository

- Read `samples/work-items/add-due-dates.md`: three tasks (API, front end, docs), API and front end
  in parallel, acceptance checks `dotnet build TodoApp.slnx`, `dotnet test --solution TodoApp.slnx`,
  `cd client && npm ci && npm test && npm run build`.
- `git -C $REPO status --porcelain` is empty; `START=$(git -C $REPO rev-parse HEAD)`.
- Read `$REPO/.messagebus/workflow.md`: design review (reviewer) → build (developer) → code review
  (reviewer, not the author) → docs (developer).
- In this example the workflow allows remote acceptance checks and has no approved frontend
  environment requirement. The remote tester runs Linux, with the required .NET SDK, Node.js and
  npm versions, package feed access, and permission to run builds and tests.
- `git -C $REPO switch -c add-due-dates`, then `git -C $REPO branch add-due-dates-api` and
  `git -C $REPO branch add-due-dates-client` (all at `START`).

## 1. Project and directory

```json
find_project { "key": "file:samples/work-items/add-due-dates.md" }
→ {}   (new project; the user said "use my own runners")

ensure_project { "key": "file:samples/work-items/add-due-dates.md", "title": "Add due dates to to-dos",
                 "executionScope": "owner-runners",
                 "refs": [ { "kind": "file", "path": "samples/work-items/add-due-dates.md" } ] }
→ { "project": { "projectId": "prj_01", "status": "active", "executionScope": "owner-runners",
                 "executionOwnerSubjectId": "sub_me", ... }, "created": true }
   (scope and owner match the choice, so dispatch may go ahead; had the user not said, ask
   "My runners only" or "Any eligible workspace runner" once before ensure_project)

directory {}
→ roles: design-reviewer (broadcast 2, distinct vendors), developer (compete),
  frontend-developer (compete), backend-developer (compete), code-reviewer (broadcast 2), tester (compete),
  docs-writer (compete), ... each with agents such as claude-code@mac-mini (Mac mini) and codex@pc
  (each agent's ownerSubjectId shows whose it is; only sub_me's personal machines can run prj_01)

describe_role { "role": "developer" }
  (and frontend-developer, backend-developer, design-reviewer, code-reviewer, docs-writer, tester)
```

Plan shown to the user as the task checklist (mapping: workflow "reviewer" → design-reviewer for
step 1 and code-reviewer for step 3; the docs step and work item task 3 are one task, run where the
workflow puts it). After this the user hears only a one-line note as each group of tasks
finishes, anything that needs them, and the final report:

```
Plan for "Add due dates to to-dos", 7 tasks:
  [ ] T1 Design review of the plan                    design-reviewer     workflow step 1
  [ ] T2 API and API tests                            backend-developer   work item task 1
  [ ] T3 Front end and front-end tests                frontend-developer  work item task 2, parallel with T2
  [ ] T4 Build and test the merged change             developer           workflow step 2
  [ ] T5 Code review of the merged change             code-reviewer       workflow step 3, not an author
  [ ] T6 README: dueDate, clearDueDate, isOverdue     docs-writer         work item task 3, workflow step 4
  [ ] T7 Verify the final integrated commit          tester              work item acceptance checks
```

Each step below is introduced to the user with one sentence first, for example "Sending the plan
to two reviewers before any code is written."

## 2. Design review (workflow step 1)

```json
send { "to": "role:design-reviewer", "type": "task.review.design", "projectId": "prj_01", "expect": "reply",
       "excludeAuthors": true,
       "title": "Design review: due dates plan", "dedupeKey": "file:samples/work-items/add-due-dates.md/T1/1",
       "labels": { "task": "T1", "round": "1", "role": "design-reviewer" },
       "body": { "summary": "Review the plan for adding due dates to the to-do sample before any code is written.",
                 "details": "## Goal\n<work item Goal and Behaviour>\n## Plan\n<T2..T6 with paths>\n## Check\nThe API shape, UI change and tests fit src/TodoApp.Api and client/src; nothing from Out of scope is planned.\n## Return\nA verdict (ready, ready with changes, not ready) and each concern with evidence.",
                 "data": { "workItem": "file:samples/work-items/add-due-dates.md", "task": "T1", "branch": "add-due-dates", "baseCommit": "<START>" } },
       "attach": [ { "format": "git-bundle", "path": "<REPO>", "branch": "add-due-dates" } ] }
→ { "threadId": "thr_T1", "status": "queued", "availableAgents": 2 }

wait { "threadIds": ["thr_T1"], "mode": "any" }   (returns within about 100 s; repeat on timedOut)
→ finished: [ thr_T1 replied, copies: [ one per reviewer ], result.usage {...} ], replies: [ the verdicts posted ]
```

Both reviewers' verdicts (from `copies` and `replies`) are "ready" or "ready with changes": fold the changes into T2–T7. "Not ready": revise
the plan and send T1 again (round 2, new dedupe key); a second "not ready" goes to the user.

## 3. API and front end in parallel

```json
send_many { "projectId": "prj_01", "messages": [
  { "to": "role:backend-developer", "type": "task.implement", "expect": "reply", "title": "API and API tests: due dates",
    "dedupeKey": "file:samples/work-items/add-due-dates.md/T2/1", "labels": { "task": "T2", "round": "1", "role": "backend-developer" },
    "body": { "summary": "Add an optional dueDate and a read-only isOverdue to the to-do API, with tests.",
              "details": "## Goal / Behaviour\n<work item text>\n## Repository\nBundle branch add-due-dates-api from <START>. Change only src/TodoApp.Api and tests/TodoApp.Api.Tests.\n## Tests to add\n<work item task 1 bullets>\n## Acceptance checks\ndotnet build TodoApp.slnx\ndotnet test --solution TodoApp.slnx\n## Out of scope\n<work item list>; anything outside those paths.\n## Return\nCommit on add-due-dates-api, run the checks, settle with the results and attach a git bundle of add-due-dates-api with base <START>.",
              "data": { "task": "T2", "branch": "add-due-dates-api", "baseCommit": "<START>", "returns": "git-bundle",
                        "checks": ["dotnet build TodoApp.slnx", "dotnet test --solution TodoApp.slnx"] } },
    "attach": [ { "format": "git-bundle", "path": "<REPO>", "branch": "add-due-dates-api" } ] },
  { "to": "role:frontend-developer", "type": "task.implement", "expect": "reply", "title": "Front end and tests: due dates",
    "dedupeKey": "file:samples/work-items/add-due-dates.md/T3/1", "labels": { "task": "T3", "round": "1", "role": "frontend-developer" },
    "body": { "summary": "Show and set due dates in the to-do front end, with tests.",
              "details": "<same sections; paths client/src; checks cd client && npm ci && npm test && npm run build; branch add-due-dates-client>",
              "data": { "task": "T3", "branch": "add-due-dates-client", "baseCommit": "<START>", "returns": "git-bundle",
                        "checks": ["cd client && npm ci && npm test && npm run build"] } },
    "attach": [ { "format": "git-bundle", "path": "<REPO>", "branch": "add-due-dates-client" } ] } ] }
→ results: [ { "threadId": "thr_T2" }, { "threadId": "thr_T3" } ]

wait { "threadIds": ["thr_T2", "thr_T3"], "mode": "any" }   (repeat on timedOut)
→ finished: [ thr_T2 replied, agentId agt_mac, machineId mch_mac, result.usage {...} ]
  replies: [ { envelope: { threadId: "thr_T2", attachments: [ { id: "att_T2", format: "git-bundle", branch: "add-due-dates-api" } ] }, body: {...} } ]

fetch_attachment { "threadId": "thr_T2", "attachmentId": "att_T2", "repository": "<REPO>" }
→ { "commit": "…", "ref": "refs/remotes/mbus/add-due-dates-api" }
```

Review: `git diff --stat <START>..refs/remotes/mbus/add-due-dates-api` touches only
`src/TodoApp.Api` and `tests/TodoApp.Api.Tests`; read the diff; then
`git switch add-due-dates && git merge --no-ff refs/remotes/mbus/add-due-dates-api`.
Then `wait {threadIds: ["thr_T3"]}` and the same for T3. Returned work that fails its checks gets
one `task.fix` to the same `agent:<agentId>` with the failure and the checks. A task that failed
before doing any work (for example `result.error` says the agent CLI isn't signed in) is a problem
on that machine: tell the user what to fix there, and once they have, `retry {"threadId": "thr_T3",
"note": "Claude Code is signed in now.", "repository": "<REPO>"}` and wait on the new thread id.

## 4. Build (workflow step 2)

```json
send { "to": "role:developer", "type": "task.build", "expect": "reply", "projectId": "prj_01",
       "title": "Build and test: due dates", "dedupeKey": "file:samples/work-items/add-due-dates.md/T4/1",
       "labels": { "task": "T4", "round": "1", "role": "developer" },
       "body": { "summary": "Run the team's build step on the merged due-dates change and report the results.",
                 "details": "Branch add-due-dates. From the repository root run:\ndotnet build TodoApp.slnx\ndotnet test --solution TodoApp.slnx\ncd client && npm ci && npm test && npm run build\nPasses when every command succeeds with no test failures. Don't change files; report each command's result." },
       "attach": [ { "format": "git-bundle", "path": "<REPO>", "branch": "add-due-dates" } ] }
→ thr_T4; wait; reply lists each command's result
```

## 5. Code review (workflow step 3)

```json
send { "to": "role:code-reviewer", "type": "task.review.code", "expect": "reply", "projectId": "prj_01",
       "excludeAuthors": true,
       "title": "Code review: due dates", "dedupeKey": "file:samples/work-items/add-due-dates.md/T5/1",
       "labels": { "task": "T5", "round": "1", "role": "code-reviewer" },
       "body": { "summary": "Review the combined due-dates change against the work item's acceptance checks.",
                 "details": "Review git diff <START>..add-due-dates. Look for bugs, missing validation, tests that would still pass if the code were wrong, and changes outside the work item's scope. Blocking findings first, each with file, line, why and a fix." },
       "attach": [ { "format": "git-bundle", "path": "<REPO>", "branch": "add-due-dates" } ] }
→ thr_T5, excludedAgents: [ agt_mac_claude ] (wrote T2 and T3), notice: "Sent 1 copy instead of 2: …"
  wait; copies: one per reviewer with its outcome; replies: the verdicts that were posted
```

`excludeAuthors` kept T2's and T3's author out; tell the user when `notice` says fewer reviewers
got it. Still check each reviewer's `agentId` isn't an author's. A blocking finding becomes
`send {to: "agent:<author>", type: "task.fix", parentThreadId: "thr_T2", labels: {role: "backend-developer"}, ...}`; after it merges, T4
(build) runs again, then T5 again (round 2).

## 6. Docs (work item task 3, workflow step 4)

```json
send { "to": "role:docs-writer", "type": "task.docs", "expect": "reply", "projectId": "prj_01",
       "title": "README: due dates", "dedupeKey": "file:samples/work-items/add-due-dates.md/T6/1",
       "labels": { "task": "T6", "round": "1", "role": "docs-writer" },
       "body": { "summary": "Describe dueDate, clearDueDate and isOverdue in README.md.",
                 "details": "Branch add-due-dates-docs (from add-due-dates). Change only README.md: the API section gets dueDate, clearDueDate and isOverdue; What the app does gets the due-date behaviour. Passes when every endpoint, field and query parameter the API has is described. Return a git bundle of add-due-dates-docs with base <add-due-dates head>.",
                 "data": { "task": "T6", "branch": "add-due-dates-docs", "baseCommit": "<add-due-dates head>", "returns": "git-bundle" } },
       "attach": [ { "format": "git-bundle", "path": "<REPO>", "branch": "add-due-dates-docs" } ] }
→ thr_T6; wait; fetch_attachment; review (README.md only); merge into add-due-dates
```

(`git branch add-due-dates-docs add-due-dates` first.)

## 7. Verify the final integrated commit remotely, close, report

The sample's implementation tasks add API and frontend tests, and code review found no additional
coverage gap. `FINAL=$(git -C "$REPO" rev-parse add-due-dates)` records the full SHA after the docs
merge. The required developer build in T4 ran before that merge, so T7 runs the acceptance checks
on `FINAL`.

```json
send { "to": "role:tester", "type": "task.test", "expect": "reply", "projectId": "prj_01",
       "title": "Verify the integrated due-dates commit", "dedupeKey": "file:samples/work-items/add-due-dates.md/T7/1",
       "labels": { "task": "T7", "round": "1", "role": "tester" },
       "body": { "summary": "Run the required build and full test suites on the final commit.",
                 "details": "Confirm HEAD is <FINAL> and the runner matches the planned Linux, .NET SDK, Node.js, npm and access requirements. From the repository root run dotnet build TodoApp.slnx and dotnet test --solution TodoApp.slnx. In client/ run npm ci, npm test and npm run build. Every command must exit 0 and both full suites must pass. Return the tested SHA, OS and toolchain versions, commands, exit statuses and test counts or logs. Report any blockers, verified cleanup, and paths and reasons for artifacts that remain. No code changes." },
       "attach": [ { "format": "git-bundle", "path": "<REPO>", "branch": "add-due-dates", "expectedHead": "<FINAL>" } ] }
→ thr_T7

wait { "threadIds": ["thr_T7"], "mode": "any" }
→ finished: [ thr_T7, result: { status: "succeeded", ... } ]; reply includes the verification evidence
```

The coordinator matches the tester's SHA to `FINAL`, confirms the runner meets the environment
requirements, and checks the reply against all five commands. Every exit status is `0`. The full
.NET suite reports 24 passed and the full client suite reports 9 passed, with no failures or skips.
The tester verifies removal of the task's build outputs, dependency directory and package cache.
If the evidence is missing or the runner cannot execute a check, keep that check unverified or
blocked. If the integrated SHA changes, dispatch a new round for the affected checks on the new SHA.

```json
complete_project { "projectId": "prj_01", "status": "completed",
  "summary": "Due dates added to the API and front end, with tests and README updates. The workflow steps passed, and a remote tester verified the required builds and full suites on <FINAL>.",
  "refs": [ { "kind": "git.branch", "repo": "messagebus-todo-app", "id": "add-due-dates" },
            { "kind": "git.commit", "repo": "messagebus-todo-app", "sha": "<FINAL>" } ] }
→ { "project": { "totals": { "tasks": 7, "tasksDone": 7, "wallSeconds": …, "agentSeconds": …, "tokensIn": …, "tokensOut": …, "estimatedCostUsd": …, "machines": [...] } } }
```

No `usage` here: under Claude Code the local server adds this session's own tokens from its log,
and the totals include them as the "Orchestrator session" line.

Then the final summary from [terminal-output.md](terminal-output.md): one row per task with role,
`kind@machine`, `activeSeconds`, tokens and cost from each thread's `result.usage`, the workflow
steps, the remote checks and tested SHA, the totals, and the branch for the user to review.

## Expected call sequence (no failures)

```
find_project
ensure_project  (executionScope "owner-runners"; verify scope and owner in the result)
directory
describe_role ×7
send            T1 design review        (bundle add-due-dates, no base, excludeAuthors)
wait            [T1]
send_many       T2 API, T3 front end    (bundles add-due-dates-api / -client, no base)
wait            [T2, T3]  → fetch_attachment T2 → merge
wait            [T3]      → fetch_attachment T3 → merge
send            T4 build                (bundle add-due-dates)
wait            [T4]
send            T5 code review          (bundle add-due-dates, excludeAuthors)
wait            [T5]
send            T6 docs                 (bundle add-due-dates-docs)
wait            [T6]      → fetch_attachment T6 → merge
send            T7 remote verification  (bundle add-due-dates, expectedHead FINAL)
wait            [T7]  → check SHA, runner, command results and cleanup
complete_project
```
