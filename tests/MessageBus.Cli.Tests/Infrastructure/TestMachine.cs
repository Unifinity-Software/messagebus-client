using System.Text;
using MessageBus.Cli.Connect;
using MessageBus.Cli.Hosting;
using MessageBus.Cli.Mcp;
using MessageBus.Cli.Runner;
using MessageBus.Cli.SignIn;
using Microsoft.Extensions.Time.Testing;

namespace MessageBus.Cli.Tests.Infrastructure;

/// <summary>
/// A pretend machine for one test: a temp home folder, a temp PATH folder for fake agent commands,
/// environment variables, a fake server and a fake clock. <see cref="RunAsync"/> runs a real mbus
/// command line against it in-process.
/// </summary>
public sealed class TestMachine : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mbus-tests", Guid.NewGuid().ToString("N"));

    public TestMachine()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Bin);
        Env["PATH"] = Bin;
        Env["PATHEXT"] = ".CMD;.EXE";
        if (OperatingSystem.IsMacOS())
        {
            Env["MBUS_KEY_STORE"] = "file"; // keep test keys out of the login Keychain
        }
    }

    public string Home => Path.Combine(_root, "home");

    public string Bin => Path.Combine(_root, "bin");

    public string MbusFolder => Path.Combine(Home, ".mbus");

    public Dictionary<string, string> Env { get; } = new(StringComparer.Ordinal);

    public FakeServer Server { get; } = new();
    internal Func<HttpMessageHandler> LocalModelHandler { get; set; } = MessageBus.Cli.Agents.OpenCodeReadiness.Handler;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));

    /// <summary>The person's browser for mbus login: records what mbus asked it to open.</summary>
    public RecordingBrowser Browser { get; } = new();

    public Dictionary<string, string> SkillFiles { get; } = new(StringComparer.Ordinal);

    /// <summary>The skill mbus connect installs; null for <see cref="SkillFiles"/>.</summary>
    public ISkillSource? Skills { get; set; }

    public ILocalMcpServer McpServer { get; set; } = new DelegateLocalMcpServer((_, _) => Task.FromResult(0));

    /// <summary>The service manager <c>mbus runner install</c> uses; tests that install set a fake one.</summary>
    public Func<MessageBus.Cli.Runner.Service.ServiceManager>? RunnerServices { get; set; }

    /// <summary>The mbus that is running; a relative path (the default) counts as no program of its own.</summary>
    public McpLaunchCommand MbusCommand { get; set; } = new(Path.Combine("opt", "mbus", "mbus"), ["mcp"]);

    /// <summary>What the person types, one answer per line.</summary>
    public string Input { get; set; } = "";

    /// <summary>Puts a fake agent command on PATH that prints <paramref name="versionOutput"/>.</summary>
    public void InstallAgent(string command, string versionOutput)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(Path.Combine(Bin, command + ".cmd"), $"@echo off\r\necho {versionOutput}\r\n");
            return;
        }
        var path = Path.Combine(Bin, command);
        File.WriteAllText(path, $"#!/bin/sh\necho '{versionOutput}'\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>
    /// Puts a fake command in <paramref name="folder"/> (default: on PATH): called with a first argument in
    /// <paramref name="answers"/> it prints that answer; called with anything else, <paramref name="otherwise"/>.
    /// </summary>
    public void InstallFake(string command, FakeAnswer otherwise, IReadOnlyDictionary<string, FakeAnswer>? answers = null, string? folder = null)
    {
        folder ??= Bin;
        Directory.CreateDirectory(folder);
        answers ??= new Dictionary<string, FakeAnswer>();
        if (OperatingSystem.IsWindows())
        {
            static string Echo(FakeAnswer a) => string.Concat(a.Text.Split('\n').Select(line =>
                (line.Length == 0 ? "echo." : "echo " + string.Concat(line.Select(c => c is '^' or '&' or '|' or '<' or '>' ? "^" + c : c == '%' ? "%%" : c.ToString())))
                + (a.ToStderr ? " 1>&2" : "") + "\r\n")) + $"exit /b {a.ExitCode}\r\n";
            var script = new System.Text.StringBuilder("@echo off\r\n");
            var labels = answers.Select((a, i) => (a.Key, Label: $"answer{i}", a.Value)).ToList();
            foreach (var (arg, label, _) in labels)
            {
                script.Append($"if \"%~1\"==\"{arg}\" goto {label}\r\n");
            }
            script.Append(Echo(otherwise));
            foreach (var (_, label, answer) in labels)
            {
                script.Append($":{label}\r\n").Append(Echo(answer));
            }
            File.WriteAllText(Path.Combine(folder, command + ".cmd"), script.ToString());
            return;
        }
        static string Print(FakeAnswer a) => $"printf '%s\\n' '{a.Text.Replace("'", "'\\''")}'{(a.ToStderr ? " >&2" : "")}; exit {a.ExitCode};;";
        var sh = new System.Text.StringBuilder("#!/bin/sh\ncase \"$1\" in\n");
        foreach (var (arg, answer) in answers)
        {
            sh.Append($"  '{arg}') {Print(answer)}\n");
        }
        sh.Append($"  *) {Print(otherwise)}\nesac\n");
        var path = Path.Combine(folder, command);
        File.WriteAllText(path, sh.ToString());
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Writes a file under the home folder.</summary>
    public string WriteHomeFile(string relativePath, string contents)
    {
        var path = Path.Combine(Home, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public async Task<CliRun> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var host = CreateHost(output, error);
        var exitCode = await CliApp.RunAsync(args, host, Xunit.TestContext.Current.CancellationToken);
        return new CliRun(exitCode, output.ToString(), error.ToString());
    }

    /// <summary>The host <see cref="RunAsync"/> runs mbus with, for code that takes one directly.</summary>
    public CliHost CreateHost(TextWriter? output = null, TextWriter? error = null, bool terminal = false) =>
        new()
        {
            Out = output ?? TextWriter.Null,
            OutIsTerminal = terminal,
            Error = error ?? TextWriter.Null,
            In = new StringReader(Input),
            OpenStandardInput = () => new MemoryStream(),
            OpenStandardOutput = () => new MemoryStream(),
            GetEnvironmentVariable = name => Env.TryGetValue(name, out var value) ? value : null,
            HomeDirectory = Home,
            MachineName = "test-mac",
            MbusCommand = MbusCommand,
            ExtraAgentDirectories = [],
            CreateHttpHandler = () => new NonDisposingHandler(Server),
            CreateLocalModelHttpHandler = LocalModelHandler,
            Time = Time,
            Browser = Browser,
            Skills = Skills ?? new DictionarySkillSource(SkillFiles),
            McpServer = McpServer,
            SleepSettings = _ => Task.FromResult<string?>(MessageBus.Cli.Readiness.MacSleepSettings.Describe(" sleep 10", " PreventUserIdleSystemSleep 0")),
            RunnerServices = RunnerServices ?? (() => throw new InvalidOperationException("This test would register a real service.")),
        };

    public void Dispose()
    {
        RunWorkspace.TryDelete(_root);
    }

    /// <summary>mbus disposes its HttpClients; the fake server outlives them.</summary>
    private sealed class NonDisposingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
        }
    }

    private sealed class DictionarySkillSource(Dictionary<string, string> files) : ISkillSource
    {
        public string Name => "mbus-orchestrate";

        public IReadOnlyList<SkillFile> GetFiles() =>
            [.. files.Select(f => new SkillFile(f.Key, Encoding.UTF8.GetBytes(f.Value)))];
    }
}

public sealed record CliRun(int ExitCode, string Output, string Error);

/// <summary>What a fake command prints (one line per <c>\n</c>) and its exit code.</summary>
public sealed record FakeAnswer(string Text, int ExitCode = 0, bool ToStderr = false);

/// <summary>Opens nothing; remembers the addresses.</summary>
public sealed class RecordingBrowser : MessageBus.Cli.SignIn.IBrowser
{
    public List<Uri> Opened { get; } = [];

    public Task<bool> OpenAsync(Uri url, CancellationToken cancellationToken)
    {
        Opened.Add(url);
        return Task.FromResult(true);
    }
}
