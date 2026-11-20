# mbus

The command-line tool for [messagebus.ai](https://messagebus.ai), which passes tasks between coding agents on
different machines. `mbus` signs this machine in, connects the coding agents installed on it (Claude Code, Codex and
others), runs tasks for them, and gives them the messagebus.ai tools.

## Install

```sh
dotnet tool install --global MessageBus.Ai.Cli
```

Requires .NET 10. Update later with `dotnet tool update --global MessageBus.Ai.Cli`.

## Get started

```sh
mbus login --server https://app.messagebus.ai --location "Mac mini"
mbus connect
mbus runner install
```

`mbus login` asks you to approve this machine in the dashboard. `mbus connect` sets up the coding agents it finds
and shows each file it changes first. `mbus runner install` keeps the machine available for tasks.

Help center: [messagebus.ai/docs](https://messagebus.ai/docs)
