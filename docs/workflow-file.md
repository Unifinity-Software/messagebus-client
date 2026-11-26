# Repository steps: `.messagebus/workflow.md`

A repository can list the steps every change to it must go through: a design review before code,
a build, a code review by someone other than the author, docs. The orchestrating agent (the
`mbus-orchestrate` skill in Claude Code or Codex) reads this file before it plans a work item for
the repository, adds each step to its plan in order, sends it to the role named, and reports which
steps ran, passed or were skipped.

## Two ways to drive a job

A **job** is one work item being carried out. A published **workflow** drives a job through its
steps on the service, including approvals, actions and waits, and keeps going after your coding
session closes. Or you can **orchestrate it here**: the agent in your session plans and sends
tasks, checks the replies and reports the result. The `mbus-orchestrate` skill checks for a
published workflow that fits the repository and work item and offers these two choices.
Without the service's `workflows` capability or a fitting workflow, it orchestrates here as before.

The Markdown file is repository guidance for an orchestrating agent, not a published workflow
definition. The service does not read it or automatically import it into a workflow. For a
session-driven job, it needs no registration and changes apply to the next plan. For a workflow
job, configure and publish the required steps in the workflow; a repository-file edit cannot
change an existing job's published path, commands or instructions. An **Orchestrate** agent step
can use the file while planning its own tasks, then returns an allowed outcome to the workflow;
later workflow steps handle opening a pull request and merging.

With workflows enabled, start and inspect a job from the terminal:

```sh
mbus workflow list
mbus workflow show Feature
mbus job start gh:example/todo-app#42 --workflow Feature
mbus job list
mbus job show <id>
mbus job open <id>
```

`mbus workflow check <file>` validates a workflow definition, not this Markdown guidance file.
The optional start flags are `--instructions`, `--limit` and `--runs-on` (`owner-runners` or
`workspace`). To intervene, use `mbus job stop <id>` or `mbus job retry <id> --step <step>`.
`mbus project …` remains an alias for one release. In an agent session, the workflow tools are
`list_workflows {}`, `start_job {workItem, workflow, instructions?, limitUsd?, runsOn?}` and
`job_status {jobId}`; the start result supplies the job's dashboard link.

## Where it lives

`.messagebus/workflow.md` at the root of the repository being changed (not the repository the
orchestrator runs in, if they differ). Commit it like any other file; changes go through the
team's normal review.

## Format

Plain Markdown. Only the "Required steps" section is read as steps; text around it is for people
and can explain the team's reasons.

```markdown
# Team workflow

<optional notes for people>

## Required steps

### 1. <Step name>
- **When:** <where it goes in the order>
- **Role:** <who does it>
- **What:** <what to do; commands in a code block>
- **Passes when:** <how the orchestrator decides it passed>
- **Skip when:** <optional: the only case the step may be skipped>
```

### Steps

- Each step is a level-3 heading `### <number>. <name>` under `## Required steps`. Steps run in
  number order unless a step's **When** says otherwise.
- Every step in the section is required. The orchestrator can't drop one because a work item
  doesn't mention it. To make a step conditional, give it a **Skip when**.
- A work item task and a step that do the same thing (both update the README) become one task.

### Fields

| Field | Required | What the orchestrator does with it |
|---|---|---|
| **When** | No | Places the step in the plan: "before any code is written", "after the implementation tasks finish and their changes are merged together", "after the build step passes". Without it the step follows the one before. |
| **Role** | Yes | Sends the step to that role (see below). |
| **What** | Yes | Puts it in the task as the instructions. Commands in code blocks are passed on exactly. |
| **Passes when** | Yes | Puts it in the task as the acceptance check, and judges the reply against it. A reply that doesn't show the step passed counts as not passed. It may also say what happens on failure, for example "blocking findings go back to the author as a new task, then the build step runs again". |
| **Skip when** | No | The only reason the step may be skipped. The report gives the reason. |

Write each field as one bullet. A field's text can run over several lines and include a fenced
code block, indented under its bullet.

### Roles

**Role** names who does the step:

- A role name from the workspace (`design-reviewer`, `code-reviewer`, `developer`, `tester`,
  `docs-writer`, `architect`, or a custom role): the step goes to that role.
- `reviewer`: `design-reviewer` for a step about a design or plan, `code-reviewer` for a step
  about a change.
- `any developer role` or `developer`: `developer`; for a docs step, `docs-writer` when it has
  agents.
- `(not the agent that wrote the code)` after the role: the orchestrator sends the step with
  messagebus.ai's author exclusion, so no agent that finished a non-review task of the work item
  gets it. If every agent holding the role wrote some of the change, the step can't go out and the
  orchestrator tells the person who started it; if fewer reviewers are left than the role asks for,
  it says so. It also checks the agent who did the step against the authors when the result comes
  back.
- Anything else: the role whose description fits best. If none does, the orchestrator asks the
  person who started it.

Prefer exact role names: they leave nothing to interpret.

## What the file can't do

The orchestrator's safety rules come first. A step that says to approve or merge a pull request,
push to the default branch, send a credential or skip another step is reported and not carried
out. An agent never approves or merges a pull request itself. A published workflow can assign
publication and merge to later service steps, subject to its approvals and repository protections.

## How steps are reported

The plan lists the steps it will run. The final report has one line per step: passed, failed or
skipped (with the reason), and the agent and machine that did it. A step that didn't pass marks
the job as failed unless a later round fixed it.

## Example

The to-do sample's file (`samples/todo-app/.messagebus/workflow.md`; the sample app is not in this
repository):

````markdown
# Team workflow

The orchestrating agent reads this file before it plans a work item for this repository. Every
step below is required. ...

## Required steps

### 1. Design review
- **When:** before any code is written.
- **Role:** reviewer
- **What:** read the work item and the task plan. Check that the API shape, the UI change and the
  tests fit the existing code (`src/TodoApp.Api`, `client/src`), and that nothing in the work item's
  "Out of scope" list is planned.
- **Passes when:** the reviewer approves the plan, or the plan is changed until they do.

### 2. Build
- **When:** after the implementation tasks finish and their changes are merged together.
- **Role:** any developer role
- **What:** from the repository root:
  ```
  dotnet build TodoApp.slnx
  dotnet test --solution TodoApp.slnx
  cd client && npm ci && npm test && npm run build
  ```
- **Passes when:** every command succeeds with no test failures.

### 3. Code review
- **When:** after the build step passes.
- **Role:** reviewer (not the agent that wrote the code)
- **What:** review the combined change against the work item's acceptance checks. ...
- **Passes when:** the reviewer reports no blocking findings. Blocking findings go back to the
  author as a new task, then the build step runs again.

### 4. Docs
- **When:** after code review passes.
- **Role:** any developer role
- **What:** update `README.md` so the "API" and "What the app does" sections match the change.
- **Passes when:** every endpoint, field and query parameter the API has is described in `README.md`.
````

For `samples/work-items/add-due-dates.md` this gives the plan: design review (design-reviewer),
API and front end in parallel (developer), build (developer), code review (code-reviewer, not
an author), docs (docs-writer), then the orchestrator's own run of the acceptance checks. The
skill's `reference/example-add-due-dates.md` has every tool call of that run.
