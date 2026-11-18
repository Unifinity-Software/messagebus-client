using System.Net.Http.Json;
using MessageBus.Cli.Agents;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Runner;
using MessageBus.Contracts.Machines;

namespace MessageBus.Cli.Runner;

/// <summary>The runner's locally opted-in tool setup, shared by the dashboard and terminal.</summary>
public interface IToolSetupRequests
{
    /// <summary>The current plan or the last result.</summary>
    ToolSetupReport Status { get; }
    /// <summary>
    /// <see cref="Status"/>, except that a settled state with nothing running says Git is installed when it already works,
    /// so a machine that has Git is never told to opt in to setting it up.
    /// </summary>
    Task<ToolSetupReport> CurrentAsync(CancellationToken cancellationToken);
    /// <summary>True while a prior installer may still be changing this machine.</summary>
    bool BlocksWork { get; }
    /// <summary>Starts setup without waiting for an owner to approve it.</summary>
    ToolSetupReport Start();
    /// <summary>Retries a locally saved audit result after a runner restart, without repeating an install.</summary>
    Task RecoverAsync(CancellationToken cancellationToken);
    /// <summary>Cancels and joins any setup before the runner stops.</summary>
    Task StopAsync();
}

/// <summary>Only a fixed local installer runs, after an owner-only server approval and one-use claim.</summary>
internal sealed class ToolSetupRequests(
    IToolSetupPlatform platform, HttpClient service, Func<IDisposable?> pause,
    Func<CancellationToken, Task> rescan, string settingsDirectory, Uri dashboard, TextWriter log, TimeProvider time) : IToolSetupRequests
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private ToolSetupReport _status = ReadInterrupted(settingsDirectory) ?? ReadSaved(settingsDirectory)?.Report
        ?? new() { State = "available", Detail = "Install Git after approving the exact plan." };
    private Task _pending = Task.CompletedTask;
    private Task _readinessRetry = Task.CompletedTask;

    internal const string InterruptedFile = "tool-setup-interrupted.json";
    internal const string InterruptedDetail = "Git setup has an unknown installer outcome, so this runner stays paused. On this machine, finish or close any Git installer, then run mbus machine setup git --acknowledge-interrupted. This does not approve another install.";

    public bool BlocksWork => File.Exists(Path.Combine(settingsDirectory, InterruptedFile));

    public ToolSetupReport Status => !_pending.IsCompleted ? Volatile.Read(ref _status)
        : BlocksWork ? (ReadInterrupted(settingsDirectory) ?? new() { State = "failed", Detail = InterruptedDetail }) with { CanStart = false }
        : platform.DisabledReason is { } why
        ? new() { State = "disabled", Detail = why }
        : Volatile.Read(ref _status);

    public async Task<ToolSetupReport> CurrentAsync(CancellationToken cancellationToken)
    {
        var report = Status;
        // An install in progress, its result and the local safety pause keep their own report. The opt-in rule is unchanged:
        // it still guards Start; it just isn't shown as an instruction to a machine that doesn't need Git set up.
        if (!_pending.IsCompleted || BlocksWork || report.State is not ("disabled" or "available" or "failed" or "declined")) return report;
        try
        {
            return await platform.GitVersionAsync(cancellationToken).ConfigureAwait(false) is { } installed
                ? new() { State = "installed", BeforeVersion = installed, Detail = $"Git {installed} is installed." }
                : report;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return report;
        }
    }

    public ToolSetupReport Start()
    {
        lock (_gate)
        {
            if (BlocksWork) return Status;
            if (platform.DisabledReason is not null) return Status;
            if (!_pending.IsCompleted) return _status;
            if (_stop.IsCancellationRequested) return new() { State = "disabled", Detail = "The runner is stopping." };
            if (pause() is not { } idle)
                return new() { State = "failed", Detail = "Wait until this runner is idle and taking work before setting up Git." };
            FileStream exclusive;
            try
            {
                Directory.CreateDirectory(settingsDirectory);
                exclusive = new FileStream(Path.Combine(settingsDirectory, "tool-setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                idle.Dispose();
                return new() { State = "failed", Detail = "Another mbus process is already setting up this machine." };
            }
            Set(new() { State = "planning", Detail = "Checking Git and resolving its exact installer version." });
            _pending = Task.Run(async () =>
            {
                using (idle)
                using (exclusive)
                    await RunAsync(_stop.Token).ConfigureAwait(false);
            });
            return _status;
        }
    }

    internal Task Pending => _pending;

    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        // Every process shares the install lock: never report an interrupted placeholder while another
        // runner is still installing, or delete a newer result written by that runner.
        using var exclusive = TryLock(settingsDirectory);
        if (exclusive is null) return;
        await RecoverLockedAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RecoverLockedAsync(CancellationToken cancellationToken)
    {
        if (ReadSaved(settingsDirectory) is { } saved)
            await SendResultAsync(saved, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync()
    {
        Task pending;
        lock (_gate)
        {
            _stop.Cancel();
            pending = _pending;
        }
        await pending.ConfigureAwait(false);
        await _readinessRetry.ConfigureAwait(false);
    }

    private void Set(ToolSetupReport report)
    {
        Volatile.Write(ref _status, report);
        if (report.Detail is { } detail) log.WriteLine($"Git setup: {detail}");
    }

    private async Task RunAsync(CancellationToken ct)
    {
        ToolSetupPlan? plan = null;
        string? approvalId = null;
        var claimed = false;
        int? exit = null;
        try
        {
            // An unreachable audit endpoint must be retried before a later install can replace its saved result.
            await RecoverLockedAsync(ct).ConfigureAwait(false);
            if (File.Exists(Path.Combine(settingsDirectory, "tool-setup-result.json")))
                throw new CliException("The previous Git setup result is still waiting to reach the audit log. Restore the server connection before trying again.");
            if (await platform.GitVersionAsync(ct).ConfigureAwait(false) is { } installed)
            {
                Set(new() { State = "succeeded", BeforeVersion = installed, AfterVersion = installed, Detail = $"Git {installed} already works. Nothing was installed." });
                await RefreshReadinessAsync(ct).ConfigureAwait(false);
                return;
            }
            plan = await platform.ResolvePlanAsync(ct).ConfigureAwait(false);
            if (!ToolSetupCatalog.Valid(plan) || plan.Os != platform.Os) throw new CliException("The local installer did not report an allowed plan for this operating system.");
            var version = plan.Version;
            log.WriteLine($"Git setup plan: {ToolSetupCatalog.Command(plan)}");
            log.WriteLine($"Source: {ToolSetupCatalog.Source(plan)}. {ToolSetupCatalog.Rollback(plan)}");
            var answer = await PostAsync<ToolSetupApprovalRequest, ToolSetupApprovalState>(ServiceRoutes.ToolSetupApproval, new() { Plan = plan }, ct).ConfigureAwait(false);
            approvalId = answer.ApprovalId;
            var approvalUrl = new Uri(dashboard, "notifications?approval=" + Uri.EscapeDataString(approvalId));
            Set(new() { State = "awaiting-approval", Plan = plan, ApprovalId = approvalId,
                Detail = $"Approve {ToolSetupCatalog.Name(plan)} {version} in the dashboard: {approvalUrl}. Then answer any operating-system permission prompt on this machine." });
            while (answer.State == "pending" && time.GetUtcNow() < answer.ExpiresAt)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), time, ct).ConfigureAwait(false);
                answer = await PostAsync<ToolSetupApprovalRequest, ToolSetupApprovalState>(ServiceRoutes.ToolSetupApproval,
                    new() { Plan = plan, ApprovalId = approvalId }, ct).ConfigureAwait(false);
            }
            if (answer.State != "approved" || time.GetUtcNow() >= answer.ExpiresAt)
            {
                Set(new() { State = "declined", Plan = plan, ApprovalId = approvalId, Detail = answer.Detail ?? "The owner did not approve this plan before it expired. Nothing was installed." });
                return;
            }
            // A newly working Git changes the reviewed before-state. Never install over it under the old approval.
            if (await platform.GitVersionAsync(ct).ConfigureAwait(false) is not null)
                throw new CliException("Git became available while approval was pending. Start setup again to review the new state.");
            var claim = await PostAsync<ToolSetupApprovalRequest, ToolSetupApprovalState>(ServiceRoutes.ToolSetupClaim,
                new() { Plan = plan, ApprovalId = approvalId }, ct).ConfigureAwait(false);
            if (claim.State != "approved" || time.GetUtcNow() >= claim.ExpiresAt)
                throw new CliException("This exact installation plan could not be claimed. Start setup again.");
            claimed = true;
            // Keep a local tombstone too: an interrupted install can never be resumed using an old approval.
            var marker = Path.Combine(settingsDirectory, "tool-setup-" + ToolSetupCatalog.Digest(plan) + "-" + SafeId(approvalId) + ".claimed");
            using (new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            var uncertain = new ToolSetupReport
            {
                State = "failed", Plan = plan, ApprovalId = approvalId, BeforeVersion = plan.BeforeVersion,
                Detail = InterruptedDetail,
            };
            // Write before starting native code. The marker remains through timeouts, lost handles and
            // runner crashes; neither a restart nor a dashboard request is an acknowledgement.
            WriteAtomic(Path.Combine(settingsDirectory, InterruptedFile), MessageBusJson.Serialize(uncertain));
            SaveResult(new() { Plan = plan, ApprovalId = approvalId, Report = uncertain });
            Set(new() { State = "installing", Plan = plan, ApprovalId = approvalId,
                Detail = $"Installing {ToolSetupCatalog.Name(plan)} {version}. Answer the operating-system permission prompt on this machine if one appears." });
            exit = await platform.InstallAsync(plan, ct).ConfigureAwait(false);
            platform.RefreshPath();
            var after = await platform.GitVersionAsync(ct).ConfigureAwait(false);
            var verified = exit == 0 && await platform.VerifyInstalledAsync(plan, after, ct).ConfigureAwait(false);
            // Only a normal successful exit plus the expected working Git clears the safety marker.
            // A nonzero winget exit cannot prove an elevated child has stopped changing this machine.
            if (verified) File.Delete(Path.Combine(settingsDirectory, InterruptedFile));
            Set(new() { State = verified ? "succeeded" : "failed", Plan = plan, ApprovalId = approvalId, BeforeVersion = plan.BeforeVersion,
                AfterVersion = after, ExitCode = exit, Detail = verified
                    ? $"Git {after} is installed and works. Checking runner readiness again."
                    : $"Git setup did not verify successfully (installer exit {exit}, Git {after ?? "not found"}). {InterruptedDetail}" });
            await RefreshReadinessAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Set(_status with { State = "failed", Plan = plan, ApprovalId = approvalId, ExitCode = exit,
                Detail = BlocksWork ? InterruptedDetail : "Git setup stopped before it could verify completion. Nothing was installed." });
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or CliException or System.Text.Json.JsonException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Set(_status with { State = "failed", Plan = plan, ApprovalId = approvalId, ExitCode = exit,
                Detail = BlocksWork ? InterruptedDetail : ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message });
        }
        finally
        {
            if (claimed && plan is not null && approvalId is not null)
            {
                try
                {
                    var result = new ToolSetupResultRequest { Plan = plan, ApprovalId = approvalId, Report = _status };
                    if (BlocksWork) WriteAtomic(Path.Combine(settingsDirectory, InterruptedFile), MessageBusJson.Serialize(_status));
                    SaveResult(result);
                    // Runner shutdown cancellation must not discard the audit outcome; this final call has its own short deadline.
                    await SendResultAsync(result, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log.WriteLine($"Git setup: couldn't save its final audit result: {ex.Message}");
                }
            }
        }
    }

    private async Task RefreshReadinessAsync(CancellationToken ct)
    {
        try { await rescan(ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or System.Text.Json.JsonException or CliException)
        {
            Set(_status with { Detail = (_status.State == "succeeded" ? "Git works on this machine. " : _status.Detail + " ")
                + "The readiness update could not reach the service; the runner will retry it." });
            if (!_stop.IsCancellationRequested && _readinessRetry.IsCompleted)
                _readinessRetry = RetryReadinessAsync(_stop.Token);
        }
    }

    private async Task RetryReadinessAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(15), time, ct).ConfigureAwait(false);
                try
                {
                    await rescan(ct).ConfigureAwait(false);
                    if (_status.State == "succeeded") Set(_status with { Detail = $"Git {_status.AfterVersion} works and runner readiness is up to date." });
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or System.Text.Json.JsonException or CliException)
                {
                    ct.ThrowIfCancellationRequested();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    internal static void AcknowledgeInterrupted(string directory)
    {
        using var exclusive = TryLock(directory) ?? throw new CliException("A runner is still setting up Git. Wait for it to finish or stop it before acknowledging an interrupted install.");
        var path = Path.Combine(directory, InterruptedFile);
        if (!File.Exists(path)) throw new CliException("There is no interrupted Git setup to acknowledge.");
        File.Delete(path);
    }

    private static FileStream? TryLock(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            return new FileStream(Path.Combine(directory, "tool-setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; }
    }

    private static ToolSetupReport? ReadInterrupted(string directory)
    {
        try
        {
            var path = Path.Combine(directory, InterruptedFile);
            return File.Exists(path) ? MessageBusJson.Deserialize<ToolSetupReport>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    private async Task SendResultAsync(ToolSetupResultRequest result, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await service.PostAsJsonAsync(ServiceRoutes.ToolSetupResult.TrimStart('/'), result, MessageBusJson.Options, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            File.Delete(Path.Combine(settingsDirectory, "tool-setup-result.json"));
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"Git setup: its result is saved locally and will be retried on restart: {ex.Message}");
        }
    }

    private void SaveResult(ToolSetupResultRequest result)
    {
        var path = Path.Combine(settingsDirectory, "tool-setup-result.json");
        WriteAtomic(path, MessageBusJson.Serialize(result));
    }

    private static void WriteAtomic(string path, string text)
    {
        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, text);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static ToolSetupResultRequest? ReadSaved(string directory)
    {
        var path = Path.Combine(directory, "tool-setup-result.json");
        try { return File.Exists(path) ? MessageBusJson.Deserialize<ToolSetupResultRequest>(File.ReadAllText(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException) { return null; }
    }

    private async Task<TResponse> PostAsync<TRequest, TResponse>(string route, TRequest body, CancellationToken ct)
    {
        using var response = await service.PostAsJsonAsync(route.TrimStart('/'), body, MessageBusJson.Options, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new CliException($"The service refused Git setup (HTTP {(int)response.StatusCode}). {await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)}");
        return await response.Content.ReadFromJsonAsync<TResponse>(MessageBusJson.Options, ct).ConfigureAwait(false)
            ?? throw new CliException("The service returned no Git setup approval state.");
    }

    private static string SafeId(string id) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)))[..16];
    internal static bool VersionsMatch(string expected, string? actual) => actual?.Replace(".windows.", ".", StringComparison.OrdinalIgnoreCase) == expected
        || actual == expected + ".windows.1";
}

/// <summary>Small boundary for testing the actual state machine without installing system software.</summary>
internal interface IToolSetupPlatform
{
    MachineOs Os => MachineOs.Windows;
    string? DisabledReason { get; }
    Task<string?> GitVersionAsync(CancellationToken ct);
    Task<string> ResolveVersionAsync(CancellationToken ct);
    async Task<ToolSetupPlan> ResolvePlanAsync(CancellationToken ct) => new() { Os = Os, Version = await ResolveVersionAsync(ct).ConfigureAwait(false) };
    Task<int> InstallAsync(ToolSetupPlan plan, CancellationToken ct);
    Task<bool> VerifyInstalledAsync(ToolSetupPlan plan, string? gitVersion, CancellationToken ct) =>
        Task.FromResult(plan.Os == MachineOs.Windows && ToolSetupRequests.VersionsMatch(plan.Version, gitVersion));
    void RefreshPath();
}
