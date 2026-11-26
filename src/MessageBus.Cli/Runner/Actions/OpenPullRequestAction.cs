using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MessageBus.Cli.Runner.Sandbox;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner.Actions;

internal sealed class OpenPullRequestAction(
    IRepoCredentialSource credentials, HttpClient? http = null, string? pushDestination = null) : IRunnerAction
{
    private static readonly HttpClient GitHub = new(new HttpClientHandler { AllowAutoRedirect = false });
    private static readonly Uri GitHubApi = new("https://api.github.com/");
    private const string AutoMergeMutation = """
        mutation($id: ID!) {
          enablePullRequestAutoMerge(input: {pullRequestId: $id, mergeMethod: SQUASH}) {
            pullRequest { id }
          }
        }
        """;

    public string Kind => RunnerActionKinds.OpenPullRequest;

    public async Task<SettleResult> RunAsync(RunnerActionContext context, CancellationToken cancellationToken)
    {
        var delivery = context.Delivery;
        if (delivery.RunnerAction?.OpenPullRequest is not { } parameters || string.IsNullOrWhiteSpace(parameters.Base))
            return Failed("Missing pull request parameters.");

        var inputSteps = delivery.StepContext?.Inputs.Select(input => input.FromStep)
            .Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (!inputSteps.Contains(parameters.BranchFrom, StringComparer.Ordinal) || !inputSteps.All(IsStepDirectory))
            return Failed("Missing or invalid branch input.");

        var repository = delivery.Envelope.Refs.Select(RunWorkspace.RepositoryUrl)
            .Select(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null)
            .FirstOrDefault(uri => uri is not null);
        if (repository is null || repository.Host != "github.com" || !repository.IsDefaultPort
            || repository.UserInfo.Length != 0 || repository.Query.Length != 0 || repository.Fragment.Length != 0
            || repository.AbsolutePath.Trim('/').Split('/') is not [var owner, var name])
            return Failed("The task repository must be a GitHub HTTPS repository.");

        name = name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        if (owner.Length == 0 || name.Length == 0)
            return Failed("The task repository must be a GitHub HTTPS repository.");

        var operation = "Read workflow inputs";
        try
        {
            var branch = context.InputBranches.GetValueOrDefault(parameters.BranchFrom)
                ?? await ReadInputAsync(context, parameters.BranchFrom, "branch.txt", cancellationToken).ConfigureAwait(false);
            branch = branch?.Trim();
            if (string.IsNullOrWhiteSpace(branch))
                return Failed("Missing branch input.");

            var headRef = "refs/heads/" + branch;
            var baseRef = "refs/heads/" + parameters.Base;
            if (headRef.Equals(baseRef, StringComparison.OrdinalIgnoreCase)
                || !await RunnerGit.IsValidRefAsync(headRef, cancellationToken).ConfigureAwait(false)
                || !await RunnerGit.IsValidRefAsync(baseRef, cancellationToken).ConfigureAwait(false))
                return Failed("Invalid source or destination branch.");

            var description = await DescriptionAsync(context, inputSteps, cancellationToken).ConfigureAwait(false);
            operation = "Request repository credential";
            var credential = await credentials.IssueAsync(new RepoCredentialRequest
            {
                DeliveryId = delivery.DeliveryId,
                WorkspaceId = delivery.Envelope.WorkspaceId,
                Repository = repository,
                CanPush = true,
            }, cancellationToken).ConfigureAwait(false);
            if (credential is null || string.IsNullOrEmpty(credential.Secret))
                return Failed("no repository credential");

            var repo = owner + "/" + name;
            var pulls = $"repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(name)}/pulls";
            var lookup = pulls + "?state=open&head=" + Uri.EscapeDataString(owner + ":" + branch);
            operation = "Look up the open pull request";
            var found = await FindAsync(lookup, credential.Secret, cancellationToken).ConfigureAwait(false);

            operation = "Push the branch";
            if (!await RunnerGit.PushBranchAsync(context.WorkDirectory, context.RunDirectory, headRef, repository,
                pushDestination ?? repository.AbsoluteUri, credential, cancellationToken).ConfigureAwait(false))
                return Failed("Git could not push the source branch.");

            if (found is null)
            {
                operation = "Open the pull request";
                using var response = await SendAsync(HttpMethod.Post, pulls, credential.Secret, new JsonObject
                {
                    ["head"] = branch,
                    ["base"] = parameters.Base,
                    ["title"] = delivery.Envelope.Title,
                    ["body"] = description,
                }, cancellationToken).ConfigureAwait(false);
                // Another delivery can create the PR after the lookup and before this request.
                if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
                    found = await FindAsync(lookup, credential.Secret, cancellationToken).ConfigureAwait(false);
                found ??= await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            }

            operation = "Read the pull request reference";
            var number = found.Value.GetProperty("number").GetInt64();
            var url = found.Value.GetProperty("html_url").GetString();
            if (number <= 0 || url is null || url.Contains(credential.Secret, StringComparison.Ordinal))
                return Failed("GitHub did not return a usable pull request reference.");

            var reference = new Ref { Kind = "github.pr", Repo = repo, Number = number, Url = url };
            if (parameters.AutoMerge is true
                && (!found.Value.TryGetProperty("auto_merge", out var autoMerge) || autoMerge.ValueKind == JsonValueKind.Null))
            {
                operation = "Request pull request auto-merge";
                try
                {
                    var nodeId = found.Value.GetProperty("node_id").GetString();
                    if (string.IsNullOrWhiteSpace(nodeId))
                        return Failed("GitHub did not return the pull request node ID.", reference);
                    using var response = await SendAsync(HttpMethod.Post, "graphql", credential.Secret, new JsonObject
                    {
                        ["query"] = AutoMergeMutation,
                        ["variables"] = new JsonObject { ["id"] = nodeId },
                    }, cancellationToken).ConfigureAwait(false);
                    var result = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
                    if (result.TryGetProperty("errors", out _)
                        || result.GetProperty("data").GetProperty("enablePullRequestAutoMerge").GetProperty("pullRequest").GetProperty("id").GetString() != nodeId)
                        return Failed("GitHub did not enable pull request auto-merge.", reference);
                }
                catch (Exception ex) when (IsOperationFailure(ex, cancellationToken))
                {
                    return Failed(FailureReason(operation, ex), reference);
                }
            }

            return new SettleResult
            {
                Status = SettleStatus.Succeeded,
                Outcome = "opened",
                Summary = $"Pull request #{number} is open.",
                Refs = [reference],
            };
        }
        catch (Exception ex) when (IsOperationFailure(ex, cancellationToken))
        {
            return Failed(FailureReason(operation, ex));
        }
    }

    private async Task<JsonElement?> FindAsync(string lookup, string secret, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, lookup, secret, null, ct).ConfigureAwait(false);
        var results = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        return results.GetArrayLength() > 0 ? results[0] : null;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string secret, JsonObject? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(http?.BaseAddress ?? GitHubApi, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Headers.UserAgent.ParseAdd("messagebus-runner");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await (http ?? GitHub).SendAsync(request, ct).ConfigureAwait(false);
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return json.RootElement.Clone();
    }

    private static async Task<string> DescriptionAsync(RunnerActionContext context, string[] steps, CancellationToken ct)
    {
        var body = new StringBuilder();
        foreach (var step in steps)
        {
            var summary = await ReadInputAsync(context, step, "summary.txt", ct).ConfigureAwait(false);
            var exhibits = await ReadInputAsync(context, step, "exhibits.json", ct).ConfigureAwait(false);
            if (summary is null && exhibits is null) continue;
            body.Append("## ").AppendLine(step).AppendLine();
            if (!string.IsNullOrWhiteSpace(summary)) body.AppendLine(summary.Trim()).AppendLine();
            if (exhibits is null) continue;
            using var json = JsonDocument.Parse(exhibits);
            foreach (var entry in json.RootElement.EnumerateArray())
            {
                var exhibit = MessageBusJson.Deserialize<ExhibitRef>(entry.GetRawText())!;
                body.Append("- ").Append(exhibit.Name);
                if (!string.IsNullOrWhiteSpace(exhibit.Caption)) body.Append(" — ").Append(exhibit.Caption);
                body.AppendLine();
            }
            body.AppendLine();
        }
        return body.ToString().TrimEnd();
    }

    private static bool IsStepDirectory(string step) =>
        !string.IsNullOrWhiteSpace(step) && step is not "." and not ".."
        && step.IndexOfAny(['/', '\\', ':']) < 0 && step.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static async Task<string?> ReadInputAsync(RunnerActionContext context, string step, string name, CancellationToken ct)
    {
        var metadata = Path.GetDirectoryName(context.InputsDirectory)!;
        var stepDirectory = Path.Combine(context.InputsDirectory, step);
        foreach (var directory in new[] { metadata, context.InputsDirectory, stepDirectory })
        {
            if (!Directory.Exists(directory)) return null;
            if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Workflow input directories must be local.");
        }

        var file = Path.Combine(stepDirectory, name);
        if (!File.Exists(file)) return null;
        if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("Workflow input files must be local.");
        return await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
    }

    private static bool IsOperationFailure(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && ex is (IOException or UnauthorizedAccessException or HttpRequestException
            or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException
            or OperationCanceledException);

    // Git output, remote bodies and exception text may echo credentials. Return only our own messages.
    private static string FailureReason(string operation, Exception ex) =>
        ex is HttpRequestException { StatusCode: { } status }
            ? $"{operation} failed (HTTP {(int)status})."
            : $"{operation} failed.";

    private static SettleResult Failed(string reason, Ref? reference = null) => new()
    {
        Status = SettleStatus.Failed,
        Outcome = "failed",
        Summary = reason,
        Error = reason,
        Refs = reference is null ? [] : [reference],
    };
}
