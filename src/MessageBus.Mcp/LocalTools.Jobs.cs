using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;

namespace MessageBus.Mcp;

internal sealed partial class LocalTools
{
    private const string WorkflowsUnavailable = "Workflows are not switched on for this workspace.";

    private Task<bool> WorkflowsSupportedAsync(CancellationToken ct) => _o.Http.BaseAddress is null
        ? Task.FromResult(false)
        : TargetCapabilities.SupportsWorkflowsAsync(_o.Http, ct);

    private string WorkflowRoute(string path) => $"api/w/{Uri.EscapeDataString(_ws)}/{path}";

    private async Task<ListWorkflowsResult> ListWorkflowsAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, WorkflowRoute("workflows"));
        var type = (JsonTypeInfo<WorkflowSummary[]>)MessageBusJson.Options.GetTypeInfo(typeof(WorkflowSummary[]));
        return new() { Workflows = await SendWorkflowRequestAsync(request, type, ct) };
    }

    private async Task<StartJobResult> StartJobAsync(StartJobInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.WorkItem))
            throw McpToolException.Invalid("workItem", "Give the tracker work item key.");
        if (string.IsNullOrWhiteSpace(input.Workflow))
            throw McpToolException.Invalid("workflow", "Give a workflow id or name.");
        if (input.RunsOn is not (null or "workspace" or "owner-runners"))
            throw McpToolException.Invalid("runsOn", "Use workspace or owner-runners.");

        var workflows = (await ListWorkflowsAsync(ct)).Workflows;
        var workflow = workflows.FirstOrDefault(w => w.Id.Equals(input.Workflow, StringComparison.OrdinalIgnoreCase));
        if (workflow is null)
        {
            var matches = workflows.Where(w => w.Name.Equals(input.Workflow, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
            if (matches.Length > 1)
                throw McpToolException.Invalid("workflow", "More than one workflow has that name. Give its id.");
            workflow = matches.FirstOrDefault()
                ?? throw McpToolException.NotFound("No workflow matches that id or name.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, WorkflowRoute("jobs"))
        {
            Content = JsonContent.Create(new NewWorkflowJob(input.WorkItem, workflow.Id, input.Instructions, input.LimitUsd, input.RunsOn),
                WorkflowHttpJson.Default.NewWorkflowJob),
        };
        var job = await SendWorkflowRequestAsync(request, WorkflowHttpJson.Default.WorkflowJobView, ct);
        return new() { JobId = job.Id, Url = JobUrl(job.Id), State = job.State, Workflow = job.Workflow };
    }

    private async Task<JobStatusResult> JobStatusAsync(JobStatusInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.JobId) || input.JobId is "." or "..")
            throw McpToolException.Invalid("jobId", "Give the job id.");
        using var request = new HttpRequestMessage(HttpMethod.Get, WorkflowRoute($"jobs/{Uri.EscapeDataString(input.JobId)}"));
        var job = await SendWorkflowRequestAsync(request, WorkflowHttpJson.Default.WorkflowJobView, ct);
        return new()
        {
            JobId = job.Id, Url = JobUrl(job.Id), State = job.State, SpentUsd = job.SpentUsd,
            LimitUsd = job.LimitUsd, ActiveStepIds = job.ActiveStepIds ?? [], Steps = job.Steps ?? [],
        };
    }

    private string JobUrl(string id) => $"{_o.Http.BaseAddress!.AbsoluteUri.TrimEnd('/')}/jobs/{Uri.EscapeDataString(id)}";

    private async Task<T> SendWorkflowRequestAsync<T>(HttpRequestMessage request, JsonTypeInfo<T> type, CancellationToken ct)
    {
        using var response = await _o.Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw McpToolException.NotFound(WorkflowsUnavailable);
        if (!response.IsSuccessStatusCode)
        {
            var code = response.StatusCode switch
            {
                HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => McpErrorCodes.InvalidInput,
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => McpErrorCodes.Forbidden,
                HttpStatusCode.Conflict => McpErrorCodes.Conflict,
                HttpStatusCode.TooManyRequests => McpErrorCodes.LimitReached,
                _ => McpErrorCodes.Unavailable,
            };
            throw new McpToolException(new McpError
            {
                Code = code,
                Message = $"The workflow endpoint returned HTTP {(int)response.StatusCode}.",
                Retryable = response.StatusCode == HttpStatusCode.TooManyRequests
                    || request.Method == HttpMethod.Get && (int)response.StatusCode >= 500,
            });
        }
        return await response.Content.ReadFromJsonAsync(type, ct)
            ?? throw new McpToolException(new McpError { Code = McpErrorCodes.Internal, Message = "The workflow endpoint returned an empty result." });
    }
}

internal sealed record NewWorkflowJob(string WorkItem, string WorkflowId, string? Instructions, decimal? LimitUsd, string? RunsOn);

internal sealed record WorkflowJobView(string Id, string State, JsonElement? Workflow, decimal SpentUsd,
    decimal? LimitUsd, IReadOnlyList<string>? ActiveStepIds, IReadOnlyList<JobStepRun>? Steps);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(NewWorkflowJob))]
[JsonSerializable(typeof(WorkflowJobView))]
internal sealed partial class WorkflowHttpJson : JsonSerializerContext;
