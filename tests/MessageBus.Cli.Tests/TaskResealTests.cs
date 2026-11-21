using System.Net;
using System.Security.Cryptography;
using System.Text;
using MessageBus.Attachments.Client;
using MessageBus.Cli.Tests.Infrastructure;
using MessageBus.Cli.Configuration;
using MessageBus.Contracts.Attachments;
using MessageBus.Contracts.Json;
using MessageBus.Contracts.Mcp;
using MessageBus.Contracts.Messaging;
using MessageBus.Contracts.Runner;
using MessageBus.Contracts.Security;
using MessageBus.Crypto;
using MessageBus.Mcp;
using Xunit;

namespace MessageBus.Cli.Tests;

public sealed class TaskResealTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replay_command_refuses_old_service_without_content_requests_or_replacement_uploads(bool sameKeyBody)
    {
        using var machine = new TestMachine();
        using var device = KeyPair.GenerateP256();
        var store = new ConfigStore(machine.MbusFolder, "file");
        store.Save(new CliConfig { Server = FakeServer.Url, WorkspaceId = "ws_test", MachineId = "mch_test" });
        store.SaveKeyPem(device.ExportPem());
        store.SaveSession(new CliSessionFile { Server = FakeServer.Url, AccessToken = "test-access", RefreshToken = "test-refresh", AccessExpiresAt = machine.Time.GetUtcNow().AddHours(1) });
        using var oldPair = KeyPair.GenerateP256();
        using var newPair = KeyPair.GenerateP256();
        var destination = Key(newPair, "ek_new", "new-role");
        using var unused = Configure(machine, destination);
        var file = MessageCrypto.Seal("private file"u8, Key(oldPair, "ek_old", "old-role"));
        var body = sameKeyBody ? BodyCrypto.Seal(new MessageBody { Summary = "Already readable" }, destination).ToSealedBody() : null;
        var envelope = Envelope(body, [FileRef("att_original", file.Ciphertext) with { Encryption = file.Encryption }]);
        machine.Server.On(HttpMethod.Get, "/api/w/ws_test/deadletters", _ => FakeServer.Json<IReadOnlyList<DeadLetter>>([new()
        {
            Id = "dead_test", Envelope = envelope, Role = "old-role", Reason = DeadLetterReason.Expired,
            DeadLetteredAt = machine.Time.GetUtcNow(), ExpiresAt = machine.Time.GetUtcNow().AddDays(1),
        }]));

        var result = await machine.RunAsync("deadletters", "replay", "--to-role", "new-role", "dead_test");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Upgrade the service", result.Output + result.Error);
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath.Contains("attachments")
            || r.Uri.AbsolutePath.EndsWith("/body") || r.Uri.AbsolutePath.EndsWith("/replay"));
        Assert.DoesNotContain(machine.Server.Requests, r => r.Method == HttpMethod.Put || r.Method == HttpMethod.Delete);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"product\":\"messagebus.ai\"}")]
    [InlineData("{\"product\":\"other\",\"capabilities\":[\"replayIndependentInputsV1\"]}")]
    [InlineData("{\"product\":\"messagebus.ai\",\"capabilities\":{\"replayIndependentInputsV1\":true}}")]
    [InlineData("{\"product\":\"messagebus.ai\",\"capabilities\":[null,\"replayIndependentInputsV1\"]}")]
    [InlineData("{\"product\":\"messagebus.ai\",\"capabilities\":[\"replayIndependentInputsV2\"]}")]
    [InlineData("not json")]
    public async Task Independent_replay_refuses_unconfirmed_services_before_any_content_request(string discovery)
    {
        using var machine = new TestMachine();
        using var oldPair = KeyPair.GenerateP256();
        using var newPair = KeyPair.GenerateP256();
        var old = Key(oldPair, "ek_old", "old-role");
        var destination = Key(newPair, "ek_new", "new-role");
        using var http = Configure(machine, destination);
        machine.Server.On(HttpMethod.Get, "/api/server", _ => new(HttpStatusCode.OK) { Content = new StringContent(discovery) });
        using var keys = new MachineKeys(http, "ws_test", null, machine.Home, TextWriter.Null, machine.Time);
        var file = MessageCrypto.Seal("private input"u8, old);
        var envelope = Envelope(null, [FileRef("att_original", file.Ciphertext) with { Encryption = file.Encryption }]);

        var error = await Assert.ThrowsAsync<McpToolException>(() => TaskReseal.PrepareReplayAsync(keys, http,
            envelope, new RoleTarget { Role = "new-role" }, Ct));

        Assert.Equal(McpErrorCodes.Conflict, error.Error.Code);
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath.Contains("attachments") || r.Uri.AbsolutePath.EndsWith("/body"));
        Assert.DoesNotContain(machine.Server.Requests, r => r.Method == HttpMethod.Put || r.Method == HttpMethod.Delete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Independent_replay_discovery_errors_refuse_before_transfers(bool networkFailure)
    {
        using var machine = new TestMachine();
        using var oldPair = KeyPair.GenerateP256();
        using var newPair = KeyPair.GenerateP256();
        var destination = Key(newPair, "ek_destination", "new-role");
        using var http = Configure(machine, destination);
        machine.Server.On(HttpMethod.Get, "/api/server", _ => networkFailure
            ? throw new HttpRequestException("discovery unavailable") : new(HttpStatusCode.Forbidden));
        using var keys = new MachineKeys(http, "ws_test", null, machine.Home, TextWriter.Null, machine.Time);
        var file = MessageCrypto.Seal("input"u8, Key(oldPair, "ek_old", "old-role"));
        var envelope = Envelope(null, [FileRef("att_original", file.Ciphertext) with { Encryption = file.Encryption }]);

        await Assert.ThrowsAsync<McpToolException>(() => TaskReseal.PrepareReplayAsync(keys, http, envelope,
            new RoleTarget { Role = "new-role" }, Ct));

        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath.Contains("attachments") || r.Uri.AbsolutePath.EndsWith("/body"));
        Assert.DoesNotContain(machine.Server.Requests, r => r.Method == HttpMethod.Put || r.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Independent_replay_discovers_each_actual_target_afresh_and_then_reseals_only_files()
    {
        using var machine = new TestMachine();
        using var oldPair = KeyPair.GenerateP256();
        using var newPair = KeyPair.GenerateP256();
        var old = Key(oldPair, "ek_file", "old-role");
        var destination = Key(newPair, "ek_body", "new-role");
        using var http = Configure(machine, destination);
        using var links = AttachmentTransfers.CreateTestHttpClient(machine.Server, disposeHandler: false);
        using var keys = new MachineKeys(http, "ws_test", null, machine.Home, TextWriter.Null, machine.Time);
        keys.Ring.Add(old.KeyId, KeyPair.FromPem(oldPair.ExportPem()));
        var file = await SealFile("independent file"u8.ToArray(), old);
        var reference = FileRef("att_original", file.Ciphertext) with { Encryption = file.Encryption };
        ConfigureDownload(machine, reference, file.Ciphertext);
        var body = BodyCrypto.Seal(new MessageBody { Summary = "Already readable" }, destination).ToSealedBody();
        var envelope = Envelope(body, [reference]);
        machine.Server.On(HttpMethod.Get, "/api/server", _ => new(HttpStatusCode.OK)
        { Content = new StringContent("{\"product\":\"messagebus.ai\",\"capabilities\":[\"approvalAffectedV1\",\"replayIndependentInputsV1\",\"settleAttemptV1\"]}") });
        var target = new RoleTarget { Role = "new-role" };
        var recipient = await TaskReseal.PrepareReplayAsync(keys, http, envelope, target, Ct);
        var result = await TaskReseal.ResealAsync(keys, http, new AttachmentClient(http, links, "ws_test"), envelope, target, Ct,
            tempDirectory: machine.Home, recipientKey: recipient);
        Assert.Null(result.Body);
        var replacement = Assert.Single(result.Attachments!);
        using var ring = new KeyRing().Add(destination.KeyId, KeyPair.FromPem(newPair.ExportPem()));
        using var encrypted = new MemoryStream(machine.Server.Single(HttpMethod.Put, "/blobs/replacement").Body);
        using var plaintext = new MemoryStream();
        await MessageCrypto.OpenAsync(encrypted, plaintext, replacement.Encryption!, ring, Ct);
        Assert.Equal("independent file"u8.ToArray(), plaintext.ToArray());

        machine.Server.On(HttpMethod.Get, "/api/server", _ => new(HttpStatusCode.NotFound));
        await Assert.ThrowsAsync<McpToolException>(() => TaskReseal.PrepareReplayAsync(keys, http, envelope, target, Ct));
        Assert.Equal(2, machine.Server.Requests.Count(r => r.Uri.AbsolutePath == "/api/server"));
        Assert.Single(machine.Server.Requests, r => r.Method == HttpMethod.Put);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ordinary_replay_shapes_do_not_require_new_service_support(bool encrypted)
    {
        using var machine = new TestMachine();
        using var pair = KeyPair.GenerateP256();
        using var destinationPair = KeyPair.GenerateP256();
        var old = Key(pair, "ek_old", "old-role");
        var destination = Key(destinationPair, "ek_new", "new-role");
        using var http = Configure(machine, destination);
        using var keys = new MachineKeys(http, "ws_test", null, machine.Home, TextWriter.Null, machine.Time);
        var body = BodyCrypto.Seal(new MessageBody { Summary = "Legacy replay" }, old).ToSealedBody();
        var file = MessageCrypto.Seal("legacy input"u8, old);
        var envelope = encrypted ? Envelope(body, [FileRef("att_old", file.Ciphertext) with { Encryption = file.Encryption }]) : Envelope(null, []);

        await TaskReseal.PrepareReplayAsync(keys, http, envelope, new RoleTarget { Role = "new-role" }, Ct);

        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath == "/api/server" || r.Uri.AbsolutePath.Contains("attachments"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Body_and_each_attachment_are_independent_and_plain_references_stay_exact(bool encryptedBody, bool encryptedFile)
    {
        using var machine = new TestMachine();
        using var oldPair = KeyPair.GenerateP256();
        using var newPair = KeyPair.GenerateP256();
        var oldKey = Key(oldPair, "ek_old", "old-role");
        var newKey = Key(newPair, "ek_new", "new-role");
        using var http = Configure(machine, newKey);
        using var links = AttachmentTransfers.CreateTestHttpClient(machine.Server, disposeHandler: false);
        using var keys = new MachineKeys(http, "ws_test", null, machine.Home, TextWriter.Null, machine.Time);
        keys.Ring.Add(oldKey.KeyId, KeyPair.FromPem(oldPair.ExportPem()));
        var attachments = new AttachmentClient(http, links, "ws_test", machine.Home);
        var plain = FileRef("att_plain", "plain file"u8.ToArray());
        var payload = "the encrypted attachment"u8.ToArray();
        var sealedFile = await SealFile(payload, oldKey);
        var encrypted = FileRef("att_encrypted", sealedFile.Ciphertext) with { Encryption = sealedFile.Encryption };
        var sameBytes = MessageCrypto.Seal("already readable"u8, newKey);
        var same = FileRef("att_same", sameBytes.Ciphertext) with { Encryption = sameBytes.Encryption };
        ConfigureDownload(machine, encrypted, sealedFile.Ciphertext);
        var envelope = Envelope(encryptedBody ? BodyCrypto.Seal(new MessageBody { Summary = "Do the work" }, oldKey).ToSealedBody() : null,
            encryptedFile ? [plain, encrypted, same, plain] : [plain]);

        var result = await TaskReseal.ResealAsync(keys, http, attachments, envelope, new RoleTarget { Role = "new-role" }, Ct,
            tempDirectory: machine.Home);

        using var destination = new KeyRing().Add(newKey.KeyId, KeyPair.FromPem(newPair.ExportPem()));
        if (encryptedBody)
            Assert.Equal("Do the work", BodyCrypto.Open(Assert.IsType<SealedBody>(result.Body), destination).Summary);
        else Assert.Null(result.Body);
        if (encryptedFile)
        {
            Assert.NotNull(result.Attachments);
            Assert.Equal(4, result.Attachments.Count);
            Assert.Same(plain, result.Attachments[0]);
            Assert.Same(same, result.Attachments[2]);
            Assert.Same(plain, result.Attachments[3]);
            var replacement = result.Attachments[1];
            Assert.Equal("att_replacement", replacement.Id);
            Assert.Equal(newKey.KeyId, replacement.Encryption!.KeyId);
            var uploaded = machine.Server.Single(HttpMethod.Put, "/blobs/replacement");
            using var uploadedStream = new MemoryStream(uploaded.Body);
            using var opened = new MemoryStream();
            await MessageCrypto.OpenAsync(uploadedStream, opened, replacement.Encryption, destination, Ct);
            Assert.Equal(payload, opened.ToArray());
            Assert.Equal(encrypted.Name, replacement.Name);
            Assert.Equal(encrypted.ContentType, replacement.ContentType);
            Assert.Equal(encrypted.Branch, replacement.Branch);
            Assert.Equal(encrypted.BaseCommit, replacement.BaseCommit);
            var request = MessageBusJson.Deserialize<UploadLinkRequest>(machine.Server.Single(HttpMethod.Post, "/api/w/ws_test/attachments/upload-link").BodyText)!;
            Assert.Equal("thr_task", request.ThreadId);
            Assert.Equal("new-role", Assert.IsType<RoleTarget>(request.Target).Role);
        }
        else
        {
            Assert.Null(result.Attachments);
            Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath.Contains("attachments"));
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(machine.Home, "mbus-reseal-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_attachment_transfers_clean_plaintext_and_partial_files_without_deleting_originals(bool failUpload)
    {
        using var machine = new TestMachine();
        using var oldPair = KeyPair.GenerateP256();
        using var newPair = KeyPair.GenerateP256();
        var oldKey = Key(oldPair, "ek_old", "old-role");
        var newKey = Key(newPair, "ek_new", "new-role");
        using var http = Configure(machine, newKey);
        using var links = AttachmentTransfers.CreateTestHttpClient(machine.Server, disposeHandler: false);
        using var keys = new MachineKeys(http, "ws_test", null, machine.Home, TextWriter.Null, machine.Time);
        keys.Ring.Add(oldKey.KeyId, KeyPair.FromPem(oldPair.ExportPem()));
        var sealedFile = await SealFile("private task input"u8.ToArray(), oldKey);
        var file = FileRef("att_original", sealedFile.Ciphertext) with { Encryption = sealedFile.Encryption };
        ConfigureDownload(machine, file, sealedFile.Ciphertext);
        if (failUpload)
        {
            machine.Server.On(HttpMethod.Post, "/api/w/ws_test/attachments/upload-link", _ =>
            {
                // The decrypted file exists at the failure point, so cleanup must remove it.
                var scratch = Assert.Single(Directory.EnumerateDirectories(machine.Home, "mbus-reseal-*"));
                Assert.Single(Directory.EnumerateFiles(scratch));
                if (!OperatingSystem.IsWindows())
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(scratch));
                return new(HttpStatusCode.ServiceUnavailable);
            });
        }
        else file = file with { Sha256 = new string('0', 64) };

        await Assert.ThrowsAsync<AttachmentTransferException>(() => TaskReseal.ResealAsync(keys, http,
            new AttachmentClient(http, links, "ws_test"), Envelope(null, [file]),
            new RoleTarget { Role = "new-role" }, Ct, tempDirectory: machine.Home));

        Assert.Empty(Directory.EnumerateFileSystemEntries(machine.Home, "mbus-reseal-*"));
        Assert.DoesNotContain(machine.Server.Requests, r => r.Method == HttpMethod.Delete || r.Method == HttpMethod.Put);
        if (failUpload)
            Assert.Single(machine.Server.Requests.Where(r => r.Method == HttpMethod.Post &&
                r.Uri.AbsolutePath.EndsWith("/upload-link", StringComparison.Ordinal)));
        else
            Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath.EndsWith("/upload-link", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Same_destination_key_preserves_body_and_files_without_downloads_or_uploads()
    {
        using var machine = new TestMachine();
        using var pair = KeyPair.GenerateP256();
        var key = Key(pair, "ek_shared", null);
        using var http = Configure(machine, key);
        using var links = AttachmentTransfers.CreateTestHttpClient(machine.Server, disposeHandler: false);
        using var keys = new MachineKeys(http, "ws_test", null, machine.Home, TextWriter.Null, machine.Time);
        // This machine doesn't need the private key when all inputs already use the destination key.
        var sealedFile = MessageCrypto.Seal("payload"u8, key);
        var file = FileRef("att_shared", sealedFile.Ciphertext) with { Encryption = sealedFile.Encryption };
        var envelope = Envelope(BodyCrypto.Seal(new MessageBody { Summary = "Do the work" }, key).ToSealedBody(), [file]);

        var result = await TaskReseal.ResealAsync(keys, http, new AttachmentClient(http, links, "ws_test"), envelope,
            new AgentTarget { AgentId = "agt_new" }, Ct);

        Assert.Null(result.Body);
        Assert.Null(result.Attachments);
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath.Contains("attachments") || r.Uri.AbsolutePath.Contains("/body"));
    }

    [Fact]
    public async Task Missing_key_on_a_later_attachment_prevents_all_downloads_and_uploads()
    {
        using var machine = new TestMachine();
        using var firstPair = KeyPair.GenerateP256();
        using var missingPair = KeyPair.GenerateP256();
        using var destinationPair = KeyPair.GenerateP256();
        var firstKey = Key(firstPair, "ek_first", "first");
        var missingKey = Key(missingPair, "ek_missing", "missing");
        var destinationKey = Key(destinationPair, "ek_destination", "new-role");
        using var http = Configure(machine, destinationKey);
        using var links = AttachmentTransfers.CreateTestHttpClient(machine.Server, disposeHandler: false);
        using var keys = new MachineKeys(http, "ws_test", null, machine.Home, TextWriter.Null, machine.Time);
        keys.Ring.Add(firstKey.KeyId, KeyPair.FromPem(firstPair.ExportPem()));
        var first = MessageCrypto.Seal("first"u8, firstKey);
        var second = MessageCrypto.Seal("second"u8, missingKey);
        var envelope = Envelope(null,
        [
            FileRef("att_first", first.Ciphertext) with { Encryption = first.Encryption },
            FileRef("att_second", second.Ciphertext) with { Encryption = second.Encryption },
        ]);
        var error = await Assert.ThrowsAsync<MissingKeyException>(() => TaskReseal.ResealAsync(keys, http,
            new AttachmentClient(http, links, "ws_test"), envelope, new RoleTarget { Role = "new-role" }, Ct));
        Assert.Equal("ek_missing", error.KeyId);
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath.Contains("attachments"));
    }

    [Fact]
    public async Task A_rotated_destination_key_invalidates_the_preview_before_content_transfers()
    {
        using var machine = new TestMachine();
        using var pair = KeyPair.GenerateP256();
        var key = Key(pair, "ek_current", "new-role");
        using var http = Configure(machine, key);
        using var links = AttachmentTransfers.CreateTestHttpClient(machine.Server, disposeHandler: false);
        using var keys = new MachineKeys(http, "ws_test", null, machine.Home, TextWriter.Null, machine.Time);
        var envelope = Envelope(BodyCrypto.Seal(new MessageBody { Summary = "Do the work" }, key).ToSealedBody(), []);

        var error = await Assert.ThrowsAsync<McpToolException>(() => TaskReseal.ResealAsync(keys, http,
            new AttachmentClient(http, links, "ws_test"), envelope, new RoleTarget { Role = "new-role" }, Ct, "ek_preview"));

        Assert.Equal(McpErrorCodes.Conflict, error.Error.Code);
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath.Contains("attachments") || r.Uri.AbsolutePath.Contains("/body"));
    }

    [Fact]
    public async Task Stored_large_body_is_read_verified_and_resealed_without_attachment_uploads()
    {
        using var machine = new TestMachine();
        using var oldPair = KeyPair.GenerateP256();
        using var newPair = KeyPair.GenerateP256();
        var oldKey = Key(oldPair, "ek_old", "old-role");
        var newKey = Key(newPair, "ek_new", "new-role");
        using var http = Configure(machine, newKey);
        using var links = AttachmentTransfers.CreateTestHttpClient(machine.Server, disposeHandler: false);
        using var keys = new MachineKeys(http, "ws_test", null, machine.Home, TextWriter.Null, machine.Time);
        keys.Ring.Add(oldKey.KeyId, KeyPair.FromPem(oldPair.ExportPem()));
        var original = new MessageBody { Summary = "Large body", Details = new string('x', 70000) };
        var ciphertext = BodyCrypto.Seal(original, oldKey);
        var body = new SealedBody { Body = new BodyRef { SizeBytes = ciphertext.Ciphertext.Length, Sha256 = ciphertext.Sha256, BlobName = "body/example" }, Encryption = ciphertext.Encryption };
        machine.Server.On(HttpMethod.Get, "/api/w/ws_test/threads/thr_task/messages/msg_old/body", _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(ciphertext.Ciphertext) });
        var result = await TaskReseal.ResealAsync(keys, http, new AttachmentClient(http, links, "ws_test"), Envelope(body, []), new RoleTarget { Role = "new-role" }, Ct);
        using var destination = new KeyRing().Add(newKey.KeyId, KeyPair.FromPem(newPair.ExportPem()));
        Assert.Equal(original.Details, BodyCrypto.Open(Convert.FromBase64String(result.Body!.Body.Data!), result.Body.Encryption, destination).Details);
        Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath.Contains("attachments"));
    }

    private static async Task<SealedBytes> SealFile(byte[] plaintext, PublicKeyInfo key)
    {
        using var source = new MemoryStream(plaintext);
        using var destination = new MemoryStream();
        var result = await MessageCrypto.SealAsync(source, destination, key, Ct);
        return new SealedBytes(destination.ToArray(), result.Encryption);
    }

    private static MessageEnvelope Envelope(SealedBody? body, IReadOnlyList<AttachmentRef> files) => new()
    {
        Id = "msg_old", ThreadId = "thr_task", WorkspaceId = "ws_test", Source = "person://ws_test/person_test",
        Type = "task.implement", Time = DateTimeOffset.UnixEpoch, Sender = new SenderIdentity { Kind = SenderKind.Person },
        Target = new RoleTarget { Role = "old-role" }, Body = body?.Body ?? new BodyRef { SizeBytes = 0, Sha256 = "empty" },
        Encryption = body?.Encryption, Attachments = files,
    };

    private static PublicKeyInfo Key(KeyPair pair, string id, string? role) => new()
    {
        KeyId = id, Scope = role is null ? KeyScope.Workspace : KeyScope.Role, Owner = role, Version = 1,
        Algorithm = pair.PublicKey.Algorithm, PublicKey = pair.PublicKey.ToBase64(), Fingerprint = pair.PublicKey.Fingerprint, CreatedAt = DateTimeOffset.UnixEpoch,
    };

    private static AttachmentRef FileRef(string id, byte[] bytes) => new()
    {
        Id = id, Name = id + ".txt", Format = AttachmentFormat.File, SizeBytes = bytes.Length,
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), ContentType = "text/plain", Branch = "feature/example", BaseCommit = "example-base",
    };

    private static void ConfigureDownload(TestMachine machine, AttachmentRef file, byte[] bytes)
    {
        machine.Server.On(HttpMethod.Post, $"/api/w/ws_test/attachments/{file.Id}/download-link", _ => FakeServer.Json(new DownloadLink
        { Id = file.Id, Url = FakeServer.Url + "/blobs/" + file.Id, ExpiresAt = machine.Time.GetUtcNow().AddHours(1) }));
        machine.Server.On(HttpMethod.Get, "/blobs/" + file.Id, _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
    }

    private static HttpClient Configure(TestMachine machine, PublicKeyInfo destination)
    {
        machine.Server.On(HttpMethod.Get, "/api/caller", _ => new(HttpStatusCode.OK) { Content = new StringContent("{\"subjectId\":\"person_test\",\"permissions\":[]}", Encoding.UTF8, "application/json") });
        machine.Server.On(HttpMethod.Get, "/api/w/ws_test/keys", _ => FakeServer.Json<IReadOnlyList<PublicKeyInfo>>([destination]));
        machine.Server.On(HttpMethod.Get, "/api/w/ws_test/keys/rotation", _ => FakeServer.Json<IReadOnlyList<KeyRotationState>>([new() { Scope = destination.Scope, Owner = destination.Owner, Key = destination }]));
        machine.Server.On(HttpMethod.Post, "/api/w/ws_test/attachments/upload-link", _ => FakeServer.Json(new UploadLink
        { AttachmentId = "att_replacement", Url = FakeServer.Url + "/blobs/replacement", MaxBytes = long.MaxValue, ExpiresAt = machine.Time.GetUtcNow().AddHours(1) }));
        machine.Server.On(HttpMethod.Put, "/blobs/replacement", _ => new(HttpStatusCode.Created));
        return new HttpClient(machine.Server, disposeHandler: false) { BaseAddress = new Uri(FakeServer.Url) };
    }
}
