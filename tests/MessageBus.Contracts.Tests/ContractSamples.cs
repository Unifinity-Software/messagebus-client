using System.Text.Json;
using MessageBus.Contracts.Attachments;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Projects;
using MessageBus.Contracts.Releases;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;
using MessageBus.Contracts.Security;

namespace MessageBus.Contracts.Tests;

/// <summary>
/// One fully populated instance of every contract type: every property set, every list non-empty.
/// The round-trip test checks that nothing is missing, so a new property or type has to be added here.
/// They double as worked examples of each type.
/// </summary>
internal static class ContractSamples
{
    private static readonly DateTimeOffset T = new(2026, 11, 13, 9, 30, 0, TimeSpan.Zero);
    private const string Ws = "w101";
    private const string Thr = "thr_01jcz7m2v1g3x4y5z6a7b8c9d0";
    private const string Msg = "msg_01jcz7m2v1g3x4y5z6a7b8c9d1";
    private const string Prj = "prj_01jcz7m2v1g3x4y5z6a7b8c9d2";
    private const string Agt = "agt_01jcz7m2v1g3x4y5z6a7b8c9d3";
    private const string Mch = "mch_01jcz7m2v1g3x4y5z6a7b8c9d4";
    private const string Dlv = "dlv_01jcz7m2v1g3x4y5z6a7b8c9d5";
    private const string Att = "att_01jcz7m2v1g3x4y5z6a7b8c9d6";
    private const string Subject = "microsoft:00000000-0000-0000-0000-000000000001";
    private const string Sha = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static readonly JsonElement Data = JsonDocument.Parse("""{"area":"api","files":["TodoItem.cs"]}""").RootElement.Clone();

    public static readonly RoleTarget RoleTo = new() { Role = "developer" };
    private static readonly ExhibitRef Exhibit = new()
    {
        Name = "Walkthrough", MediaType = "video/mp4", Size = 4000, Caption = "Creating a task",
    };
    private static readonly StepInput ReviewInput = new() { FromStep = "code", ThreadId = Thr, What = "branch" };
    private static readonly StepContext ReviewContext = new()
    {
        JobId = "job_demo", StepId = "review", StepName = "Review", Round = 2,
        AllowedOutcomes = ["approved", "changes-needed"], Inputs = [ReviewInput],
    };
    private static readonly RunCommandParameters CommandAction = new() { Command = "dotnet build" };
    private static readonly OpenPullRequestParameters PullRequestAction = new() { BranchFrom = "code", Base = "main", AutoMerge = true };
    private static readonly RecordWalkthroughParameters WalkthroughAction = new() { Url = "https://example.com/demo" };
    private static readonly RollBackParameters RollBackAction = new() { DeploymentId = "deployment_demo" };
    private static readonly RunnerAction Action = new()
    {
        Kind = RunnerActionKinds.OpenPullRequest, RunCommand = CommandAction, OpenPullRequest = PullRequestAction,
        RecordWalkthrough = WalkthroughAction, RollBack = RollBackAction,
    };
    public static readonly Ref PrRef = new()
    {
        Kind = RefKinds.GitHubPullRequest, Url = "https://github.com/acme/todo/pull/42", Repo = "acme/todo",
        Number = 42, Sha = "a1b2c3d", Path = "feature/due-dates", Id = "PR_kwDO", Title = "Add due dates",
    };
    public static readonly EncryptionInfo Enc = new()
    {
        Algorithm = EncryptionAlgorithms.Aes256Gcm, KeyWrapAlgorithm = EncryptionAlgorithms.EcdhEsA256Kw,
        KeyId = "ek_01jcz7m2v1g3x4y5z6a7b8c9d7", WrappedKey = Convert.ToBase64String(new byte[32]), Nonce = "AAECAwQFBgcICQoL",
        EphemeralPublicKey = "MFkwEwYHKoZIzj0CAQ==",
    };
    public static readonly SenderIdentity Sender = new()
    {
        Kind = SenderKind.Agent, SubjectId = Subject, DisplayName = "PC · Claude Code", AgentId = Agt,
        MachineId = Mch, ApiKeyId = "key_01jcz7m2v1g3x4y5z6a7b8c9d8", Role = "architect",
    };
    public static readonly BodyRef Body = new()
    {
        SizeBytes = 1234, Sha256 = Sha, ContentType = MessageLimits.BodyContentType, Data = "c2VhbGVk",
        BlobName = $"{Ws}/{Thr}/{Msg}",
    };
    public static readonly AttachmentRef Attachment = new()
    {
        Id = Att, Name = "due-dates.bundle", Format = AttachmentFormat.GitBundle, SizeBytes = 20_971_520,
        Sha256 = Sha, ContentType = "application/x-git-bundle", Branch = "feature/due-dates", BaseCommit = "0f1e2d3", Encryption = Enc,
    };
    public static readonly FrictionNote Friction = new() { Kind = "missing-context", Note = "The task did not name the API version." };
    public static readonly MessageBody PlainBody = new() { Summary = "Add a due date to todo items.", Details = "API and UI.", Data = Data, Friction = [Friction] };
    public static readonly ProgressRequest CodeRequest = new() { Kind = ProgressRequestKinds.GitHistory, AttachmentId = Att, ReplyTo = new InboxTarget { InboxId = "inb_01jcz7m2v1g3x4y5z6a7b8c9dm" } };
    public static readonly ProgressUpdate Progress = new() { Message = "Running tests", Percent = 60, At = T, Request = CodeRequest };
    public static readonly Usage RunUsage = new()
    {
        AgentKind = AgentKind.Codex, Vendor = "openai", Model = "gpt-5-codex", TokensIn = 182_000, CachedTokensIn = 150_000, TokensOut = 9_400,
        EstimatedCostUsd = 0.42m, ActiveSeconds = 412.5,
        Models = [new Usage { Model = "gpt-5-codex", TokensIn = 182_000, CachedTokensIn = 150_000, TokensOut = 9_400 }],
    };
    public static readonly SettleResult Result = new()
    {
        Status = SettleStatus.Succeeded, Summary = "Added DueDate and tests.", Refs = [PrRef, new Ref { Kind = RefKinds.Exhibit, Exhibit = Exhibit }], Usage = RunUsage,
        Outcome = "approved", ExitCode = 0, FailureCategory = "refused", Error = "none", FrictionKinds = ["missing-context"],
    };
    public static readonly JoinSpec Join = new() { Mode = WaitMode.All, ThreadIds = [Thr], To = RoleTo, TimeoutSeconds = 3600 };

    public static readonly VerificationRequest RequiredVerification = new() { Environment = "frontend", RequiredChecks = ["build"] };
    public static readonly VerificationCheck BuildCheck = new() { Name = "build", State = VerificationState.Failed, Reason = "Nonzero exit." };
    public static readonly VerificationResult Verification = new()
    {
        State = VerificationState.Failed, Environment = "frontend", Reason = "Build failed.",
        Revision = new string('a', 40), Checks = [BuildCheck],
    };
    public static readonly VerificationSnapshot VerificationDefinition = new()
    {
        Environment = "frontend", Recipe = "node-npm-ci", Version = "1", Digest = new string('b', 64),
        NodeVersion = "24.1.0", NpmVersion = "11.3.0", Repository = "gh:acme/api", Remote = "https://github.com/acme/api.git",
        Revision = new string('a', 40), BaseRevision = new string('c', 40), RequiredChecks = ["build"],
    };

    public static readonly MessageEnvelope Envelope = new()
    {
        Verification = RequiredVerification,
        SpecVersion = "1.0", Id = Msg, Type = "task.implement", Source = $"agent://{Ws}/{Agt}", Time = T, WorkspaceId = Ws,
        Target = RoleTo, ReplyTo = new InboxTarget { InboxId = "inb_01jcz7m2v1g3x4y5z6a7b8c9d9" }, ThreadId = Thr,
        ParentId = "msg_01jcz7m2v1g3x4y5z6a7b8c9da", ProjectId = Prj, Title = "Add due dates", Labels = new Dictionary<string, string> { ["area"] = "api" },
        Refs = [PrRef], Expect = Expect.Outcome, Priority = Priority.High, DedupeKey = "due-dates-api", ExpiresAt = T.AddDays(1),
        HopCount = 1, Trace = new TraceContext { TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01", TraceState = "mbus=1" },
        Sender = Sender, Body = Body, Attachments = [Attachment], Encryption = Enc, ExcludeAgents = ["agt_01jcz7m2v1g3x4y5z6a7b8c9dq"], Model = "claude-fable-5-2",
    };

    public static readonly ThreadCopy Copy = new()
    {
        DeliveryId = Dlv, AgentId = Agt, MachineId = Mch, Status = ThreadStatus.Failed, Attempts = 1, Result = Result, LastError = "Not logged in",
    };

    public static readonly ThreadSummary Thread = new()
    {
        Verification = Verification, CurrentFrictionKinds = ["build-or-test"],
        ThreadId = Thr, WorkspaceId = Ws, ProjectId = Prj, ExecutionOwnerSubjectId = Subject, ParentThreadId = "thr_01jcz7m2v1g3x4y5z6a7b8c9db",
        ChildThreadIds = ["thr_01jcz7m2v1g3x4y5z6a7b8c9dc"], RootMessageId = Msg, Type = "task.implement", Title = "Add due dates",
        Target = RoleTo, Sender = Sender, Expect = Expect.Outcome, Status = ThreadStatus.Settled, CreatedAt = T, UpdatedAt = T.AddMinutes(9),
        CompletedAt = T.AddMinutes(9), AgentId = Agt, MachineId = Mch, Attempts = 1, HopCount = 1, MessageCount = 3, LastProgress = Progress,
        Result = Result, LastError = "lease expired", DeadLetterReason = DeadLetterReason.MaxAttempts, Join = Join, Deadline = T.AddHours(2),
        Copies = [Copy], RequestedModel = "claude-opus-5-5",
        HandledAt = T.AddMinutes(10), HandledBy = "Ada", HandledReason = "Wrong role", CanHandle = true, FailureCategory = "setup",
    };

    public static readonly DeliveryInfo Delivery = new()
    {
        DeliveryId = Dlv, MessageId = Msg, ThreadId = Thr, AgentId = Agt, MachineId = Mch, Role = "developer",
        Attempt = 2, Status = DeliveryStatus.Leased, LeaseExpiresAt = T.AddMinutes(30),
    };

    public static readonly DetectedAgent Detected = new()
    {
        Kind = AgentKind.ClaudeCode, Found = true, Version = "2.1.0", OnPath = true, ExecutablePath = "/opt/homebrew/bin/claude",
        McpConfigured = true, SignedIn = true, Vendor = "anthropic", NotReady = "Claude Code can't use its sign-in in this SSH session.",
        DefaultModel = "claude-opus-5-5", Models = ["claude-opus-5-5", "claude-fable-5-2"], ModelsPartial = true,
    };
    public static readonly RunIsolationReport Isolation = new()
    {
        Summary = "OS sandbox: runs can't reach your personal files or other runs.", Credentials = "A token for the task's repository only.",
        Limitations = ["Network: not limited by run isolation."], Level = "os-sandbox", Network = "enforced",
    };
    public static readonly MachineDeployment Deployment = new() { Target = DeploymentTarget.AzureContainerInstances,
        AzureResourceId = "/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/prototype/providers/Microsoft.ContainerInstance/containerGroups/runner" };
    public static readonly MachineInventory Inventory = new()
    {
        MachineId = Mch, HostName = "mac-mini", Os = MachineOs.MacOs, OsVersion = "15.1", Architecture = CpuArchitecture.Arm64, Target = DeploymentTarget.Pc,
        Deployment = Deployment, Location = "Mac mini", CliVersion = "0.1.0", Slots = 2, SlotCeiling = 4, SlotMachineMax = 10, CpuCount = 10, MemoryBytes = 16L << 30,
        RoleKeysReadable = new Dictionary<string, bool> { ["developer"] = true },
        Capabilities = ["os:macos", "tool:dotnet"], Agents = [Detected], Isolation = Isolation, LastUpdate = UpdateReportSample, Updates = UpdateAbilitySample, ScannedAt = T,
    };
    public static readonly AcceptsWorkFrom Accepts = new() { Mode = AcceptsWorkFromMode.OwnProjectsAndGroups, GroupIds = ["grp-1"] };
    private static readonly RunnerCpuSample ResourceCpu = new()
        { UtilizationPercent = 37.5, EffectiveCpus = 2.5, HostLogicalCpus = 8, LimitSource = ResourceLimitSource.CgroupV2 };
    private static readonly RunnerMemorySample ResourceMemory = new()
        { TotalBytes = 4L << 30, AvailableBytes = 1L << 30, HostTotalBytes = 8L << 30, LimitSource = ResourceLimitSource.CgroupV2 };
    private static readonly RunnerProcessTreeSample ResourceTree = new()
        { CpuPercent = 10, ResidentBytes = 32L << 20, ProcessCount = 2, Reason = "process-exited" };
    private static readonly RunnerResourceSample ResourceSample = new()
    {
        Sequence = 3, SampledAt = T, WindowMs = 15000, Status = ResourceSampleStatus.Partial, Reason = "process-exited",
        Cpu = ResourceCpu, Memory = ResourceMemory, Runner = ResourceTree, Tasks = ResourceTree,
    };
    private static readonly MachineResourceView Resources = new()
    {
        Sample = ResourceSample, ReceivedAt = T.AddSeconds(1), AgeSeconds = 2, State = MachineResourceState.Elevated,
        Reasons = ["memory-pressure"], SlotsTotal = 2, SlotsBusy = 1,
    };
    public static readonly MachineInfo Machine = new()
    {
        Resources = Resources, MachineId = Mch, WorkspaceId = Ws, OwnerSubjectId = Subject, Kind = MachineKind.Personal, Name = "mac-mini", Location = "Mac mini",
        Os = MachineOs.MacOs, Architecture = CpuArchitecture.Arm64, Target = DeploymentTarget.Docker, CliVersion = "0.1.0", Slots = 2, SlotsBusy = 1,
        Deployment = Deployment, SlotSetting = 2, SlotCeiling = 4, SlotMachineMax = 10, CpuCount = 10, MemoryBytes = 16L << 30,
        Capabilities = ["os:macos"], CustomCapabilities = ["tier:gpu"], Agents = [Detected], Isolation = Isolation, LastUpdate = UpdateReportSample, Updates = UpdateAbilitySample, Update = UpdateStatusSample, AcceptsWorkFrom = Accepts, RepoCredentialMode = RepoCredentialMode.KeychainToken,
        DailySpendCeilingUsd = 20m, KeyFingerprint = Sha, Online = true, PendingApproval = true, EnrolledAt = T, LastSeenAt = T, RunnerSeenAt = T,
    };
    public static readonly CapacityWindow CapacityWindowSample = new()
        { Window = CapacityNames.Weekly, WindowMinutes = 10080, UsedPercent = 97, Status = CapacityNames.Warning, ResetsAt = T.AddDays(1), MeasuredAt = T };
    public static readonly CapacityLimitHit CapacityHit = new() { Window = CapacityNames.Weekly, At = T, ResetsAt = T.AddDays(1) };
    public static readonly AgentCapacity CapacitySample = new() { Windows = [CapacityWindowSample], LimitHit = CapacityHit, UpdatedAt = T };
    public static readonly AgentCapacityReport CapacityReport = new() { AgentKind = "codex", Windows = [CapacityWindowSample], LimitHit = CapacityHit };
    public static readonly AgentInfo AgentRecord = new()
    {
        AgentId = Agt, MachineId = Mch, OwnerSubjectId = Subject, Kind = AgentKind.ClaudeCode, Version = "2.1.0", Vendor = "anthropic",
        DisplayName = "Mac mini · Claude Code", Location = "Mac mini", Roles = ["developer"], UnreadableRoles = ["developer"], Presence = AgentPresence.Online,
        SlotsFree = 1, LastSeenAt = T, Capabilities = ["agent:claude-code", "os:macos", "tool:dotnet"], NotReady = "git doesn't work.",
        Resources = Resources, Capacity = CapacitySample, DefaultModel = "claude-opus-5-5", Models = ["claude-opus-5-5", "claude-fable-5-2"], ModelsPartial = true,
    };

    public static readonly ProjectTotals Totals = new()
    {
        Friction = new Dictionary<string, long> { ["missing-context"] = 1 },
        FrictionByRole = new Dictionary<string, IReadOnlyDictionary<string, long>> { ["developer"] = new Dictionary<string, long> { ["missing-context"] = 1 } },
        Tasks = 3, TasksDone = 2, TasksWorking = 1, TasksFailed = 1, TasksCancelled = 1, TasksFailedUnhandled = 1, TasksRedone = 1, TasksWaitingOnMachine = 1, WallSeconds = 1800, AgentSeconds = 950.5, TokensIn = 400_000,
        TokensOut = 20_000, EstimatedCostUsd = 1.25m, PullRequests = 1, Agents = [Agt], Machines = [Mch], Vendors = ["openai"], Models = ["gpt-5-codex"],
    };
    public static readonly SourceRef Source = new() { System = SourceRef.AzureDevOps, Path = "contoso/web", Item = "123" };
    public static readonly ProjectVisibility Visibility = new() { Mode = ProjectVisibilityMode.Listed, SubjectIds = [Subject], GroupIds = ["grp-1"] };
    public static readonly ProjectActivity Activity = new()
    {
        ThreadId = Thr, Status = ThreadStatus.Settled, SentAt = T,
        StartedAt = T.AddMinutes(1), EndedAt = T.AddMinutes(2),
    };
    public static readonly ProjectCoordinator Coordinator = new()
    {
        AgentKind = AgentKind.Codex, AgentId = Agt, MachineId = Mch, CheckedInAt = T,
    };
    public static readonly ProjectInfo Project = new()
    {
        Coordinator = Coordinator, State = ProjectState.Working, NeedsYou = [ProjectNeed.Approval], PendingApprovals = 1,
        ExecutionScope = ProjectExecutionScope.OwnerRunners, ExecutionOwnerSubjectId = Subject,
        ProjectId = Prj, WorkspaceId = Ws, Key = "ado:contoso/web#123", Title = "Add due dates", Source = Source, Refs = [PrRef],
        StartedBy = Sender, Status = ProjectStatus.Active, Visibility = Visibility, BudgetCapUsd = 25m, MaxFanOut = 4, Summary = "Done.",
        CreatedAt = T, CompletedAt = T.AddHours(1), ArchivedAt = T.AddHours(2), LastActivityAt = T.AddMinutes(50), Totals = Totals, Activity = [Activity],
    };

    public static readonly DeliverySettings DeliverySettings = new() { Mode = DeliveryMode.Broadcast, Count = 2, DistinctVendors = true, Replies = ReplyMode.FirstReply, PreferredAgentKinds = [AgentKind.Codex, AgentKind.ClaudeCode], AgentKindWaitSeconds = 45 };
    public static readonly AlertRule Alert = new()
    {
        RuleId = "alr_01jcz7m2v1g3x4y5z6a7b8c9dd", Name = "Developer backlog", Role = "developer", ProjectId = Prj,
        Metric = AlertMetric.QueueDepth, Threshold = 10, WindowSeconds = 600, DestinationIds = ["dst-1"], Enabled = true,
    };
    public static readonly PermissionProfile Profile = new()
    {
        Name = PermissionProfiles.Worker, AllowedTools = ["Edit", "Bash(dotnet test)"], Network = NetworkAccess.Allowlist,
        AllowedHosts = ["api.nuget.org"], CanEditFiles = true, CanRunCommands = true, CanPush = true, CanRunTests = true,
    };
    public static readonly RunEnvironment RunEnv = new()
    {
        CodexOnWindows = CodexWindowsSandbox.SameAsOtherAgents, KeepAgentTools = ["playwright"],
        Checkout = CheckoutMode.Clone, SetupCommand = "dotnet restore", TimeoutSeconds = 1800,
        Logs = LogCapture.Upload, PermissionProfile = Profile,
        Models = new RoleModels
        {
            Defaults = [new AgentModelDefault { Agent = AgentKind.ClaudeCode, Model = "claude-opus-5-5" }],
            Allowed = ["claude-opus-5-5", "claude-fable-5-2"],
        },
    };
    public static readonly InstructionVersion Instructions = new()
    {
        Role = "developer", Version = 3, Content = "# Developer\nImplement the task.", ContentSha256 = Sha, Source = InstructionSource.Repo,
        SourceRef = "acme/todo@a1b2c3d", SignedBy = Subject, SigningKeyId = "ek_01jcz7m2v1g3x4y5z6a7b8c9de", Signature = "c2ln", CreatedAt = T, Live = true,
    };
    public static readonly RoleLimits Limits = new()
    {
        MaxAttempts = 3, LeaseTimeoutSeconds = 900, HeartbeatSeconds = 30, MessageLifetimeSeconds = 7200,
        RetryBackoff = new RetryBackoff { Kind = BackoffKind.Fixed, InitialSeconds = 10, MaxSeconds = 10 },
        DefaultPriority = Priority.Low, DedupeWindowSeconds = 3600, MaxAttachmentBytes = 50_000_000,
        DailyBudgetUsd = 25m, WhenBudgetReached = BudgetAction.Economy, MaxConcurrentRuns = 4, MaxFanOutPerThread = 8,
        MaxCostPerRunUsd = 2m, EconomyModel = "claude-haiku-4-5",
    };
    public static readonly AgentRole Role = new()
    {
        Name = "developer", WorkspaceId = Ws, DisplayName = "Developer", Description = "Writes code.", BuiltIn = true,
        Delivery = DeliverySettings, RequiredCapabilities = ["tool:dotnet"], Limits = Limits, AlertRules = [Alert],
        Access = new RoleAccess
        {
            Owners = [Subject], RequireApprovalToJoin = true, AllowedSenders = ["architect"], JoinApprovers = JoinApprovers.Group,
            JoinApproverGroupId = "8d0f6c1e-5b8a-4f0e-9d4c-2a7b3c9e1f00", RequireRunApproval = true,
        },
        RunEnvironment = RunEnv, GenerationId = "gen_original", LegacyInstructionCutoff = 2, LiveInstructionVersion = 3, CreatedAt = T, UpdatedAt = T,
    };
    public static readonly DirectoryRole DirectoryEntry = new()
    {
        Name = "developer", DisplayName = "Developer", Description = "Writes code.", BuiltIn = true, Delivery = DeliverySettings,
        RequiredCapabilities = ["tool:dotnet"], Agents = [AgentRecord], AgentsOnline = 1, AgentsFree = 1, FreeSlots = 1, QueueDepth = 2,
    };
    private static readonly VerificationRecipe FrontendRecipe = new()
    {
        Id = "node-npm-ci", Revision = 1, NodeMajor = 24, Checks = ["test", "typecheck", "build"],
    };

    private static readonly VerificationEnvironmentApproval FrontendApproval = new()
    {
        Id = "frontend", Version = JsonSerializer.SerializeToElement("v1"), RecipeId = FrontendRecipe.Id,
        RecipeRevision = FrontendRecipe.Revision, ApprovedSharedMachineCount = 1,
    };

    private static readonly VerificationDirectory VerificationRegistry = new()
    {
        RegistryVersion = 1, RequiredTaskRequestsSupported = true, Recipes = [FrontendRecipe], Environments = [FrontendApproval],
    };

    public static readonly TokenPrice Price = new()
    {
        InputUsdPerMillion = 3m, OutputUsdPerMillion = 15m, CachedInputUsdPerMillion = 0.3m, CacheWriteUsdPerMillion = 3.75m, CacheWrite1hUsdPerMillion = 6m,
    };
    public static readonly RunCostCap CostCap = new() { MaxUsd = 2m, Prices = new Dictionary<string, TokenPrice> { ["claude-sonnet-4-5"] = Price } };
    public static readonly DownloadLink Link = new() { Id = Att, Url = "https://127.0.0.1:10000/devstoreaccount1/attachments/w101/att?sig=x", ExpiresAt = T.AddMinutes(30) };
    public static readonly SealedBody Sealed = new() { Body = Body, Encryption = Enc };
    public static readonly AttachSpec AttachSpec = new()
    {
        Format = AttachmentFormat.GitBundle, Path = ".", Branch = "feature/due-dates", Base = "main",
        ExpectedHead = "0123456789abcdef0123456789abcdef01234567",
    };

    public static readonly SendInput Send = new()
    {
        Verification = RequiredVerification,
        To = RoleTo, Type = "task.implement", Title = "Add due dates", Body = PlainBody, SealedBody = Sealed, ProjectId = Prj, ThreadId = Thr,
        ParentThreadId = "thr_01jcz7m2v1g3x4y5z6a7b8c9df", SourceThreadId = Thr, ParentId = Msg, Expect = Expect.Outcome, ReplyTo = new AgentTarget { AgentId = Agt },
        Priority = Priority.High, DedupeKey = "due-dates-api", ExpiresInSeconds = 3600, DeadlineSeconds = 7200,
        Labels = new Dictionary<string, string> { ["area"] = "api" }, Refs = [PrRef], Attach = [AttachSpec], Attachments = [Attachment],
        ExcludeAgents = [Agt], ExcludeAuthors = true, Model = "claude-fable-5-2",
    };
    public static readonly SendResult Sent = new()
    {
        MessageId = Msg, ThreadId = Thr, ProjectId = Prj, Status = ThreadStatus.Queued, Deduplicated = true, AvailableAgents = 2, AgentsOnline = 2, AgentsFree = 1, QueuePosition = 3,
        ExcludedAgents = [Agt], Notice = "Sent 1 copy instead of 2: only 1 agent outside the excluded ones holds the role.",
    };
    public static readonly ReceivedMessage Received = new() { Envelope = Envelope, Body = PlainBody, Delivery = Delivery, Links = [Link], Decrypted = true };

    public static readonly ThreadBrief Brief = new()
    {
        Verification = Verification, CurrentFrictionKinds = ["build-or-test"],
        ThreadId = Thr, Title = "Add due dates", Type = "task.implement", To = "role:developer", Status = ThreadStatus.Settled,
        ParentThreadId = "thr_01jcz7m2v1g3x4y5z6a7b8c9db", AgentId = Agt, MachineId = Mch, Attempts = 1, UpdatedAt = T,
        Outcome = SettleStatus.Succeeded, Summary = "Added due dates.", RequestedModel = "claude-opus-5-5", Usage = RunUsage, LastError = "first attempt timed out",
    };


    /// <summary>Every sample, keyed by its type.</summary>
    public static IReadOnlyDictionary<Type, object> All { get; } = new object[]
    {
        Envelope,
        RoleTo,
        new AgentTarget { AgentId = Agt },
        new CapabilityTarget { Capabilities = ["os:macos", "tool:dotnet"] },
        new InboxTarget { InboxId = "inb_01jcz7m2v1g3x4y5z6a7b8c9d9" },
        PlainBody,
        Friction,
        Body,
        PrRef with { Kind = RefKinds.Exhibit, Exhibit = Exhibit },
        Attachment,
        Enc,
        Sender,
        Envelope.Trace!,
        Thread,
        Copy,
        Result,
        RunUsage,
        Progress,
        CodeRequest,
        Join,
        new ThreadStatusChanged { EventId = "evt_1", ThreadId = Thr, ProjectId = Prj, At = T, Status = ThreadStatus.Working, Reason = "picked up", AgentId = Agt, MachineId = Mch },
        new ThreadProgressed { EventId = "evt_2", ThreadId = Thr, ProjectId = Prj, At = T, Progress = Progress },
        new ThreadMessageAdded { EventId = "evt_3", ThreadId = Thr, ProjectId = Prj, At = T, MessageId = Msg, Type = "answer", Title = "Re: due dates", Sender = Sender, Retarget = new RetargetMetadata { PreviousMessageId = Msg, From = RoleTo, To = new AgentTarget { AgentId = Agt }, Reason = "Wrong role" } },
        new ThreadSettled { EventId = "evt_4", ThreadId = Thr, ProjectId = Prj, At = T, Result = Result },
        new ThreadMoved
        {
            EventId = "evt_5", ThreadId = Thr, ProjectId = Prj, At = T, FromProjectId = "prj_01jcz7m2v1g3x4y5z6a7b8c9d3", FromTitle = "Quality loop 1",
            ToProjectId = Prj, ToTitle = "Quality loop 2", MovedBy = "Ada", Reason = "Sent to the wrong project.",
        },
        Delivery,
        new DeadLetter { Id = "dlq-1", Envelope = Envelope, Role = "developer", Reason = DeadLetterReason.NoHolder, LastError = "no agent", Attempts = 5, LastAgentId = Agt, LastMachineId = Mch, DeadLetteredAt = T, ExpiresAt = T.AddDays(14) },
        new ReplayDeadLetterInput { ToRole = "tester", SealedBody = Sealed, Attachments = [Attachment] },
        Accepts,
        Detected,
        Isolation,
        Deployment,
        Inventory,
        Machine,
        AgentRecord,
        CapacityWindowSample,
        CapacityHit,
        CapacitySample,
        CapacityReport,
        new MachineEnrollRequest { WorkspaceId = Ws, PublicKey = "MFkwEwYHKoZIzj0CAQ==", Name = "mac-mini", Location = "Mac mini", Inventory = Inventory, DeviceCompliant = true, EntraDeviceId = "0b6f5c2e-1d3a-4c8e-9f70-2a4b6d8e0c11", DeviceProof = DeviceProofSample },
        DeviceProofSample,
        new MachineEnrollResponse { Machine = Machine, WorkspaceName = "Contoso", MinCliVersion = "0.1.0" },
        new EnrollableWorkspace { WorkspaceId = Ws, Name = "Contoso", Role = "admin" },
        new PublicKeyInfo
        {
            KeyId = "ek_01jcz7m2v1g3x4y5z6a7b8c9d7", Scope = KeyScope.Role, Owner = "developer", Version = 2, Algorithm = "P-256",
            PublicKey = "MFkwEwYHKoZIzj0CAQ==", Fingerprint = Sha, CreatedAt = T, RetiredAt = T.AddDays(30), ExpiresAt = T.AddDays(60),
            SigningKeyId = "ek_01jcz7m2v1g3x4y5z6a7b8c9de", Signature = "c2ln",
        },
        new PublishKeyRequest
        {
            Scope = KeyScope.Role, Owner = "developer", PublicKey = "MFkwEwYHKoZIzj0CAQ==", SigningKeyId = "ek_01jcz7m2v1g3x4y5z6a7b8c9de", Signature = "c2ln",
        },
        new KeyAccessLossInfo { MachineId = Mch, Name = "mac-mini", Reason = KeyAccessLoss.LeftRole },
        new KeyRotationState
        {
            Scope = KeyScope.Role, Owner = "developer", RotationNeeded = true,
            Key = new PublicKeyInfo
            {
                KeyId = "ek_01jcz7m2v1g3x4y5z6a7b8c9d7", Scope = KeyScope.Role, Owner = "developer", Version = 2, Algorithm = "P-256",
                PublicKey = "MFkwEwYHKoZIzj0CAQ==", Fingerprint = Sha, CreatedAt = T, RetiredAt = T.AddDays(30), ExpiresAt = T.AddDays(60),
                SigningKeyId = "ek_01jcz7m2v1g3x4y5z6a7b8c9de", Signature = "c2ln",
            },
            MustLoseAccess = [new KeyAccessLossInfo { MachineId = Mch, Name = "mac-mini", Reason = KeyAccessLoss.MachineRemoved }],
            Holders = [Mch],
        },
        new KeyGrant
        {
            GrantId = "grt_01jcz7m2v1g3x4y5z6a7b8c9dh", Role = "developer", KeyVersion = 2, MachineId = Mch, SealedPrivateKey = "c2VhbGVk",
            GrantedBy = Subject, SigningKeyId = "ek_01jcz7m2v1g3x4y5z6a7b8c9de", Signature = "c2ln", CreatedAt = T, RevokedAt = T.AddDays(1),
        },
        Visibility,
        Source,
        Totals,
        Activity,
        Coordinator,
        Project,
        DeliverySettings,
        Limits.RetryBackoff,
        Limits,
        Alert,
        Role.Access,
        Profile,
        RunEnv,
        RunEnv.Models,
        RunEnv.Models.Defaults[0],
        Instructions,
        Role,
        ProposalDefinition,
        ProposalDefinition.RunEnvironment!,
        ProposalDefinition.RunEnvironment!.PermissionProfile!,
        ProposalDefinition.RunEnvironment!.Models!,
        new RoleProposalsCapability { Version = 1, BaselineVersion = "role-proposal-readonly-v1", NormalizerVersion = "role-definition-v1" },
        new SubmitRoleProposalRequest { Operation = RoleProposalOperation.Create, Definition = ProposalDefinition, Rationale = "Need a reviewer", IdempotencyKey = "retry-example", AgentId = Agt, CoordinatorRecordId = "coord_example", InboxId = "inb_example" },
        new RoleProposalSubmitted { ProposalId = "rpr_example", Status = RoleProposalStatus.Pending, ExpiresAt = T, ReviewUrl = "https://example.com/review" },
        ProposalBase,
        new RoleProposalWarning { Code = "runtime-enforcement-dependent", Field = "runEnvironment", Severity = "warning", Message = "Runtime enforcement depends on the runner." },
        ProposalReceipt,
        new RoleProposalDetail { ProposalId = "rpr_example", Status = RoleProposalStatus.Approved, Operation = RoleProposalOperation.Update,
            CoordinatorRecordId = "coord_example", InboxId = "inb_example", AgentId = Agt, MachineId = Mch, ExpiresAt = T, ReviewUrl = "https://example.com/review",
            Rationale = "Need a reviewer", BaseDefinition = ProposalDefinition, RequestedDefinition = ProposalDefinition, CurrentDefinition = ProposalDefinition,
            FinalDefinition = ProposalDefinition, Warnings = [new RoleProposalWarning { Code = "runtime-enforcement-dependent", Field = "runEnvironment", Severity = "warning", Message = "Runtime enforcement depends on the runner." }],
            BaseRole = ProposalBase, Receipt = ProposalReceipt, DecidedBy = Subject, DecidedAt = T, Comment = "Reviewed" },
        new ProposeRoleInput { Operation = RoleProposalOperation.Create, Definition = ProposalDefinition, Rationale = "Need a reviewer", IdempotencyKey = "retry-example" },
        new GetRoleProposalInput { ProposalId = "rpr_example" },
        new WaitRoleProposalInput { ProposalId = "rpr_example", TimeoutSeconds = 10 },
        new RoleJoinRequest
        {
            RequestId = "rjr_01jcz7m2v1g3x4y5z6a7b8c9dj", Role = "developer", AgentId = Agt, MachineId = Mch, RequestedBy = Subject,
            Status = JoinRequestStatus.Approved, RequestedAt = T, DecidedBy = Subject, DecidedAt = T.AddMinutes(5),
        },
        DirectoryEntry,
        Link,
        RequiredVerification,
        BuildCheck,
        Verification,
        VerificationDefinition,
        Exhibit,
        ReviewInput,
        ReviewContext,
        CommandAction,
        PullRequestAction,
        WalkthroughAction,
        RollBackAction,
        Action,
        new TaskDelivery
        {
            Verification = VerificationDefinition, StepContext = ReviewContext, RunnerAction = Action,
            DeliveryId = Dlv, Attempt = 1, LeaseExpiresAt = T.AddMinutes(30), AgentId = Agt, Envelope = Envelope, Role = "developer",
            Instructions = Instructions, RunEnvironment = RunEnv, HeartbeatSeconds = 60, Links = [Link], ProjectKey = "ado:contoso/web#123", ProjectOwner = Subject, InboxOwner = Subject,
            RequireRunApproval = true, Model = "claude-haiku-4-5", EconomyModel = true, CostCap = CostCap,
        },
        new DeliveryDecision { Accepted = false, Reason = DeclineReason.NotAccepted, Detail = "own projects only", Result = Result },
        new CancelDelivery { DeliveryId = Dlv, ThreadId = Thr, Reason = "project cancelled" },
        new DrainRequest { Reason = "update", Deadline = T.AddHours(1), ManagedDrainId = "aci-operation", Resume = true },
        new RescanRequest { RequestedBy = Subject },
        new ConnectAgentRequest { Agent = AgentKind.Codex, RequestedBy = "Ada Lovelace" },
        new ConnectAgentResult { Status = ConnectAgentStatus.NeedsTerminal, Message = "Run mbus connect codex on build-01." },
        new UpdateRequest { Pinned = true, Via = RunnerInstallKind.DotnetTool, Version = "0.2.0", Channel = "stable", Mirror = "https://mirror.example.com/mbus" },
        ReleasePolicySample,
        UpdateReportSample,
        UpdateAbilitySample,
        UpdateStatusSample,
        UpdateWaitingTaskSample,
        new RunnerUpdateBlockedBy { Count = 3 },
        new RunnerUpdateProgress { BlockedBy = new RunnerUpdateBlockedBy { Count = 3 }, Stage = RunnerUpdateStage.Ended, Version = "0.2.0", Report = UpdateReportSample },
        new RunnerStatus { Resources = ResourceSample, Version = "0.1.0", Slots = 2, ActiveDeliveryIds = [Dlv], Draining = true, SlotCeiling = 4, SlotMachineMax = 10, SupportsManagedDrain = true, ManagedDrainId = "aci-operation" },
        new SlotsRequest { Slots = 2 },
        new RunnerWorkPolicy
        {
            OwnerSubjectId = Subject, AcceptsWorkFrom = Accepts, GroupMemberSubjectIds = [Subject], DailySpendCeilingUsd = 10m,
            RepoCredentialMode = RepoCredentialMode.OwnerSession, Releases = ReleasePolicySample, WorkspaceName = "Contoso",
        },
        new RunnerHeartbeat { Resources = ResourceSample, ActiveDeliveryIds = [Dlv], Slots = 2, SlotsBusy = 1, Draining = true, SessionId = "3f2a9c" },
        new HeartbeatAck { SupportsResourceSamples = true, Leases = [LeaseSample], RunnerConnected = true, Suspension = SuspensionSample },
        SuspensionSample,
        ResourceSample, ResourceCpu, ResourceMemory, ResourceTree, Resources,
        LeaseSample,
        new RunnerProgress { DeliveryId = Dlv, Progress = Progress },
        new RunnerSettle { Verification = Verification, VerificationAttempt = 2, VerificationDigest = VerificationDefinition.Digest,
            DeliveryId = Dlv, Attempt = 2, Result = Result, Declined = DeclineReason.MachineNotReady, UnreadableRole = "developer" },
        new RunnerRepoCredentialRequest { DeliveryId = Dlv, Repository = "https://github.com/acme/api.git", CanPush = true },
        new RunnerRepoCredential { UserName = "x-access-token", Secret = "ghs_x", ExpiresAt = T.AddHours(1) },
        new RunnerTelemetryCredentialRequest { DeliveryId = Dlv },
        new RunApprovalRequest { DeliveryId = Dlv },
        new ToolSetupPlan { Os = MachineOs.MacOs, PackageLabel = "Command Line Tools for Xcode-26.1", Version = "26.1", BeforeVersion = "2.49.0" },
        new ToolSetupReport { State = "succeeded", Detail = "Git is ready.", Plan = new() { Version = "2.52.0", BeforeVersion = "2.51.0.windows.1" },
            ApprovalId = "apr_setup", BeforeVersion = "2.51.0.windows.1", AfterVersion = "2.52.0.windows.1", ExitCode = 0 },
        new ToolSetupApprovalRequest { Plan = new() { Version = "2.52.0", BeforeVersion = "2.51.0.windows.1" }, ApprovalId = "apr_setup" },
        new ToolSetupApprovalState { ApprovalId = "apr_setup", State = "approved", ExpiresAt = T.AddHours(1), Detail = "Ada approved it." },
        new ToolSetupResultRequest { Plan = new() { Version = "2.52.0", BeforeVersion = "2.51.0.windows.1" }, ApprovalId = "apr_setup",
            Report = new() { State = "succeeded", Detail = "Git is ready.", Plan = new() { Version = "2.52.0", BeforeVersion = "2.51.0.windows.1" },
                ApprovalId = "apr_setup", BeforeVersion = "2.51.0.windows.1", AfterVersion = "2.52.0.windows.1", ExitCode = 0 } },
        new RunApprovalState { ApprovalId = "apr_01jcz7m2v1g3x4y5z6a7b8c9dm", Status = RunApprovalStatus.Declined, ApproverName = "Ada Lovelace",
            Detail = "Ada Lovelace declined to run it on build-01; it waits for another machine." },
        Price,
        CostCap,
        new RunnerTelemetryCredential { Endpoint = "/api/w/w101/otlp", Token = "mbt_x", ExpiresAt = T.AddHours(1), Traces = true, Metrics = true, Logs = true },
        new UploadLinkRequest { Target = RoleTo, Name = "due-dates.bundle", Format = AttachmentFormat.GitBundle, SizeBytes = 20_971_520, Sha256 = Sha, ThreadId = Thr, Encryption = Enc },
        new UploadLink { AttachmentId = Att, Url = "https://127.0.0.1:10000/devstoreaccount1/attachments/w101/att?sig=w", ExpiresAt = T.AddMinutes(10), MaxBytes = 104_857_600 },
        AttachSpec,
        new McpError { Code = McpErrorCodes.LimitReached, Message = "Project budget reached.", Retryable = true, RetryAfterSeconds = 60, Field = "projectId", ProposalId = "rpr_example", Status = RoleProposalStatus.Pending, CurrentRevision = Sha },
        Sealed,
        Send,
        Sent,
        new SendManyInput { Messages = [Send], ProjectId = Prj, Join = Join },
        new SendManyResult { Results = [Sent], JoinThreadId = Thr, Notice = "Budget at 80%." },
        new WaitInput { ThreadIds = [Thr], ProjectId = Prj, SeenCopies = [Dlv], SeenReplies = [Msg], Mode = WaitMode.All, TimeoutSeconds = 90 },
        new WaitResult { Notice = "Budget at 80%.", Partial = true, SeenCopies = [Dlv], SeenReplies = [Msg], Finished = [Thread], Pending = ["thr_01jcz7m2v1g3x4y5z6a7b8c9dk"], TimedOut = true, Replies = [Received] },
        new WatchInput { ThreadId = Thr, ProjectId = Prj, After = "evt_1", TimeoutSeconds = 30, Limit = 20, Compact = false },
        new WatchResult { Events = [new ThreadSettled { EventId = "evt_4", ThreadId = Thr, ProjectId = Prj, At = T, Result = Result }], Cursor = "evt_4", More = true },
        new InboxInput { Role = "developer", Max = 2, Peek = true, TimeoutSeconds = 30, InboxId = "inb_01jcz7m2v1g3x4y5z6a7b8c9dm" },
        Received,
        new InboxResult { Messages = [Received] },
        new ProgressInput { ThreadId = Thr, DeliveryId = Dlv, Message = "Running tests", Percent = 60, Request = CodeRequest },
        new ProgressResult { LeaseExpiresAt = T.AddMinutes(30), CancelRequested = true },
        new SettleInput { ThreadId = Thr, DeliveryId = Dlv, Attempt = 2, Friction = [Friction], Result = Result, Reply = PlainBody, SealedReply = Sealed, ReplyOnly = true, FrictionOnly = true, Attach = [AttachSpec], Attachments = [Attachment] },
        new FetchAttachmentInput { ThreadId = Thr, AttachmentId = Att, DeliveryId = Dlv, Repository = "/src/app", Into = "refs/remotes/mbus/feature", Path = "/tmp/a.patch" },
        new FetchAttachmentResult { AttachmentId = Att, Format = AttachmentFormat.GitBundle, Commit = "0123abcd", Ref = "refs/remotes/mbus/feature", Path = "/tmp/a.patch" },
        new SettleOutput { Status = ThreadStatus.Replied, ReplyMessageId = Msg },
        new StatusInput { ThreadIds = [Thr], ProjectId = Prj, Compact = true, Limit = 20 },
        new StatusResult { Threads = [Thread], Replies = [Received], Partial = true, Project = Project, Tasks = [Brief], More = true },
        Brief,
        new RetargetInput { ThreadId = Thr, To = RoleTo, Reason = "Wrong recipient", DryRun = true,
            ExpectedTaskMessageId = Msg, OperationId = "operation-example", SealedBody = new SealedBody { Body = Body, Encryption = Enc }, Attachments = [Attachment] },
        new RetargetResult { ThreadId = Thr, MessageId = Msg, PreviousMessageId = Msg, From = RoleTo, To = new AgentTarget { AgentId = Agt },
            Status = ThreadStatus.Queued, DryRun = true, AlreadyApplied = true, NeedsReseal = true,
            Recipient = new PublicKeyInfo { KeyId = "ek_example", Scope = KeyScope.Workspace, Version = 1, Algorithm = "EC-P256", PublicKey = "example", Fingerprint = "example", CreatedAt = T },
            Copies = 1, AgentsOnline = 1, AgentsFree = 1, QueuePosition = 1, Notice = "Independent task settings apply." },
        new RetargetMetadata { PreviousMessageId = Msg, From = RoleTo, To = new AgentTarget { AgentId = Agt }, Reason = "Wrong role" },
        new MoveInput { ThreadId = Thr, ProjectId = Prj, IncludeSubtasks = true, Reason = "Wrong project", DryRun = true },
        new MovedFrom { ProjectId = Prj, Title = "Source", Tasks = 1 },
        new MoveThreadsResult { ThreadIds = [Thr], Tasks = 1, OpenTasks = 1,
            From = [new MovedFrom { ProjectId = Prj, Title = "Source", Tasks = 1 }], ToProjectId = Prj, ToTitle = "Target",
            EstimatedCostUsd = 1, BudgetCapUsd = 2, CostAfterUsd = 3, OverBudgetCap = true, DryRun = true },
        new CancelInput { ThreadId = Thr, ProjectId = Prj, Reason = "superseded" },
        new CancelResult { CancelledThreadIds = [Thr], Message = "Thread thr_1 had already finished (failed); cancelled 1 task started from it." },
        new FindProjectInput { Key = "gh:owner/repo#42" },
        new FindProjectResult { Project = Project },
        new EnsureProjectInput { ExecutionScope = ProjectExecutionScope.OwnerRunners, ClaimedWorkItems = ["ado:contoso/web#123"], CoordinatorInboxId = "inb_session", Key = "ado:contoso/web#123", Title = "Add due dates", Refs = [PrRef], BudgetCapUsd = 25m, MaxFanOut = 4 },
        new EnsureProjectResult { Project = Project, Created = true, CoordinationAcquired = true, CoordinatorInboxId = "inb_session" },
        new CompleteProjectInput { ProjectId = Prj, Summary = "Done.", Status = ProjectStatus.Failed, Refs = [PrRef], Usage = RunUsage },
        new CompleteProjectResult { Project = Project, Notice = "Session usage was unavailable." },
        new DirectoryInput { Role = "developer", Capabilities = ["os:macos"], OnlineOnly = true, VerificationRepositoryId = "github:12345" },
        new DirectoryResult { Roles = [DirectoryEntry], Verification = VerificationRegistry },
        VerificationRegistry,
        FrontendRecipe,
        FrontendApproval,
        new DescribeRoleInput { Role = "developer" },
        new DescribeRoleResult { Role = Role, Instructions = Instructions, AgentsOnline = 1 },
        new SubscribeInput { Refs = [PrRef], EventTypes = ["pr.merged"], ProjectId = Prj, DeliverTo = RoleTo, ExpiresInSeconds = 86400 },
        new SubscribeResult { SubscriptionId = "sub_01jcz7m2v1g3x4y5z6a7b8c9dm", DeliverTo = RoleTo, ExpiresAt = T.AddDays(1) },
        new OpenInboxInput { LifetimeSeconds = 3600 },
        new OpenInboxResult
        {
            InboxId = "inb_01jcz7m2v1g3x4y5z6a7b8c9dm", ReplyTo = new InboxTarget { InboxId = "inb_01jcz7m2v1g3x4y5z6a7b8c9dm" }, ExpiresAt = T.AddDays(1),
            AgentId = "agt_01jcz7m2v1g3x4y5z6a7b8c9dm", Resumable = true, SessionId = "sess-1", Notice = "Resumes this session.",
        },
        new RetryInput { ThreadId = Thr, Note = "The Mac now has the .NET SDK.", Repository = "/src/app" },
        new RequestApprovalInput
        {
            Affected = "Archived task rows.",
            Title = "Drop the archived_tasks table", Detail = "Deletes 1,200 archived rows.", Approver = "role:developer", ThreadId = Thr, ProjectId = Prj,
            HighRisk = true, ExpiresInSeconds = 3600, ReplyTo = RoleTo,
        },
        new WaitApprovalInput { ApprovalId = "apr_01jcz7m2v1g3x4y5z6a7b8c9dn", TimeoutSeconds = 55 },
        new ApprovalResult
        {
            Affected = "Archived task rows.",
            ApprovalId = "apr_01jcz7m2v1g3x4y5z6a7b8c9dn", Status = ApprovalState.Approved, Title = "Drop the archived_tasks table", Approvers = ["Ada"],
            ExpiresAt = T.AddHours(1), DecidedBy = "Ada", DecidedAt = T.AddMinutes(5), Comment = "After the backup.", RequestDigest = "ab12", TimedOut = true,
        },
        new MessageBus.Contracts.Http.ServerCapabilitiesResponse { Product = "messagebus.ai", Capabilities = ["approvalAffectedV1", "settleAttemptV1", "roleProposalsV1", WorkflowCapabilities.Workflows],
            RoleProposals = new RoleProposalsCapability { Version = 1, BaselineVersion = "role-proposal-readonly-v1", NormalizerVersion = "role-definition-v1" } },
    }.ToDictionary(o => o.GetType());


    private static RunnerReleasePolicy ReleasePolicySample => new() { Channel = "preview", PinnedVersion = "0.2.0", Mirror = "https://mirror.example.com/mbus" };

    private static RunnerUpdateReport UpdateReportSample => new()
    {
        Outcome = RunnerUpdateOutcome.RolledBack, FromVersion = "0.1.0", ToVersion = "0.2.0", At = T,
        Reason = "mbus 0.2.0 didn't start on this machine, so mbus 0.1.0 was put back.", ReceivedAt = T,
    };

    private static RunnerUpdateAbility UpdateAbilitySample => new() { Via = RunnerInstallKind.Standalone, CanUpdate = false, Reason = "This mbus is a build from source and can't update itself." };

    private static RunnerUpdateWaitingTask UpdateWaitingTaskSample => new()
    {
        ThreadId = Thr, Title = "Plan the client split", AgentKind = "claude-code", AgentDisplayName = "Build PC · Claude Code", StartedAt = T,
    };

    private static MachineUpdateStatus UpdateStatusSample => new()
    {
        BlockedBy = new RunnerUpdateBlockedBy { Count = 3 },
        Stage = RunnerUpdateStage.Waiting, Version = "0.2.0", RequestedAt = T, At = T, OfflineAt = T, WaitingFor = [UpdateWaitingTaskSample],
    };



    private static RoleDefinitionV1 ProposalDefinition => new() { Name = Role.Name, DisplayName = Role.DisplayName, Description = Role.Description,
        Delivery = DeliverySettings, RequiredCapabilities = ["tool:dotnet"], Limits = Limits, AlertRules = [Alert], Access = Role.Access,
        RunEnvironment = new RoleProposalRunEnvironment
        {
            CodexOnWindows = RunEnv.CodexOnWindows, KeepAgentTools = RunEnv.KeepAgentTools, Checkout = RunEnv.Checkout,
            SetupCommand = RunEnv.SetupCommand, TimeoutSeconds = RunEnv.TimeoutSeconds, Logs = RunEnv.Logs,
            PermissionProfile = new RoleProposalPermissionProfile
            {
                Name = Profile.Name, AllowedTools = Profile.AllowedTools, Network = Profile.Network, AllowedHosts = Profile.AllowedHosts,
                CanEditFiles = Profile.CanEditFiles, CanRunCommands = Profile.CanRunCommands, CanPush = Profile.CanPush, CanRunTests = Profile.CanRunTests,
            },
            Models = new RoleProposalModels { Defaults = RunEnv.Models.Defaults, Allowed = RunEnv.Models.Allowed },
        }, Instructions = "Review changes." };
    private static RoleProposalBaseRole ProposalBase => new() { Generation = "gen_original", Etag = "etag_original" };
    private static RoleProposalReceipt ProposalReceipt => new() { ProposalId = "rpr_example", Role = "developer", Generation = "gen_original", Version = 4,
        FinalDigest = Sha, ContentSha256 = Sha, ApprovedBy = Subject, AppliedAt = T };

    private static DeviceProof DeviceProofSample => new() { Certificate = "MIIDXTCCAkWgAwIBAgI=", SignedAt = 1_794_000_000, Signature = "c2lnbmF0dXJl" };

    private static WorkspaceSuspensionStatus SuspensionSample => new()
    { Message = "Under review", Reference = "SUS-ABC234", SupportEmail = "help@example.com", StopRunningTasks = true };

    private static LeaseState LeaseSample => new() { DeliveryId = Dlv, LeaseExpiresAt = T.AddMinutes(30), CancelRequested = true };


}
