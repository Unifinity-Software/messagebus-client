using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MessageBus.Cli.Tests.Infrastructure;

// Deliberately two different listeners: no inference request can be counted as both local and cloud.
internal sealed class OpenCodeProofEndpoint : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly ConcurrentBag<Task> _connections = [];
    internal readonly ConcurrentQueue<object> Requests = [];
    internal readonly ConcurrentQueue<string> Models = [];
    internal readonly ConcurrentQueue<string> Errors = [];
    internal bool FailInference { get; set; }
    internal JsonObject Config { get; set; } = new();
    internal string Url { get; }
    internal const string ApiId = "proof-local-coder";

    internal OpenCodeProofEndpoint()
    {
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _loop = Accept();
    }

    private async Task Accept()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
                _connections.Add(Handle(await _listener.AcceptTcpClientAsync(_stop.Token)));
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }

    private async Task Handle(TcpClient client)
    {
        using (client)
        try
        {
            var stream = client.GetStream();
            // One request per connection; explicitly close rather than implement a partial keep-alive protocol.
            using var reader = new StreamReader(stream, Encoding.Latin1, false, 4096, leaveOpen: true);
            var first = await reader.ReadLineAsync(_stop.Token) ?? throw new IOException("Missing request line");
            var length = 0;
            while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } header)
            {
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header.Split(':')[1]);
                if (header.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected chunked request; proof cannot silently drop it");
            }
            if (length > 1024 * 1024) throw new IOException("Oversized proof request");
            var chars = new char[length];
            var read = 0;
            while (read < length)
            {
                var n = await reader.ReadAsync(chars.AsMemory(read), _stop.Token);
                if (n == 0) throw new IOException("Truncated request");
                read += n;
            }
            var body = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(chars));
            var path = first.Split(' ')[1];
            Requests.Enqueue(new { request = first, body });
            var status = "200 OK";
            var type = "application/json";
            string response;
            if (path == "/api/v1/models") response = $$"""{"models":[{"type":"llm","key":"{{ApiId}}","loaded_instances":[{"id":"{{ApiId}}"}]}]}""";
            else if (path == "/v1/models") response = $$"""{"object":"list","data":[{"id":"{{ApiId}}","object":"model","owned_by":"proof"}]}""";
            else if (path == "/.well-known/opencode" || path == "/api/config") response = new JsonObject { ["config"] = Config.DeepClone() }.ToJsonString();
            else if (path == "/api/orgs") response = "[{\"id\":\"proof-org\",\"name\":\"Proof\"}]";
            else if (path == "/v1/chat/completions")
            {
                var input = JsonNode.Parse(body)!;
                var model = input["model"]!.GetValue<string>();
                Models.Enqueue(model);
                if (FailInference || model != ApiId)
                {
                    status = "400 Bad Request"; // non-retryable, keeps the failure proof short
                    response = "{\"error\":{\"message\":\"PROOF_PRIMARY_FAILURE\",\"type\":\"invalid_request_error\",\"code\":\"proof_failure\"}}";
                }
                else if (input["stream"]?.GetValue<bool>() == true)
                {
                    type = "text/event-stream";
                    response = Event(new { role = "assistant", content = "LOCAL_OK" }, (string?)null)
                        + Event(new { }, "stop") + "data: [DONE]\n\n";
                    string Event(object delta, string? finish) => "data: " + JsonSerializer.Serialize(new
                    {
                        id = "chatcmpl-proof", @object = "chat.completion.chunk", created = 1, model,
                        choices = new[] { new { index = 0, delta, finish_reason = finish } },
                        usage = new { prompt_tokens = 12, completion_tokens = 3, total_tokens = 15 },
                    }) + "\n\n";
                }
                else response = JsonSerializer.Serialize(new
                {
                    id = "chatcmpl-proof", @object = "chat.completion", created = 1, model,
                    choices = new[] { new { index = 0, message = new { role = "assistant", content = "LOCAL_OK" }, finish_reason = "stop" } },
                    usage = new { prompt_tokens = 12, completion_tokens = 3, total_tokens = 15 },
                });
            }
            else { status = "404 Not Found"; response = "{}"; }
            var bytes = Encoding.UTF8.GetBytes(response);
            var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, _stop.Token);
            await stream.WriteAsync(bytes, _stop.Token);
        }
        catch (Exception e)
        {
            if (!_stop.IsCancellationRequested) Errors.Enqueue(e.ToString());
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _loop;
        await Task.WhenAll(_connections);
        _stop.Dispose();
    }
}
