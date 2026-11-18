using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner.Sandbox;

/// <summary>
/// Asks the service for each run's repository credential (the original issue): a GitHub App installation token for
/// the task's repository, or the workspace's Azure DevOps token when its admins allowed runs to use
/// it. The service checks this machine holds the delivery. No answer, or no connection, means the
/// run gets none and works from its git bundle, as before.
/// </summary>
/// <param name="http">The machine-signed service client (<see cref="IRunnerHostContext.CreateServiceClient"/>).</param>
/// <param name="log">Where a failed request is noted.</param>
public sealed class ServiceRepoCredentials(HttpClient http, TextWriter log) : IRepoCredentialSource
{
    /// <inheritdoc />
    public string Description => "From the workspace's GitHub App or Azure DevOps connection, for the task's repository only; none when neither is set up.";

    /// <inheritdoc />
    public async Task<RepoCredential?> IssueAsync(RepoCredentialRequest request, CancellationToken cancellationToken)
    {
        var body = new RunnerRepoCredentialRequest
        {
            DeliveryId = request.DeliveryId,
            Repository = request.Repository.AbsoluteUri,
            CanPush = request.CanPush,
        };
        var json = JsonSerializer.Serialize(body, (JsonTypeInfo<RunnerRepoCredentialRequest>)MessageBusJson.Options.GetTypeInfo(typeof(RunnerRepoCredentialRequest)));
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, ServiceRoutes.RepoCredential.TrimStart('/'))
            {
                Content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
            };
            using var response = await http.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NoContent) return null;
            if (!response.IsSuccessStatusCode)
            {
                await log.WriteLineAsync($"No repository credential for {request.Repository}: the service answered {(int)response.StatusCode}.").ConfigureAwait(false);
                return null;
            }
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var credential = MessageBusJson.Deserialize<RunnerRepoCredential>(text);
            return credential is null ? null : new RepoCredential { UserName = credential.UserName, Secret = credential.Secret, ExpiresAt = credential.ExpiresAt };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            await log.WriteLineAsync($"No repository credential for {request.Repository}: {ex.Message}").ConfigureAwait(false);
            return null;
        }
    }
}
