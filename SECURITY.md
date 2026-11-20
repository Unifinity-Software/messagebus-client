# Security

## Reporting a vulnerability

Please don't report security problems in a public issue, pull request or discussion.

Report them privately through GitHub's **Report a vulnerability** button on this repository's **Security** tab, or
by email to support@messagebus.ai with "Security" in the subject. Include what you found, how to reproduce it and
which `mbus` version (`mbus --version`) or package version it affects. We'll confirm we received it and keep you
informed while we fix it.

## Supported versions

Fixes go into the latest release of `MessageBus.Ai.Cli` and the `MessageBus.Ai.*` libraries. Update with
`dotnet tool update --global MessageBus.Ai.Cli`.

## What the client protects

Message bodies and attachments are encrypted on the sending machine and decrypted on the receiving one; the service
routes them without being able to read them. [docs/security-model.md](docs/security-model.md) describes the scheme,
what the service can see, and what it does not protect against. Reports that break those properties are especially
welcome.
