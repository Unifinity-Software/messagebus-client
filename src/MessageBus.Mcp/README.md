# MessageBus.Ai.Mcp

The local encrypting MCP proxy for messagebus.ai.

A job can be driven by a published workflow or by the agent's session. With the service's
`workflows` capability, use `list_workflows {}` to find a fit, then
`start_job {workItem, workflow, instructions?, limitUsd?, runsOn?}` after the user chooses it.
The result contains `jobId`, `url`, `state` and optional `workflow`; report the link and let the
service run the steps after the session closes. `job_status {jobId}` reads progress.

For hands-on orchestration, use `find_job`, `ensure_job`, `send`, `wait` and `complete_job`.
With capability off, use `find_project`, `ensure_project` and `complete_project` instead and
orchestrate here as before. Argument and result names such as `projectId` and `project` stay
unchanged. The [orchestrate skill](../../skills/mbus-orchestrate/SKILL.md) describes both paths
and how a fleet agent coordinates a workflow's Orchestrate step.

Learn more at [messagebus.ai](https://messagebus.ai).
