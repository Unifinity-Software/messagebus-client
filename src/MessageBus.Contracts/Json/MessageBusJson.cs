using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MessageBus.Contracts.Attachments;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Projects;
using MessageBus.Contracts.Releases;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using MessageBus.Contracts.Security;

namespace MessageBus.Contracts.Json;

/// <summary>
/// The one set of JSON settings every component uses for contract types: camelCase property names,
/// enums as their lowercase hyphenated names (pinned per member with
/// <see cref="JsonStringEnumMemberNameAttribute"/>), nulls omitted, a <c>kind</c> discriminator on
/// polymorphic types that may appear anywhere in the object, and source-generated metadata (no
/// reflection, so the trimmed single-file <c>mbus</c> works).
/// </summary>
/// <remarks>
/// A contract property with a declared default (<c>= []</c>, <c>= new()</c>, <c>= 5</c>) is
/// <c>{ get; set; }</c>, not <c>{ get; init; }</c>. The source generator builds a type with init-only
/// properties through one generated object initializer that assigns every init-only property, so a
/// property the JSON leaves out is overwritten with null or zero and its default is lost. A settable
/// property is assigned only when the JSON has it. Required properties stay <c>required … init</c>
/// (they are always present). <c>OmittedPropertyTests</c> checks every contract type.
/// </remarks>
public static class MessageBusJson
{
    /// <summary>
    /// Ready-to-use, read-only options that know every contract type. Use for anything outside
    /// ASP.NET Core: the CLI, the runner and tests.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    /// <summary>
    /// Applies the same settings to options someone else owns (ASP.NET Core's
    /// <c>ConfigureHttpJsonOptions</c>, the MCP SDK) and puts the contract metadata first in its
    /// resolver chain, keeping that owner's other resolvers for its own types.
    /// </summary>
    /// <param name="options">Options that aren't read-only yet.</param>
    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = null;
        options.PropertyNameCaseInsensitive = true;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        options.AllowOutOfOrderMetadataProperties = true;
        options.NumberHandling = JsonNumberHandling.AllowReadingFromString;
        options.TypeInfoResolverChain.Insert(0, MessageBusJsonContext.Default);
    }

    /// <summary>Serializes a contract value with <see cref="Options"/>.</summary>
    /// <typeparam name="T">A contract type.</typeparam>
    /// <param name="value">The value.</param>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T)));

    /// <summary>Deserializes a contract value with <see cref="Options"/>.</summary>
    /// <typeparam name="T">A contract type.</typeparam>
    /// <param name="json">The JSON text.</param>
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize(json, (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T)));

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(MessageBusJsonContext.Default.Options);
        options.MakeReadOnly();
        return options;
    }
}

/// <summary>Source-generated metadata for every contract type (see <see cref="MessageBusJson"/>).</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(Http.ServerCapabilitiesResponse))]
// Messaging
[JsonSerializable(typeof(MessageEnvelope))]
[JsonSerializable(typeof(MessageTarget))]
[JsonSerializable(typeof(RoleTarget))]
[JsonSerializable(typeof(AgentTarget))]
[JsonSerializable(typeof(CapabilityTarget))]
[JsonSerializable(typeof(InboxTarget))]
[JsonSerializable(typeof(MessageBody))]
[JsonSerializable(typeof(BodyRef))]
[JsonSerializable(typeof(Ref))]
[JsonSerializable(typeof(AttachmentRef))]
[JsonSerializable(typeof(EncryptionInfo))]
[JsonSerializable(typeof(SenderIdentity))]
[JsonSerializable(typeof(TraceContext))]
[JsonSerializable(typeof(ThreadSummary))]
[JsonSerializable(typeof(ThreadCopy))]
[JsonSerializable(typeof(SettleResult))]
[JsonSerializable(typeof(Usage))]
[JsonSerializable(typeof(ProgressUpdate))]
[JsonSerializable(typeof(JoinSpec))]
[JsonSerializable(typeof(ThreadEvent))]
[JsonSerializable(typeof(ThreadStatusChanged))]
[JsonSerializable(typeof(ThreadProgressed))]
[JsonSerializable(typeof(ThreadMessageAdded))]
[JsonSerializable(typeof(ThreadSettled))]
[JsonSerializable(typeof(ThreadMoved))]
[JsonSerializable(typeof(DeliveryInfo))]
[JsonSerializable(typeof(DeadLetter))]
[JsonSerializable(typeof(IReadOnlyList<DeadLetter>))]
[JsonSerializable(typeof(ReplayDeadLetterInput))]
// Machines
[JsonSerializable(typeof(AcceptsWorkFrom))]
[JsonSerializable(typeof(DetectedAgent))]
[JsonSerializable(typeof(MachineInventory))]
[JsonSerializable(typeof(MachineInfo))]
[JsonSerializable(typeof(RunIsolationReport))]
[JsonSerializable(typeof(AgentInfo))]
[JsonSerializable(typeof(AgentCapacityReport))]
[JsonSerializable(typeof(IReadOnlyList<AgentInfo>))]
// Security
[JsonSerializable(typeof(MachineEnrollRequest))]
[JsonSerializable(typeof(MachineEnrollResponse))]
[JsonSerializable(typeof(EnrollableWorkspace))]
[JsonSerializable(typeof(IReadOnlyList<EnrollableWorkspace>))]
[JsonSerializable(typeof(PublicKeyInfo))]
[JsonSerializable(typeof(PublishKeyRequest))]
[JsonSerializable(typeof(KeyGrant))]
[JsonSerializable(typeof(IReadOnlyList<PublicKeyInfo>))]
[JsonSerializable(typeof(IReadOnlyList<KeyGrant>))]
[JsonSerializable(typeof(KeyRotationState))]
[JsonSerializable(typeof(IReadOnlyList<KeyRotationState>))]
// Projects
[JsonSerializable(typeof(ProjectVisibility))]
[JsonSerializable(typeof(SourceRef))]
[JsonSerializable(typeof(ProjectTotals))]
[JsonSerializable(typeof(ProjectInfo))]
[JsonSerializable(typeof(ProjectActivity))]
// Roles
[JsonSerializable(typeof(DeliverySettings))]
[JsonSerializable(typeof(RetryBackoff))]
[JsonSerializable(typeof(RoleLimits))]
[JsonSerializable(typeof(AlertRule))]
[JsonSerializable(typeof(RoleAccess))]
[JsonSerializable(typeof(PermissionProfile))]
[JsonSerializable(typeof(RunEnvironment))]
[JsonSerializable(typeof(InstructionVersion))]
[JsonSerializable(typeof(AgentRole))]
[JsonSerializable(typeof(RoleJoinRequest))]
[JsonSerializable(typeof(DirectoryRole))]
// Runner
[JsonSerializable(typeof(DownloadLink))]
[JsonSerializable(typeof(TaskDelivery))]
[JsonSerializable(typeof(DeliveryDecision))]
[JsonSerializable(typeof(CancelDelivery))]
[JsonSerializable(typeof(DrainRequest))]
[JsonSerializable(typeof(RescanRequest))]
[JsonSerializable(typeof(ConnectAgentRequest))]
[JsonSerializable(typeof(ConnectAgentResult))]
[JsonSerializable(typeof(UpdateRequest))]
[JsonSerializable(typeof(RunnerReleasePolicy))]
[JsonSerializable(typeof(RunnerUpdateReport))]
[JsonSerializable(typeof(RunnerUpdateAbility))]
[JsonSerializable(typeof(RunnerUpdateProgress))]
[JsonSerializable(typeof(MachineUpdateStatus))]
[JsonSerializable(typeof(RunnerStatus))]
[JsonSerializable(typeof(SlotsRequest))]
[JsonSerializable(typeof(RunnerWorkPolicy))]
[JsonSerializable(typeof(RunnerHeartbeat))]
[JsonSerializable(typeof(HeartbeatAck))]
[JsonSerializable(typeof(LeaseState))]
[JsonSerializable(typeof(RunnerProgress))]
[JsonSerializable(typeof(RunnerSettle))]
[JsonSerializable(typeof(RunnerRepoCredentialRequest))]
[JsonSerializable(typeof(RunnerRepoCredential))]
[JsonSerializable(typeof(RunnerTelemetryCredentialRequest))]
[JsonSerializable(typeof(RunnerTelemetryCredential))]
[JsonSerializable(typeof(RunApprovalRequest))]
[JsonSerializable(typeof(ToolSetupPlan))]
[JsonSerializable(typeof(ToolSetupReport))]
[JsonSerializable(typeof(ToolSetupApprovalRequest))]
[JsonSerializable(typeof(ToolSetupApprovalState))]
[JsonSerializable(typeof(ToolSetupResultRequest))]
[JsonSerializable(typeof(RunApprovalState))]
[JsonSerializable(typeof(TokenPrice))]
[JsonSerializable(typeof(RunCostCap))]
// Attachments
[JsonSerializable(typeof(UploadLinkRequest))]
[JsonSerializable(typeof(UploadLink))]
[JsonSerializable(typeof(AttachSpec))]
[JsonSerializable(typeof(IReadOnlyList<DownloadLink>))]
// MCP
[JsonSerializable(typeof(McpCatalogFeatures))]
[JsonSerializable(typeof(McpError))]
[JsonSerializable(typeof(SealedBody))]
[JsonSerializable(typeof(SendInput))]
[JsonSerializable(typeof(SendResult))]
[JsonSerializable(typeof(SendManyInput))]
[JsonSerializable(typeof(SendManyResult))]
[JsonSerializable(typeof(WaitInput))]
[JsonSerializable(typeof(WaitResult))]
[JsonSerializable(typeof(WatchInput))]
[JsonSerializable(typeof(WatchResult))]
[JsonSerializable(typeof(InboxInput))]
[JsonSerializable(typeof(ReceivedMessage))]
[JsonSerializable(typeof(InboxResult))]
[JsonSerializable(typeof(FetchAttachmentInput))]
[JsonSerializable(typeof(FetchAttachmentResult))]
[JsonSerializable(typeof(ProgressInput))]
[JsonSerializable(typeof(ProgressResult))]
[JsonSerializable(typeof(SettleInput))]
[JsonSerializable(typeof(SettleOutput))]
[JsonSerializable(typeof(StatusInput))]
[JsonSerializable(typeof(StatusResult))]
[JsonSerializable(typeof(ThreadBrief))]
[JsonSerializable(typeof(MoveInput))]
[JsonSerializable(typeof(MoveThreadsResult))]
[JsonSerializable(typeof(CancelInput))]
[JsonSerializable(typeof(CancelResult))]
[JsonSerializable(typeof(FindProjectInput))]
[JsonSerializable(typeof(FindProjectResult))]
[JsonSerializable(typeof(EnsureProjectInput))]
[JsonSerializable(typeof(EnsureProjectResult))]
[JsonSerializable(typeof(CompleteProjectInput))]
[JsonSerializable(typeof(CompleteProjectResult))]
[JsonSerializable(typeof(DirectoryInput))]
[JsonSerializable(typeof(DirectoryResult))]
[JsonSerializable(typeof(DescribeRoleInput))]
[JsonSerializable(typeof(DescribeRoleResult))]
[JsonSerializable(typeof(SubscribeInput))]
[JsonSerializable(typeof(SubscribeResult))]
[JsonSerializable(typeof(RetryInput))]
[JsonSerializable(typeof(RequestApprovalInput))]
[JsonSerializable(typeof(WaitApprovalInput))]
[JsonSerializable(typeof(ApprovalResult))]
[JsonSerializable(typeof(OpenInboxInput))]
[JsonSerializable(typeof(OpenInboxResult))]
[JsonSerializable(typeof(RoleProposalOperation))]
[JsonSerializable(typeof(RoleProposalStatus))]
[JsonSerializable(typeof(RoleDefinitionV1))]
[JsonSerializable(typeof(RoleProposalRunEnvironment))]
[JsonSerializable(typeof(RoleProposalPermissionProfile))]
[JsonSerializable(typeof(RoleProposalModels))]
[JsonSerializable(typeof(RoleProposalsCapability))]
[JsonSerializable(typeof(SubmitRoleProposalRequest))]
[JsonSerializable(typeof(RoleProposalSubmitted))]
[JsonSerializable(typeof(RoleProposalBaseRole))]
[JsonSerializable(typeof(RoleProposalWarning))]
[JsonSerializable(typeof(RoleProposalReceipt))]
[JsonSerializable(typeof(RoleProposalDetail))]
[JsonSerializable(typeof(ProposeRoleInput))]
[JsonSerializable(typeof(GetRoleProposalInput))]
[JsonSerializable(typeof(WaitRoleProposalInput))]
public sealed partial class MessageBusJsonContext : JsonSerializerContext;
