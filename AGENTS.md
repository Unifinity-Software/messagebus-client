# AGENTS.md

Instructions for coding agents (and people) working in this repository. README.md says what is here.

## The repository

The messagebus.ai client: the `mbus` command (`src/MessageBus.Cli` is the library, `src/MessageBus.Cli.Tool` the
executable), its runner, the local MCP server (`src/MessageBus.Mcp`) and the libraries they share with the hosted
service (`Contracts`, `Crypto`, `Runner.Transport`, `Attachments.Client`). The service lives elsewhere and builds
against exact published versions of these packages. So anything both sides exchange is a public protocol:
[docs/protocol.md](docs/protocol.md).

## Rules

- **The protocol stays compatible with the live service.** A change to what the client and service exchange (contract
  types, JSON names, routes, headers, close codes, MCP tools, encryption, the update feed) updates `docs/protocol.md`
  in the same pull request. New fields are optional, and new behaviour starts only when the service says it supports
  it. Never rename or remove a JSON field, route or tool without a deprecation period.
- **Contracts stay client-shaped.** A type belongs in `MessageBus.Contracts` only if a client project uses it.
- **Every public Contracts and Crypto member is documented**: a missing doc comment fails the build.
- **Contracts, Crypto and Attachments.Client stay trim- and AOT-safe** (`IsAotCompatible`): JSON goes through the
  source-generated `MessageBusJson` context, never reflection.
- **No secrets, keys or personal details in the repository**: no real tokens, machine names, tenant or account
  ids, internal host names or personal paths, in code, tests or docs. Test data uses made-up names.
- **Package ids never change.** The eight ids in README.md are published on nuget.org.

## Tests

`dotnet build MessageBus.Client.slnx` and `dotnet test --solution MessageBus.Client.slnx` must pass.

Before adding a test, ask whether it guards real behaviour nothing else covers. Keep tests fast:

- Anything time-based takes `TimeProvider`; tests use `FakeTimeProvider` and jump straight to the target time. No
  `Task.Delay` or polling against real time.
- CLI tests run the real commands in-process against a fake server (an `HttpMessageHandler`), with temporary folders
  for the home directory and the fake agent (`MessageBus.Testing`) instead of a real coding agent.
- Keep xunit parallelization on; tests must not share mutable state.
- Tests that start `mbus`, use this machine's key or push skip when they run inside a messagebus.ai task
  (`tests/Shared/InsideRun.cs`); they run outside a task and in CI.
- No tests for trivial code, legacy or compatibility shims.

## Documentation

Customer-facing behaviour is described in the help center at [messagebus.ai/docs](https://messagebus.ai/docs). When a
pull request changes what users see or do, say so in its description so the help center can follow.
