using System.Text.Json;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Server;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Contracts.Security;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class ServerBindingTests
{
    private const string Origin = "https://eu.stamps.example.test";
    private const string Alias = "https://alias.stamps.example.test";
    private const string Legacy = """{"product":"messagebus.ai","signIn":{"microsoft":true},"capabilities":["approvalAffectedV1","settleAttemptV1"],"stamp":{"stampId":"ca","geography":"canada","azureRegion":"canadacentral","url":"https://legacy.example.test","aliases":[]}}""";
    private const string Isolated = """{"product":"messagebus.ai","url":"https://eu.stamps.example.test","capabilities":["approvalAffectedV1","settleAttemptV1"],"stamp":{"stampId":"eu","geography":"eu","azureRegion":"westeurope","url":"https://eu.stamps.example.test","aliases":["https://alias.stamps.example.test"]},"signIn":{"microsoft":true,"authority":{"version":1,"stampId":"eu","issuer":"https://eu.stamps.example.test","audience":"https://eu.stamps.example.test"},"machineSignature":{"version":2,"required":true}}}""";
    private const string Expected = """{"version":1,"transportOrigin":"https://eu.stamps.example.test","stampId":"eu","issuer":"https://eu.stamps.example.test","audience":"https://eu.stamps.example.test","authorityVersion":1,"minimumMachineSignatureVersion":2,"machineSignatureRequired":true}""";
    private static ServerBindingFloor NeverPinned => ServerBindingFloor.FromCompleteState([]);

    [Fact]
    public async Task Restart_stale_writers_rotation_and_logout_preserve_the_floor_and_pending_checkpoint()
    {
        using var machine = new TestMachine();
        var store = new ConfigStore(machine.MbusFolder, "file");
        var staleConfig = new CliConfig { Server = Origin + "/gateway", MachineId = "mch_fixture", PendingRunnerEnrollment = "fixture-fingerprint" };
        var staleSession = Session(Origin + "/gateway");
        store.Save(staleConfig);
        store.SaveSession(staleSession);
        var snapshot = await Prepare(store);
        Assert.Equal(Origin + "/gateway/", snapshot!.SelectedEndpoint);
        Assert.Equal(snapshot, store.Load().ServerBinding);
        Assert.Equal(snapshot, store.LoadSession()!.ServerBinding);

        // A writer holding a pre-pin object must reread and merge, including pending enrollment and token rotation.
        var restarted = new ConfigStore(machine.MbusFolder, "file");
        restarted.Save(staleConfig with { WorkspaceName = "Changed", PendingRunnerEnrollment = null });
        restarted.SaveSession(staleSession with { AccessToken = "fixture-rotated-access", RefreshToken = "fixture-rotated-refresh" });
        Assert.Equal(snapshot, restarted.Load().ServerBinding);
        Assert.Equal(snapshot, restarted.LoadSession()!.ServerBinding);
        Assert.Equal("fixture-rotated-refresh", restarted.LoadSession()!.RefreshToken);
        restarted.DeleteSession();
        restarted.Save(new CliConfig()); // Local identity forgetting still retains the config's surviving evidence.
        Assert.Equal(snapshot, restarted.Load().ServerBinding);
        await AssertLegacyRefused(restarted);
        Assert.True(File.Exists(restarted.ServerBindingsPath));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(restarted.ServerBindingsPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(machine.MbusFolder));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_ledger_with_either_surviving_snapshot_blocks_load_save_and_logout(bool sessionOnly)
    {
        using var machine = new TestMachine();
        var store = new ConfigStore(machine.MbusFolder, "file");
        if (!sessionOnly) store.Save(new CliConfig { Server = Origin + "/gateway" });
        store.SaveSession(Session(Origin + "/gateway"));
        await Prepare(store);
        if (sessionOnly && File.Exists(store.ConfigPath)) File.Delete(store.ConfigPath);
        File.Delete(store.ServerBindingsPath);
        var before = File.ReadAllText(store.SessionPath);
        Assert.Throws<ServerBindingException>(() => store.LoadSession());
        Assert.Throws<ServerBindingException>(() => store.DeleteSession());
        Assert.Throws<ServerBindingException>(() => store.Save(new CliConfig()));
        Assert.Throws<ServerBindingException>(() => new ConfigStore(machine.MbusFolder, "file"));
        Assert.Equal(before, File.ReadAllText(store.SessionPath));
        Assert.False(File.Exists(store.ServerBindingsPath));
    }

    [Theory]
    [InlineData("ledger-corrupt")]
    [InlineData("ledger-future")]
    [InlineData("ledger-duplicate")]
    [InlineData("ledger-unreadable")]
    [InlineData("session-corrupt")]
    [InlineData("session-null-snapshot")]
    [InlineData("config-unknown-snapshot")]
    [InlineData("config-scope")]
    public async Task Unknown_corrupt_and_contradictory_saved_state_never_becomes_empty_legacy(string damage)
    {
        using var machine = new TestMachine();
        var store = new ConfigStore(machine.MbusFolder, "file");
        store.Save(new CliConfig { Server = Origin + "/gateway" });
        store.SaveSession(Session(Origin + "/gateway"));
        await Prepare(store);
        var ledger = File.ReadAllText(store.ServerBindingsPath);
        switch (damage)
        {
            case "ledger-corrupt": File.WriteAllText(store.ServerBindingsPath, "{broken"); break;
            case "ledger-future": File.WriteAllText(store.ServerBindingsPath, ledger.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal)); break;
            case "ledger-duplicate": File.WriteAllText(store.ServerBindingsPath, ledger.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1,\"schemaVersion\": 1", StringComparison.Ordinal)); break;
            case "ledger-unreadable": File.Delete(store.ServerBindingsPath); Directory.CreateDirectory(store.ServerBindingsPath); break;
            case "session-corrupt": File.WriteAllText(store.SessionPath, "{broken"); break;
            case "session-null-snapshot": File.WriteAllText(store.SessionPath, "{\"serverBinding\":null}"); break;
            case "config-unknown-snapshot": File.WriteAllText(store.ConfigPath, File.ReadAllText(store.ConfigPath).Replace("\"minimumMachineSignatureVersion\": 2", "\"minimumMachineSignatureVersion\": 3", StringComparison.Ordinal)); break;
            case "config-scope": File.WriteAllText(store.ConfigPath, File.ReadAllText(store.ConfigPath).Replace("\"server\": \"" + Origin + "/gateway\"", "\"server\": \"" + Origin + "/other\"", StringComparison.Ordinal)); break;
        }
        Assert.Throws<ServerBindingException>(() => store.LoadSession());
        Assert.Throws<ServerBindingException>(() => new ConfigStore(machine.MbusFolder, "file"));
    }

    [Theory]
    [InlineData("before-marker", false)]
    [InlineData("after-marker", true)]
    [InlineData("after-ledger", true)]
    [InlineData("after-config", true)]
    [InlineData("after-session", true)]
    [InlineData("after-commit", false)]
    public async Task Interrupted_durable_boundaries_never_return_a_prepared_credential_or_downgrade(string boundary, bool interrupted)
    {
        using var machine = new TestMachine();
        var store = new ConfigStore(machine.MbusFolder, "file");
        store.Save(new CliConfig { Server = Origin + "/gateway" });
        store.SaveSession(Session(Origin + "/gateway"));
        store.BindingWriteBoundary = current => { if (current == boundary) throw new IOException("fixture disk failure"); };
        await Assert.ThrowsAsync<IOException>(() => Prepare(store));
        Assert.Equal(interrupted, File.Exists(Path.Combine(machine.MbusFolder, "server-bindings.pending")));
        Assert.Empty(Directory.GetFiles(machine.MbusFolder, "*.tmp"));
        if (interrupted)
            Assert.Throws<ServerBindingException>(() => new ConfigStore(machine.MbusFolder, "file"));
        else
        {
            var restarted = new ConfigStore(machine.MbusFolder, "file");
            if (boundary == "after-commit") await AssertLegacyRefused(restarted);
            else Assert.Null(restarted.Load().ServerBinding); // No credential was prepared and no pin was published.
        }
    }

    [Fact]
    public async Task Filesystem_publish_failure_leaves_a_restrictive_marker_and_original_credentials()
    {
        using var machine = new TestMachine();
        var store = new ConfigStore(machine.MbusFolder, "file");
        store.SaveSession(Session(Origin + "/gateway"));
        var original = File.ReadAllText(store.SessionPath);
        store.BindingWriteBoundary = boundary =>
        {
            if (boundary == "after-marker") Directory.CreateDirectory(store.ServerBindingsPath);
        };
        await Assert.ThrowsAnyAsync<IOException>(() => Prepare(store));
        Assert.Equal(original, File.ReadAllText(store.SessionPath));
        Assert.Empty(Directory.GetFiles(machine.MbusFolder, "*.tmp"));
        Assert.Throws<ServerBindingException>(() => store.LoadSession());
    }

    [Fact]
    public async Task Concurrent_conflicting_first_pins_reread_the_winner_under_the_shared_lock()
    {
        using var machine = new TestMachine();
        var first = new ConfigStore(machine.MbusFolder, "file");
        var second = new ConfigStore(machine.MbusFolder, "file");
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winner = first.WithProfileSecurityLockAsync(async context =>
        {
            held.SetResult();
            await release.Task;
            return context.PrepareBinding(ServerEndpoint.Parse(Origin + "/gateway"), ServerDiscoveryParser.Parse(Isolated));
        }, TestContext.Current.CancellationToken);
        await held.Task;
        var conflicting = second.WithProfileSecurityLockAsync(context => Task.FromResult(context.PrepareBinding(
            ServerEndpoint.Parse(Origin + "/gateway"), ServerDiscoveryParser.Parse(Isolated.Replace("\"stampId\":\"eu\"", "\"stampId\":\"ca\"", StringComparison.Ordinal)))), TestContext.Current.CancellationToken);
        Assert.False(conflicting.IsCompleted);
        release.SetResult();
        var snapshot = await winner;
        await Assert.ThrowsAsync<ServerBindingException>(() => conflicting);
        // Independent origins merge; the losing writer cannot drop the earlier pin.
        var other = "https://ca.stamps.example.test";
        await second.WithProfileSecurityLockAsync(context => Task.FromResult(context.PrepareBinding(ServerEndpoint.Parse(other),
            ServerDiscoveryParser.Parse(Isolated.Replace(Origin, other, StringComparison.Ordinal).Replace("\"stampId\":\"eu\"", "\"stampId\":\"ca\"", StringComparison.Ordinal)))), TestContext.Current.CancellationToken);
        using var ledger = JsonDocument.Parse(File.ReadAllText(first.ServerBindingsPath));
        Assert.Equal(2, ledger.RootElement.GetProperty("bindings").EnumerateObject().Count());
        Assert.Equal(snapshot!.Binding, ServerBinding.Create("eu", Origin, Origin));
        await AssertLegacyRefused(first);
    }

    [Fact]
    public async Task Inherited_children_cannot_overlap_preparations_or_writers_and_public_writers_wait_for_release()
    {
        using var machine = new TestMachine();
        var store = new ConfigStore(machine.MbusFolder, "file");
        store.Save(new CliConfig { Server = Origin + "/gateway" });
        var beforeMarker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? ordinaryWriter = null;
        var other = "https://ca.stamps.example.test";
        var firstBoundary = 0;
        store.BindingWriteBoundary = boundary =>
        {
            if (boundary == "before-marker" && Interlocked.Increment(ref firstBoundary) == 1)
            {
                beforeMarker.SetResult();
                release.Task.GetAwaiter().GetResult();
            }
        };
        await store.WithProfileSecurityLockAsync(async context =>
        {
            var first = Task.Run(() => context.PrepareBinding(ServerEndpoint.Parse(Origin + "/gateway"),
                ServerDiscoveryParser.Parse(Isolated)), TestContext.Current.CancellationToken);
            await beforeMarker.Task;
            try
            {
                await Task.Run(() =>
                {
                    Assert.Throws<InvalidOperationException>(() => context.PrepareBinding(ServerEndpoint.Parse(other),
                        ServerDiscoveryParser.Parse(Isolated.Replace(Origin, other, StringComparison.Ordinal))));
                    Assert.Throws<InvalidOperationException>(() => context.Save(new CliConfig()));
                    Assert.Throws<InvalidOperationException>(() => context.SaveSession(Session(Origin + "/gateway")));
                }, TestContext.Current.CancellationToken);
                ordinaryWriter = Task.Run(() =>
                {
                    writerStarted.SetResult();
                    store.Save(new CliConfig { Server = Origin + "/gateway", WorkspaceName = "Child write" });
                }, TestContext.Current.CancellationToken);
                await writerStarted.Task;
                Assert.False(ordinaryWriter.IsCompleted);
                Assert.False(File.Exists(Path.Combine(machine.MbusFolder, "server-bindings.pending")));
            }
            finally { release.TrySetResult(); }
            await first;
            store.BindingWriteBoundary = null;
            // Sequential awaited child operations retain the same explicit context safely.
            await Task.Run(() => context.PrepareBinding(ServerEndpoint.Parse(other),
                ServerDiscoveryParser.Parse(Isolated.Replace(Origin, other, StringComparison.Ordinal))), TestContext.Current.CancellationToken);
            return true;
        }, TestContext.Current.CancellationToken);
        await ordinaryWriter!;
        Assert.Equal("Child write", store.Load().WorkspaceName);
        Assert.NotNull(store.Load().ServerBinding);
        using var ledger = JsonDocument.Parse(File.ReadAllText(store.ServerBindingsPath));
        Assert.Equal(2, ledger.RootElement.GetProperty("bindings").EnumerateObject().Count());
        Assert.False(File.Exists(Path.Combine(machine.MbusFolder, "server-bindings.pending")));
    }

    [Fact]
    public async Task Existing_shared_directory_permissions_are_unchanged_on_binding_and_provisioning_refusal()
    {
        using var machine = new TestMachine();
        var shared = Path.Combine(machine.Home, "shared-fixture");
        Directory.CreateDirectory(shared);
        string? aclBefore = null;
        UnixFileMode modeBefore = default;
        if (OperatingSystem.IsWindows())
        {
            var directory = new DirectoryInfo(shared);
            var acl = directory.GetAccessControl();
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                FileSystemRights.ReadAndExecute, AccessControlType.Allow));
            directory.SetAccessControl(acl);
            aclBefore = directory.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All);
        }
        else
        {
            modeBefore = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute;
            File.SetUnixFileMode(shared, modeBefore);
        }
        File.WriteAllText(Path.Combine(shared, "unrelated.txt"), "fixture data");
        var store = new ConfigStore(shared, "file");
        await Assert.ThrowsAsync<IOException>(() => Prepare(store));
        await Assert.ThrowsAsync<IOException>(() => store.WithProfileSecurityLockAsync(context =>
            Task.FromResult(context.ValidateProvisioning(ServerEndpoint.Parse(Origin), Expected, shared)), TestContext.Current.CancellationToken));
        if (OperatingSystem.IsWindows())
            Assert.Equal(aclBefore, new DirectoryInfo(shared).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        else Assert.Equal(modeBefore, File.GetUnixFileMode(shared));
        Assert.Equal("fixture data", File.ReadAllText(Path.Combine(shared, "unrelated.txt")));
        Assert.False(File.Exists(store.ServerBindingsPath));
        Assert.False(File.Exists(Path.Combine(shared, "server-bindings.pending")));
    }

    [Fact]
    public async Task Mac_directory_acl_grant_refuses_without_rewriting_the_existing_acl_or_mode()
    {
        if (!OperatingSystem.IsMacOS()) return;
        using var machine = new TestMachine();
        var profile = Path.Combine(machine.Home, "acl-fixture");
        DurableProfileFile.CreatePrivateDirectory(profile);
        // Independent native tool creates a real ACL grant that POSIX mode bits cannot describe.
        await RunNative("/bin/chmod", "+a", "everyone allow list,search", profile);
        var before = await AclEntries();
        var mode = File.GetUnixFileMode(profile);
        var store = new ConfigStore(profile, "file");
        await Assert.ThrowsAsync<IOException>(() => Prepare(store));
        Assert.Equal(before, await AclEntries());
        Assert.Equal(mode, File.GetUnixFileMode(profile));
        Assert.False(File.Exists(store.ServerBindingsPath));
        Assert.False(File.Exists(Path.Combine(profile, "server-bindings.pending")));

        async Task<string> AclEntries() => string.Join('\n', (await RunNative("/bin/ls", "-lde", profile)).Split('\n').Skip(1));

        static async Task<string> RunNative(string executable, params string[] arguments)
        {
            var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, await error);
            return await output;
        }
    }

    [Fact]
    public async Task Existing_private_owned_profile_is_accepted_without_permission_repair()
    {
        using var machine = new TestMachine();
        var profile = Path.Combine(machine.Home, "private-fixture");
        DurableProfileFile.CreatePrivateDirectory(profile);
        string? aclBefore = OperatingSystem.IsWindows()
            ? new DirectoryInfo(profile).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All) : null;
        UnixFileMode modeBefore = OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(profile);
        // Old-shaped settings and unrelated data already exist before ConfigStore opens the profile.
        File.WriteAllText(Path.Combine(profile, CliConfig.FileName), "{\"server\":\"" + Origin + "/gateway\"}");
        File.WriteAllText(Path.Combine(profile, "unrelated.txt"), "fixture data");
        var store = new ConfigStore(profile, "file");
        await Prepare(store);
        await store.WithProfileSecurityLockAsync(context => Task.FromResult(
            context.ValidateProvisioning(ServerEndpoint.Parse(Origin), Expected, profile)), TestContext.Current.CancellationToken);
        Assert.NotNull(store.Load().ServerBinding);
        Assert.Equal("fixture data", File.ReadAllText(Path.Combine(profile, "unrelated.txt")));
        if (OperatingSystem.IsWindows())
            Assert.Equal(aclBefore, new DirectoryInfo(profile).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.All));
        else Assert.Equal(modeBefore, File.GetUnixFileMode(profile));
    }

    [Fact]
    public void Directory_alias_is_refused_before_any_profile_file_or_permission_change()
    {
        // Windows symlink creation needs a host privilege; native ACL coverage above needs none.
        if (OperatingSystem.IsWindows()) return;
        using var machine = new TestMachine();
        var target = Path.Combine(machine.Home, "target-fixture");
        Directory.CreateDirectory(target);
        var mode = File.GetUnixFileMode(target);
        var alias = Path.Combine(machine.Home, "alias-fixture");
        Directory.CreateSymbolicLink(alias, target);
        Assert.Throws<IOException>(() => new ConfigStore(alias, "file"));
        Assert.Equal(mode, File.GetUnixFileMode(target));
        Assert.Empty(Directory.GetFileSystemEntries(target));
    }

    [Fact(Timeout = 30_000)]
    public async Task Published_session_callback_can_await_read_rotate_and_save_without_losing_the_floor()
    {
        using var machine = new TestMachine();
        var store = new ConfigStore(machine.MbusFolder, "file");
        store.Save(new CliConfig { Server = Origin + "/gateway" });
        store.SaveSession(Session(Origin + "/gateway"));
        var pin = await Prepare(store);
        // This zero-argument delegate is the published source/CLR shape used by package consumers.
        Func<Task<string>> callback = async () =>
        {
            await Task.Yield();
            var config = store.Load();
            var previous = await Task.Run(() => store.LoadSession(), TestContext.Current.CancellationToken);
            store.Save(config with { WorkspaceName = "Updated under session lock" });
            await Task.Run(() => store.SaveSession(previous! with
            {
                AccessToken = "fixture-rotated-access", RefreshToken = "fixture-rotated-refresh",
            }), TestContext.Current.CancellationToken);
            Assert.Equal(pin, store.Load().ServerBinding);
            return store.LoadSession()!.RefreshToken;
        };
#pragma warning disable CS0618 // Exercise the exact published overload, retained for existing consumers.
        var refresh = await store.WithSessionLockAsync(callback, TestContext.Current.CancellationToken);
#pragma warning restore CS0618
        Assert.Equal("fixture-rotated-refresh", refresh);
        Assert.Equal(refresh, store.LoadSession()!.RefreshToken);
        Assert.Equal(pin, store.LoadSession()!.ServerBinding);
        store.DeleteSession();
        Assert.Null(store.LoadSession());
        await AssertLegacyRefused(store);
    }

    [Fact(Timeout = 30_000)]
    public async Task Published_callback_children_share_the_guard_and_unrelated_callers_wait_for_release()
    {
        using var machine = new TestMachine();
        var store = new ConfigStore(machine.MbusFolder, "file");
        store.Save(new CliConfig { Server = Origin + "/gateway" });
        store.SaveSession(Session(Origin + "/gateway"));
        var pin = await Prepare(store);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = 0;
        store.BindingWriteBoundary = boundary =>
        {
            if (boundary != "before-config-save") return;
            Interlocked.Increment(ref writes);
            Assert.Throws<InvalidOperationException>(() => store.Load()); // Same-thread recursion also refuses.
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        };
#pragma warning disable CS0618
        var outer = store.WithSessionLockAsync(async () =>
        {
            var first = Task.Run(() => store.Save(new CliConfig
            { Server = Origin + "/gateway", WorkspaceName = "Winning child" }), TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            try
            {
                await Task.Run(() =>
                {
                    Assert.Throws<InvalidOperationException>(() => store.Load());
                    Assert.Throws<InvalidOperationException>(() => store.LoadSession());
                    Assert.Throws<InvalidOperationException>(() => store.Save(new CliConfig()));
                    Assert.Throws<InvalidOperationException>(() => store.SaveSession(Session(Origin + "/gateway")));
                    Assert.Throws<InvalidOperationException>(() => store.DeleteSession());
                }, TestContext.Current.CancellationToken);
            }
            finally { release.TrySetResult(); }
            await first;
            Assert.Equal(1, Volatile.Read(ref writes));
            Assert.Equal("Winning child", store.Load().WorkspaceName);
            Assert.Equal("fixture-refresh", store.LoadSession()!.RefreshToken);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.WithProfileSecurityLockAsync(
                context => Task.FromResult(context.Load()), TestContext.Current.CancellationToken));
            callbackChecked.TrySetResult();
            await finishCallback.Task.WaitAsync(TestContext.Current.CancellationToken);
            return true;
        }, TestContext.Current.CancellationToken);
#pragma warning restore CS0618
        try
        {
            await Task.WhenAny(callbackChecked.Task, outer).WaitAsync(TestContext.Current.CancellationToken);
            if (outer.IsCompleted) await outer; // Surface a failed callback instead of waiting on its signal forever.
            Assert.True(callbackChecked.Task.IsCompletedSuccessfully);
            // This call originates outside the compatibility scope on the SAME store, so it reaches
            // the normal semaphore wait synchronously and cannot read state until the callback exits.
            var waiting = store.WithProfileSecurityLockAsync(context =>
            {
                Assert.Equal(pin, context.Load().ServerBinding);
                context.SaveSession(Session(Origin + "/gateway") with { RefreshToken = "fixture-after-release" });
                return Task.FromResult(true);
            }, TestContext.Current.CancellationToken);
            Assert.False(waiting.IsCompleted);
            finishCallback.TrySetResult();
            await outer;
            await waiting;
        }
        finally
        {
            release.TrySetResult();
            finishCallback.TrySetResult();
            store.BindingWriteBoundary = null;
        }
        Assert.Equal(pin, store.Load().ServerBinding);
        Assert.Equal(pin, store.LoadSession()!.ServerBinding);
        Assert.Equal("fixture-after-release", store.LoadSession()!.RefreshToken);
    }

    [Theory(Timeout = 30_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Published_callback_scope_cannot_escape_completion_or_poison_later_operations(bool failCallback)
    {
        using var machine = new TestMachine();
        var store = new ConfigStore(machine.MbusFolder, "file");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? child = null;
#pragma warning disable CS0618
        var outer = store.WithSessionLockAsync(async () =>
        {
            child = Task.Run(async () =>
            {
                await release.Task.WaitAsync(TestContext.Current.CancellationToken);
                Assert.Throws<InvalidOperationException>(() => store.Load());
                Assert.Throws<InvalidOperationException>(() => store.Save(new CliConfig { WorkspaceName = "Escaped child" }));
                Assert.Throws<InvalidOperationException>(() => store.LoadSession());
                Assert.Throws<InvalidOperationException>(() => store.SaveSession(Session(Origin)));
                Assert.Throws<InvalidOperationException>(() => store.DeleteSession());
            }, TestContext.Current.CancellationToken);
            await Task.Yield();
            if (failCallback) throw new IOException("fixture callback failure");
            return true;
        }, TestContext.Current.CancellationToken);
#pragma warning restore CS0618
        try
        {
            if (failCallback) await Assert.ThrowsAsync<IOException>(() => outer);
            else await outer;
            store.Save(new CliConfig { WorkspaceName = "After callback" });
        }
        finally { release.TrySetResult(); }
        await child!;
        Assert.Equal("After callback", store.Load().WorkspaceName);
        Assert.Null(store.LoadSession());
    }

    [Fact]
    public async Task Explicit_context_supports_nested_session_writers_and_refuses_use_after_release()
    {
        using var machine = new TestMachine();
        var store = new ConfigStore(machine.MbusFolder, "file");
        ConfigStore.ProfileSecurityTransaction? escaped = null;
        await store.WithProfileSecurityLockAsync(async context =>
        {
            escaped = context;
            var snapshot = context.PrepareBinding(ServerEndpoint.Parse(Origin + "/gateway"), ServerDiscoveryParser.Parse(Isolated));
            return await SaveNestedAsync(context, snapshot);
        }, TestContext.Current.CancellationToken);
        Assert.NotNull(store.LoadSession()!.ServerBinding);
        Assert.Throws<InvalidOperationException>(() => escaped!.LoadBindingFloor());
        Assert.Throws<ServerBindingException>(() => store.SaveSession(Session(Origin + "/different") with { ServerBinding = store.Load().ServerBinding }));

        static async Task<bool> SaveNestedAsync(ConfigStore.ProfileSecurityTransaction transaction, ServerBindingSnapshot? snapshot)
        {
            await Task.Yield();
            transaction.Save(new CliConfig { Server = Origin + "/gateway", ServerBinding = snapshot });
            transaction.SaveSession(Session(Origin + "/gateway") with { ServerBinding = snapshot });
            return true;
        }
    }

    [Fact]
    public async Task Provisioning_validates_exact_origin_floor_and_explicit_writable_home_before_preparation()
    {
        using var machine = new TestMachine();
        var store = new ConfigStore(machine.MbusFolder, "file");
        await store.WithProfileSecurityLockAsync(context =>
        {
            var selected = ServerEndpoint.Parse(Origin + "/gateway");
            Assert.Throws<ServerBindingException>(() => context.ValidateProvisioning(selected, Expected, ""));
            Assert.Throws<ServerBindingException>(() => context.ValidateProvisioning(selected, Expected, machine.Home));
            Assert.Throws<ServerBindingException>(() => context.ValidateProvisioning(ServerEndpoint.Parse(Alias), Expected, machine.MbusFolder));
            var expected = context.ValidateProvisioning(selected, Expected, machine.MbusFolder);
            Assert.Throws<ServerBindingException>(() => context.PrepareBinding(selected, ServerDiscoveryParser.Parse(Legacy), expected));
            context.PrepareBinding(selected, ServerDiscoveryParser.Parse(Isolated), expected);
            Assert.Throws<ServerBindingException>(() => context.ValidateProvisioning(selected,
                Expected.Replace("\"stampId\":\"eu\"", "\"stampId\":\"ca\"", StringComparison.Ordinal), machine.MbusFolder));
            Assert.Empty(Directory.GetFiles(machine.MbusFolder, ".binding-write-probe-*"));
            return Task.FromResult(true);
        }, TestContext.Current.CancellationToken);
    }

    private static CliSessionFile Session(string server) => new()
    {
        Server = server, AccessToken = "fixture-access", RefreshToken = "fixture-refresh",
        AccessExpiresAt = DateTimeOffset.UnixEpoch, // Expiry must not erase the snapshot.
    };

    private static Task<ServerBindingSnapshot?> Prepare(ConfigStore store) => store.WithProfileSecurityLockAsync(context =>
        Task.FromResult(context.PrepareBinding(ServerEndpoint.Parse(Origin + "/gateway"), ServerDiscoveryParser.Parse(Isolated))), TestContext.Current.CancellationToken);

    private static Task AssertLegacyRefused(ConfigStore store) => store.WithProfileSecurityLockAsync(context =>
    {
        Assert.Throws<ServerBindingException>(() => ServerBindingPolicy.Evaluate(ServerEndpoint.Parse(Origin),
            ServerDiscoveryParser.Parse(Legacy), context.LoadBindingFloor(), null));
        return Task.FromResult(true);
    }, TestContext.Current.CancellationToken);

    [Fact]
    public void Descriptive_stamp_preserves_legacy_but_surviving_or_provisioned_pin_blocks_stripping()
    {
        var endpoint = ServerEndpoint.Parse(Origin + "/gateway");
        foreach (var json in new[] { """{"product":"messagebus.ai"}""", Legacy })
        {
            var discovery = ServerDiscoveryParser.Parse(json);
            Assert.Equal(1, ServerBindingPolicy.Evaluate(endpoint, discovery, NeverPinned, null).MachineSignatureVersion);
            var provisioned = ServerBindingExpectation.Parse(Expected);
            Assert.Throws<ServerBindingException>(() => ServerBindingPolicy.Evaluate(endpoint, discovery, NeverPinned, provisioned));
            // A surviving credential/checkpoint snapshot is evidence even if the ledger is missing.
            var surviving = ServerBindingFloor.FromCompleteState([provisioned]);
            Assert.Throws<ServerBindingException>(() => ServerBindingPolicy.Evaluate(endpoint, discovery, surviving, null));
        }
        Assert.Throws<ServerBindingException>(() => ServerBindingPolicy.Evaluate(endpoint, ServerDiscoveryParser.Parse(Isolated), ServerBindingFloor.Unknown, null));
    }

    [Theory]
    [InlineData("\"authority\":null")]
    [InlineData("\"authority\":{}")]
    [InlineData("\"machineSignature\":null")]
    [InlineData("\"machineSignature\":{\"version\":2,\"required\":true}")]
    public void Any_descriptor_presence_requires_the_whole_bundle(string fields) => Assert.Throws<ServerBindingException>(() =>
        ServerDiscoveryParser.Parse("{\"product\":\"messagebus.ai\",\"signIn\":{" + fields + "}}"));

    [Theory]
    [InlineData("\"version\":2,\"required\":true", "\"version\":1,\"required\":true")]
    [InlineData("\"version\":2,\"required\":true", "\"version\":2,\"required\":false")]
    [InlineData("\"version\":1,\"stampId\"", "\"version\":1,\"version\":1,\"stampId\"")]
    [InlineData("\"authority\":{", "\"Authority\":{")]
    [InlineData("\"machineSignature\":{", "\"machineSignature\":null,\"machineSignature\":{")]
    [InlineData("\"stampId\":\"eu\",\"issuer\"", "\"stampId\":\"ca\",\"issuer\"")]
    [InlineData("\"audience\":\"https://eu.stamps.example.test\"", "\"audience\":\"https://ca.stamps.example.test\"")]
    [InlineData("\"required\":true", "\"required\":\"true\"")]
    [InlineData("\"aliases\":[\"https://alias.stamps.example.test\"]", "\"aliases\":null")]
    public void Malformed_partial_duplicate_and_downgraded_bundles_refuse(string old, string replacement) =>
        Assert.Throws<ServerBindingException>(() => ServerDiscoveryParser.Parse(Isolated.Replace(old, replacement, StringComparison.Ordinal)));

    [Fact]
    public void Alias_needs_both_trusted_local_mapping_and_current_server_configuration()
    {
        var selected = ServerEndpoint.Parse(Alias + "/gateway");
        var discovery = ServerDiscoveryParser.Parse(Isolated);
        Assert.Throws<ServerBindingException>(() => ServerBindingPolicy.Evaluate(selected, discovery, NeverPinned, null));
        var mapped = ServerBindingExpectation.Create(Alias, ServerBinding.Create("eu", Origin, Origin));
        var result = ServerBindingPolicy.Evaluate(selected, discovery, NeverPinned, mapped);
        Assert.Equal(Origin, result.Binding!.Issuer);
        Assert.True(result.RequiresIsolatedPersistence);
        var noAlias = ServerDiscoveryParser.Parse(Isolated.Replace("[\"https://alias.stamps.example.test\"]", "[]", StringComparison.Ordinal));
        Assert.Throws<ServerBindingException>(() => ServerBindingPolicy.Evaluate(selected, noAlias, NeverPinned, mapped));
        Assert.NotEqual(ServerEndpoint.Parse(Origin + "/gateway"), selected); // equal stamp never grants credential migration
        Assert.Throws<ServerBindingException>(() => ServerBindingPolicy.Evaluate(selected, discovery, NeverPinned, ServerBindingExpectation.Parse(Expected)));
    }

    [Fact]
    public void Provisioning_cannot_replace_a_persisted_tuple_and_source_generated_shape_stays_additive()
    {
        var selected = ServerEndpoint.Parse(Origin);
        var provisioned = ServerBindingExpectation.Parse(Expected);
        var conflict = ServerBindingExpectation.Create(Origin, ServerBinding.Create("ca", "https://ca.stamps.example.test", "https://ca.stamps.example.test"));
        Assert.Throws<ServerBindingException>(() => ServerBindingPolicy.Evaluate(selected, ServerDiscoveryParser.Parse(Isolated), ServerBindingFloor.FromCompleteState([conflict]), provisioned));
        var legacy = new ServerInfoResponse { Product = ServerInfoResponse.ProductName, SignIn = new ServerSignIn { Microsoft = true } };
        var json = JsonSerializer.Serialize(legacy, CliJsonContext.Default.ServerInfoResponse);
        Assert.DoesNotContain("authority", json);
        Assert.DoesNotContain("machineSignature", json);
        Assert.Equal(1, ServerBindingPolicy.Evaluate(selected, ServerDiscoveryParser.Parse(json), NeverPinned, null).MachineSignatureVersion);
        Assert.Equal(new[] { "approvalAffectedV1", "settleAttemptV1" }, ServerDiscoveryParser.Parse(Isolated).Info.Capabilities);
        foreach (var malformed in new[] { "{}", "null", Expected.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
            Expected.Replace("\"authorityVersion\":1", "\"authorityVersion\":1,\"authorityVersion\":1", StringComparison.Ordinal) })
            Assert.Throws<ServerBindingException>(() => ServerBindingExpectation.Parse(malformed));
        Assert.Throws<ServerBindingException>(() => ServerDiscoveryParser.Parse("{broken"));
    }

    [Fact]
    public void Origin_normalization_keeps_exact_base_scope_and_requires_explicit_local_http_policy()
    {
        Assert.Equal(Origin, ProtocolOrigin.Normalize("HTTPS://EU.STAMPS.EXAMPLE.TEST:443/"));
        Assert.Equal("https://xn--bcher-kva.example.test", ProtocolOrigin.Normalize("https://bücher.example.test"));
        Assert.Equal("https://[::1]:8443", ProtocolOrigin.Normalize("https://[::1]:8443/"));
        var scope = ServerEndpoint.Parse("HTTPS://EU.STAMPS.EXAMPLE.TEST:443/gateway");
        Assert.Equal(scope, ServerEndpoint.Parse(Origin + "/gateway/"));
        Assert.NotEqual(scope, ServerEndpoint.Parse(Origin + "/other"));
        Assert.True(scope.Contains(new Uri(Origin + "/gateway/api/caller?x=%2F")));
        foreach (var uri in new[] { Origin + "/gateway-evil/api", Origin + "/other/api", Alias + "/gateway/api", Origin + "/gateway/../other/api",
            Origin + "/gateway/%2e%2e%2foutside", Origin + "/gateway/%252e%252e%252foutside", Origin + "/gateway/%5coutside" })
            Assert.False(scope.Contains(new Uri(uri)));
        Assert.Throws<ServerBindingException>(() => ServerEndpoint.Parse(Origin + "/gateway%2foutside"));
        foreach (var bad in new[] { "https://user@eu.stamps.example.test", Origin + "/a/..", Origin + "/?x=1", Origin + ".", "https://%65u.stamps.example.test", "https://eu.stamps.example.test\\evil", "https://[fe80::1%25eth0]", "https://eu.stamps.example.test:" })
            Assert.Throws<ArgumentException>(() => ProtocolOrigin.Normalize(bad));
        Assert.Throws<ServerBindingException>(() => ServerBinding.Create("eu", "http://localhost:1234", "http://localhost:1234"));
        Assert.Equal("http://localhost:1234", ServerBinding.Create("eu", "http://localhost:1234", "http://localhost:1234", allowLoopbackHttp: true).Issuer);
        Assert.Throws<ServerBindingException>(() => ServerBinding.Create("eu", "http://foreign.example.test", "http://foreign.example.test", allowLoopbackHttp: true));
    }
}
