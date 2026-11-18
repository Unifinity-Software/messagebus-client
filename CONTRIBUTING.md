# Contributing

Thanks for helping. This repository holds the messagebus.ai client: the `mbus` command, its runner, the local MCP
server and the libraries they share with the hosted service.

## Before you start

- For a bug, open an issue with `mbus --version`, your operating system, the command you ran and what it printed.
  Remove tokens, keys and anything else private from the output first.
- For a larger change, open an issue first so we can agree on the shape.
- Security problems go through [SECURITY.md](SECURITY.md), never a public issue.

## Making a change

1. Fork the repository and branch from `main`.
2. Build and test:

   ```sh
   dotnet build MessageBus.Client.slnx
   dotnet test --solution MessageBus.Client.slnx
   ```

3. Follow [AGENTS.md](AGENTS.md): it holds the rules for code, tests and test data.
4. Open a pull request against `main` and describe what changes for users.

## Changes to the protocol

The hosted service at app.messagebus.ai talks to every released `mbus`, so a change to anything the client and
service exchange (contract types, routes, headers, MCP tools, encryption, the update feed) must:

- update [docs/protocol.md](docs/protocol.md) in the same pull request;
- keep working against the service that is live now: new fields optional, new behaviour only when the service says
  it supports it;
- never rename or remove a field, route or tool that released clients use.

The service side follows after the client release that carries the change.

## Licence

By contributing you agree that your contribution is licensed under the [MIT licence](LICENSE).
