using System.Collections;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner.Sandbox;

/// <summary>
/// Where a run's agent sends its own OpenTelemetry (the original issue): the service's intake for this workspace, with a credential
/// for this run alone, instead of the owner's <c>OTEL_*</c> settings (which can hold the collector's key).
/// </summary>
public interface IRunTelemetrySource
{
    /// <summary>The run's telemetry settings, or null when the workspace exports none (the run then exports nothing).</summary>
    Task<RunTelemetrySettings?> IssueAsync(string deliveryId, CancellationToken cancellationToken);
}

/// <summary>A run's OTLP/HTTP endpoint and credential, and which signals the workspace takes.</summary>
public sealed record RunTelemetrySettings(Uri Endpoint, string Token, bool Traces, bool Metrics, bool Logs)
{
    /// <summary>
    /// The run's <c>OTEL_*</c> variables: these settings, and every other OTLP exporter variable the runner has removed
    /// (null), so a per-signal endpoint or header the owner passed with <see cref="SandboxEnvVars.PassEnvironment"/> can't
    /// send the run's telemetry elsewhere or with the owner's key.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Variables(IDictionary inherited)
    {
        var variables = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in inherited)
        {
            if (entry.Key is string name && name.StartsWith("OTEL_EXPORTER_OTLP_", StringComparison.OrdinalIgnoreCase)) variables[name] = null;
        }
        variables["OTEL_EXPORTER_OTLP_ENDPOINT"] = Endpoint.AbsoluteUri.TrimEnd('/');
        variables["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf";
        variables["OTEL_EXPORTER_OTLP_HEADERS"] = $"{RunnerTelemetryCredential.HeaderName}={Token}";
        variables["OTEL_TRACES_EXPORTER"] = Traces ? "otlp" : "none";
        variables["OTEL_METRICS_EXPORTER"] = Metrics ? "otlp" : "none";
        variables["OTEL_LOGS_EXPORTER"] = Logs ? "otlp" : "none";
        return variables;
    }
}

/// <summary>No telemetry settings from the service (tests, and runners not signed in to one).</summary>
public sealed class NoRunTelemetry : IRunTelemetrySource
{
    /// <inheritdoc />
    public Task<RunTelemetrySettings?> IssueAsync(string deliveryId, CancellationToken cancellationToken) => Task.FromResult<RunTelemetrySettings?>(null);
}

/// <summary>Asks the service (<see cref="ServiceRoutes.TelemetryCredential"/>) for each run's telemetry credential.</summary>
/// <param name="http">The machine-signed service client.</param>
/// <param name="log">Where a failed request is noted.</param>
public sealed class ServiceRunTelemetry(HttpClient http, TextWriter log) : IRunTelemetrySource
{
    /// <inheritdoc />
    public async Task<RunTelemetrySettings?> IssueAsync(string deliveryId, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(new RunnerTelemetryCredentialRequest { DeliveryId = deliveryId },
            (JsonTypeInfo<RunnerTelemetryCredentialRequest>)MessageBusJson.Options.GetTypeInfo(typeof(RunnerTelemetryCredentialRequest)));
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, ServiceRoutes.TelemetryCredential.TrimStart('/'))
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
            using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent) return null;
            if (!response.IsSuccessStatusCode)
            {
                await log.WriteLineAsync($"No telemetry settings for the run: the service answered {(int)response.StatusCode}.").ConfigureAwait(false);
                return null;
            }
            var credential = MessageBusJson.Deserialize<RunnerTelemetryCredential>(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (credential is null || http.BaseAddress is null) return null;
            return new RunTelemetrySettings(new Uri(http.BaseAddress, credential.Endpoint.TrimStart('/')), credential.Token,
                credential.Traces, credential.Metrics, credential.Logs);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            await log.WriteLineAsync($"No telemetry settings for the run: {ex.Message}").ConfigureAwait(false);
            return null;
        }
    }
}
