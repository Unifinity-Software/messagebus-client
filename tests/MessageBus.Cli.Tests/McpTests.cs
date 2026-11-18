using System.Net;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Mcp;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Http;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class McpTests
{
    [Fact]
    public async Task Version_reports_the_cli_library_version_instead_of_the_entry_assembly_version()
    {
        using var machine = new TestMachine();

        var run = await machine.RunAsync("--version");

        Assert.Equal(0, run.ExitCode);
        Assert.Equal(CliHost.Version + Environment.NewLine, run.Output);
    }

    [Fact]
    public async Task Mcp_in_a_container_reaches_the_server_with_the_api_key_and_keeps_stdout_for_mcp()
    {
        using var machine = new TestMachine();
        machine.Env["MBUS_SERVER"] = FakeServer.Url;
        machine.Env["MBUS_API_KEY"] = "mbk_secret";
        machine.Server.On(HttpMethod.Get, "/api/ping", _ => new HttpResponseMessage(HttpStatusCode.OK));
        Uri? serverUrl = null;
        machine.McpServer = new DelegateLocalMcpServer(async (context, ct) =>
        {
            serverUrl = context.ServerUrl;
            using var response = await context.Http.GetAsync("api/ping", ct);
            return response.IsSuccessStatusCode ? 7 : 1;
        });

        var run = await machine.RunAsync("mcp");

        Assert.Equal(7, run.ExitCode);
        Assert.Equal(new Uri(FakeServer.Url), serverUrl);
        var ping = machine.Server.Single(HttpMethod.Get, "/api/ping");
        Assert.Equal("MbusKey mbk_secret", ping.Header("Authorization"));
        Assert.Equal("mcp", ping.Header(MbusHeaders.ClientComponent));
        Assert.Equal("", run.Output);
    }

    [Fact]
    public async Task Mcp_without_sign_in_exits_without_starting_the_server()
    {
        using var machine = new TestMachine();
        var started = false;
        machine.McpServer = new DelegateLocalMcpServer((_, _) => { started = true; return Task.FromResult(0); });

        var run = await machine.RunAsync("mcp");

        Assert.NotEqual(0, run.ExitCode);
        Assert.False(started);
        Assert.Equal("", run.Output);
    }

    /// <summary>
    /// the original issue: the version header goes only to a person's terminal: never on stdout that carries MCP, JSON for a script,
    /// help or --version, and not before the commands that print a fuller summary themselves.
    /// </summary>
    [Theory]
    [InlineData(true, true, "status")]
    [InlineData(true, true, "runner", "install")]
    [InlineData(false, false, "status")]
    [InlineData(false, true, "status", "--json")]
    [InlineData(false, true, "mcp")]
    [InlineData(false, true, "runner")]
    [InlineData(false, true, "doctor")]
    [InlineData(false, true, "login", "--help")]
    [InlineData(false, true, "--version")]
    [InlineData(false, true, "no-such-command")]
    public void The_header_shows_only_on_a_terminal_and_never_on_mcp_or_json_output(bool shown, bool terminal, params string[] args)
    {
        using var machine = new TestMachine();
        var parse = CliApp.BuildRootCommand(machine.CreateHost()).Parse(args);
        Assert.Equal(shown, MessageBus.Cli.Output.CliHeader.ShouldShow(parse, terminal));
    }
}
