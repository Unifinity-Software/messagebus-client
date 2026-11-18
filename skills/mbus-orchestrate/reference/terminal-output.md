# What the user sees

Short plain lines, in this order. Numbers come from tool results only (`wait`, `status`,
`complete_project`); never estimate them yourself. Name machines and agents the way `directory`
does (`kind@machine`, plus the location when it has one).

## The rule: quiet unless it matters

The user wants outcomes, not a running story. Make tool calls without announcing them. Never relay
what a worker said it did step by step, never quote progress lines, and never reprint the checklist
just because a task changed state. Write only:

- the plan, once, after the directory (below);
- one line when a group of tasks finishes or the plan changes;
- a problem, the moment it needs the user (below);
- the final report.

A failure you can handle yourself (a retry, a fix task) gets no message of its own; mention it in
the next one-line note. Asked "where are we?", print the checklist (below) once.

## After the directory: the plan, as a checklist

```
Plan for <work item title>, 6 tasks:
  [ ] T1 Design review of the plan        design-reviewer     (workflow step 1)
  [ ] T2 API and API tests                backend-developer
  [ ] T3 Front end and front-end tests    frontend-developer  (parallel with T2)
  ...
Workflow (.messagebus/workflow.md): design review, build, code review, docs. All will run.
Agents online: backend-developer 1 (codex@pc), frontend-developer 1 (claude-code@mac-mini), design-reviewer 1, code-reviewer 1.
```

Say what can't run and why (a role with no agents, a step you'll skip). Then dispatch without
waiting for a reply, unless the user asked to approve the plan first.

## While it runs

One line when something changes the picture, with the clock time and the count done:

```
10:34 · API and front end done and merged (2 of 6); build and tests next.
10:51 · Code review found 1 blocking issue; sent the fix back to the same agent.
```

When you need the user, say so on its own line, starting with what they must do, then where and
why:

```
You need to act: install the .NET SDK on mac-mini. The build task can't run there without it;
I'll retry it once you say it's done.
```

When the user asks for status, print the checklist, one line per planned task:

```
10:20 · 2 of 6 done
  [x] T1 Design review of the plan        design-reviewer     codex@mac-mini        done 3 min
  [x] T2 API and API tests                backend-developer   codex@pc              done 14 min · merged
  [>] T3 Front end and front-end tests    frontend-developer  claude-code@mac-mini  running 9 min
  [!] T4 Build and test                   developer           claude-code@mac-mini  needs you: no .NET SDK on the Mac
  [ ] T5 Code review                      code-reviewer                              waiting for T4
  [ ] T6 README                           docs-writer                                waiting for T5
```

States: `[ ]` not sent or queued, `[>]` running, `[x]` done, `[!]` failed or needs the user,
`[-]` skipped or cancelled (say why). A retried task keeps its line and shows the round
(`round 2`).

## At the end

```
Add due dates to to-dos: all work is done. The result is on branch add-due-dates in <repo>;
review it and merge it yourself (agents don't merge).

  Task                             Role                Agent / machine          Time    Tokens   Est. cost
  1 Design review                  design-reviewer     claude-code@mac-mini     3 min   12.1k    $0.09
  2 API and API tests              backend-developer   codex@pc                 14 min  41.2k    $0.31
  ...
  Orchestrator (this session)      -                   claude-code@pc           40 min  610k     $1.20

Workflow steps: design review passed · build passed · code review passed (1 finding fixed) · docs passed
Checks run here: dotnet build, dotnet test (24 passed), npm test (9 passed), npm run build.
Wall time 52 min · agent time 1 h 10 min · 186k tokens · est. cost $1.42 · 2 machines, 2 models.
Project: <projectId> (dashboard, Projects page).
```

- Time per task: `result.usage.activeSeconds`. Tokens: `tokensIn + tokensOut`. Cost:
  `estimatedCostUsd`, or "unknown" when absent. Machine: `machineId` mapped through `directory`.
- Totals: the `totals` from `complete_project` (`wallSeconds`, `agentSeconds`, tokens,
  `estimatedCostUsd`, `tasksCostUnknown`, `machines`, `models`). They include this session's own
  line when `complete_project` had a `usage`; if it had none, say the orchestrator isn't counted.
- If the dashboard address is known (the `MBUS_SERVER` environment variable), link
  `<server>/projects?project=<projectId>`.
- Read each batch's decrypted answers' `body.friction` and each copy's `result.frictionKinds`. Include a short friction line in the report with counts by kind and role (project `totals.friction` and `totals.frictionByRole`), and summarize the notes with task links when they explain a problem. Omitted friction means no report, not proof nothing was hard. Never print secrets from a note.
- A failed or partial run gets the same summary, with what didn't pass first.
