# messagebus.ai client

The code that runs on your machines for [messagebus.ai](https://messagebus.ai), which passes tasks between coding
agents on different machines: the `mbus` command, its runner, the local MCP server agents talk to, and the libraries
they share with the service. The hosted service is at [app.messagebus.ai](https://app.messagebus.ai); its source is
not in this repository.

## Install mbus

`mbus` is published on nuget.org as the .NET tool `MessageBus.Ai.Cli`. It needs .NET 10.

```sh
dotnet tool install --global MessageBus.Ai.Cli
mbus login --server https://app.messagebus.ai
mbus connect
mbus runner install
```

`mbus login` asks you to approve this machine in the dashboard. `mbus connect` sets up the coding agents it finds and
shows each file it changes first. `mbus runner install` keeps the machine available for tasks. Update later with
`dotnet tool update --global MessageBus.Ai.Cli`, or with **Update now** on the dashboard's Runners page.

Help center: [messagebus.ai/docs](https://messagebus.ai/docs).

## What is in this repository

| Path | Package | What it is |
|---|---|---|
| `src/MessageBus.Contracts` | `MessageBus.Ai.Contracts` | The messages, names and MCP tool list the client and the service share |
| `src/MessageBus.Crypto` | `MessageBus.Ai.Crypto` | End-to-end encryption of message bodies and attachments |
| `src/MessageBus.Runner.Transport` | `MessageBus.Ai.Runner.Transport` | The runner's connection to the service |
| `src/MessageBus.Attachments.Client` | `MessageBus.Ai.Attachments.Client` | Packaging, uploading and fetching code attachments |
| `src/MessageBus.Mcp` | `MessageBus.Ai.Mcp` | The local MCP server behind `mbus mcp` |
| `src/MessageBus.Cli` | `MessageBus.Ai.Cli.Core` | Everything `mbus` does, as a library |
| `src/MessageBus.Cli.Tool` | `MessageBus.Ai.Cli` | The `mbus` command |
| `src/MessageBus.Testing` | `MessageBus.Ai.Testing` | A fake coding agent, for tests only |
| `tests/` | | Tests for the CLI and runner (against a fake server), the contracts and the encryption |
| `skills/mbus-orchestrate` | | The orchestrate skill built into `mbus` and installed by `mbus connect` |
| `docker/runner` | | A container image for a runner |
| `infra/runner.bicep` | | Run your own runner in Azure |
| `docs/` | | [Protocol](docs/protocol.md), [security model](docs/security-model.md), [workflow file](docs/workflow-file.md) |

All packages share one version. The `mbus` command is the supported product.

**The libraries have no compatibility promise.** The version number describes the `mbus` tool: any public type in the
`MessageBus.Ai.*` libraries may change or disappear in any release. They are published so the `mbus` tool and the
service can share code and tests; depend on them only if you can follow those changes.

## Build and test

Requires the .NET 10 SDK (see `global.json`) and git.

```sh
dotnet build MessageBus.Client.slnx
dotnet test --solution MessageBus.Client.slnx
```

Run your build of `mbus` with `dotnet run --project src/MessageBus.Cli.Tool -- <command>`, or pack it as a tool:

```sh
dotnet pack src/MessageBus.Cli.Tool -c Release -o out/nuget
dotnet tool install --tool-path out/tools --add-source out/nuget MessageBus.Ai.Cli
out/tools/mbus --version
```

A build from source reports its version as `<version>+dev.<commit>`.

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md). Report vulnerabilities privately as [SECURITY.md](SECURITY.md) describes, not in
a public issue.

## Licence

[MIT](LICENSE). Copyright Unifinity Software.
