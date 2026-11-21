using Xunit;
using System.Net;
using System.Text;
using MessageBus.Cli.Server;
using MessageBus.Contracts.Json;

namespace MessageBus.Cli.Tests.Infrastructure;

/// <summary>A request the fake server received, with its body read out.</summary>
public sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string BodyText => Encoding.UTF8.GetString(Body);

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// The messagebus.ai server as an <see cref="HttpMessageHandler"/>: routes by method and path, and
/// records every request. <c>/api/server</c> answers like a server with CLI sign-in set up unless a
/// test replaces it.
/// </summary>
public sealed class FakeServer : HttpMessageHandler
{
    public const string Url = "http://127.0.0.1:5200";

    private readonly Dictionary<string, Func<RecordedRequest, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);
    private readonly List<RecordedRequest> _requests = [];

    public FakeServer() => On(HttpMethod.Get, "/api/server", _ => Json(ConfiguredServerInfo));

    public static ServerInfoResponse ConfiguredServerInfo { get; } = new()
    {
        Product = "messagebus.ai",
        Version = "0.1.0",
        Url = Url,
        Capabilities = [], // Feature-specific tests opt in to their supported protocols.
        SignIn = new ServerSignIn
        {
            Microsoft = true,
            CliApprovalUrl = Url + "/cli-login",
        },
    };

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public RecordedRequest Single(HttpMethod method, string path) =>
        Assert.Single(Requests, r => r.Method == method && r.Uri.AbsolutePath == path);

    public FakeServer On(HttpMethod method, string path, Func<RecordedRequest, HttpResponseMessage> respond)
    {
        _routes[$"{method} {path}"] = respond;
        return this;
    }

    public static HttpResponseMessage Json<T>(T value, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(
            value is ServerInfoResponse info
                ? System.Text.Json.JsonSerializer.Serialize(info, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
                : MessageBusJson.Serialize(value),
            Encoding.UTF8, "application/json"),
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!, headers, body);
        lock (_requests)
        {
            _requests.Add(recorded);
        }
        return _routes.TryGetValue($"{request.Method} {request.RequestUri!.AbsolutePath}", out var respond)
            ? respond(recorded)
            : new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
