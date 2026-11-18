using System.Net;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Security;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Http;
using MessageBus.Tests.Shared;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class KeysCommandTests
{
    [Fact]
    public async Task List_shows_the_local_device_fingerprint_even_when_the_service_has_no_keys()
    {
        Assert.SkipWhen(InsideRun.Now, InsideRun.MachineKeyReason);
        using var machine = new TestMachine();
        var store = new ConfigStore(machine.MbusFolder, "file");
        using var device = MachineKey.Create();
        store.Save(new CliConfig { Server = FakeServer.Url, WorkspaceId = "w100", MachineId = "mch_local", MachineName = "Admin laptop", KeyStore = "file" });
        store.SaveKeyPem(device.ExportPrivateKeyPem());
        machine.Server
            .On(HttpMethod.Get, ApiRoutes.Caller, _ => Json("{\"permissions\":[]}"))
            .On(HttpMethod.Get, "/api/w/w100/keys", _ => Json("[]"))
            .On(HttpMethod.Get, "/api/w/w100/keys/rotation", _ => Json("[]"));

        var result = await machine.RunAsync("keys", "list");

        Assert.True(result.ExitCode == 0, result.Error);
        Assert.Contains("Admin laptop (mch_local)", result.Output);
        Assert.Contains(string.Join(' ', device.Fingerprint.Chunk(4).Select(c => new string(c))), result.Output);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, System.Text.Encoding.UTF8, "application/json"),
    };
}
