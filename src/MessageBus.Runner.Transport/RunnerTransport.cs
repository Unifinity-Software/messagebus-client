using MessageBus.Contracts.Json;
using MessageBus.Contracts.Runner;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace MessageBus.Runner.Transport;

/// <summary>The runner connection's settings (<c>mbus.v1</c>) on top of the generic transport.</summary>
public static class RunnerTransport
{
    /// <summary>How long the runner waits before trying again while its machine is held for approval.</summary>
    public static readonly TimeSpan PendingApprovalDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Close codes after which the runner stops: Groundhog's terminal ones (credential revoked, machine
    /// removed, session replaced) and "update required" (update, then start again).
    /// </summary>
    /// <param name="code">The close code.</param>
    public static bool IsTerminalClose(int code) =>
        RunnerCloseCodes.IsTerminal(code) || code == RunnerCloseCodes.UpdateRequired;

    /// <summary>
    /// Client options for <c>mbus runner</c>: <see cref="RunnerProtocol.ConnectPath"/>,
    /// <see cref="RunnerProtocol.SubProtocol"/>, the runner's close-code rules and contract JSON for the
    /// routes. Add <see cref="ReverseConnectionClientOptions.PrepareRequest"/> (machine signature, session,
    /// version and slots headers) with a <c>with</c> expression.
    /// </summary>
    /// <param name="serverAddress">The service's address, for example <c>http://localhost:5200</c>.</param>
    /// <param name="mapRoutes">Maps <see cref="RunnerRoutes"/> handlers.</param>
    public static ReverseConnectionClientOptions ClientOptions(Uri serverAddress, Action<WebApplication> mapRoutes) => new()
    {
        ServerAddress = serverAddress,
        ConnectPath = RunnerProtocol.ConnectPath,
        SubProtocol = RunnerProtocol.SubProtocol,
        MapRoutes = mapRoutes,
        ConfigureServices = services => services.ConfigureHttpJsonOptions(json => MessageBusJson.Configure(json.SerializerOptions)),
        IsTerminalClose = IsTerminalClose,
        MinimumDelayAfterClose = code => code == RunnerCloseCodes.PendingApproval ? PendingApprovalDelay : null,
    };
}
