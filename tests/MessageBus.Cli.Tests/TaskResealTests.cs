using System.Net;
using System.Security.Cryptography;
using System.Text;
using MessageBus.Attachments.Client;
using MessageBus.Cli.Tests.Infrastructure;
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
        using var keys = new MachineKeys(http, "ws_test", null, machine.Home, TextWriter.Null, machine.Time);
        keys.Ring.Add(oldKey.KeyId, KeyPair.FromPem(oldPair.ExportPem()));
        var attachments = new AttachmentClient(http, http, "ws_test", machine.Home);
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
        Assert.Empty(Directory.EnumerateFiles(machine.Home, "mbus-reseal-*"));
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
                Assert.Single(Directory.EnumerateFiles(machine.Home, "mbus-reseal-*"));
                return new(HttpStatusCode.ServiceUnavailable);
            });
        }
        else file = file with { Sha256 = new string('0', 64) };

        await Assert.ThrowsAsync<AttachmentTransferException>(() => TaskReseal.ResealAsync(keys, http,
            new AttachmentClient(http, http, "ws_test"), Envelope(null, [file]),
            new RoleTarget { Role = "new-role" }, Ct, tempDirectory: machine.Home));

        Assert.Empty(Directory.EnumerateFiles(machine.Home, "mbus-reseal-*"));
        Assert.DoesNotContain(machine.Server.Requests, r => r.Method == HttpMethod.Delete || r.Method == HttpMethod.Put);
        if (!failUpload)
            Assert.DoesNotContain(machine.Server.Requests, r => r.Uri.AbsolutePath.EndsWith("/upload-link", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Same_destination_key_preserves_body_and_files_without_downloads_or_uploads()
    {
        using var machine = new TestMachine();
        using var pair = KeyPair.GenerateP256();
        var key = Key(pair, "ek_shared", null);
        using var http = Configure(machine, key);
        using var keys = new MachineKeys(http, "ws_test", null, machine.Home, TextWriter.Null, machine.Time);
        // This machine doesn't need the private key when all inputs already use the destination key.
        var sealedFile = MessageCrypto.Seal("payload"u8, key);
        var file = FileRef("att_shared", sealedFile.Ciphertext) with { Encryption = sealedFile.Encryption };
        var envelope = Envelope(BodyCrypto.Seal(new MessageBody { Summary = "Do the work" }, key).ToSealedBody(), [file]);

        var result = await TaskReseal.ResealAsync(keys, http, new AttachmentClient(http, http, "ws_test"), envelope,
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
            new AttachmentClient(http, http, "ws_test"), envelope, new RoleTarget { Role = "new-role" }, Ct));
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
        using var keys = new MachineKeys(http, "ws_test", null, machine.Home, TextWriter.Null, machine.Time);
        var envelope = Envelope(BodyCrypto.Seal(new MessageBody { Summary = "Do the work" }, key).ToSealedBody(), []);

        var error = await Assert.ThrowsAsync<McpToolException>(() => TaskReseal.ResealAsync(keys, http,
            new AttachmentClient(http, http, "ws_test"), envelope, new RoleTarget { Role = "new-role" }, Ct, "ek_preview"));

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
        using var keys = new MachineKeys(http, "ws_test", null, machine.Home, TextWriter.Null, machine.Time);
        keys.Ring.Add(oldKey.KeyId, KeyPair.FromPem(oldPair.ExportPem()));
        var original = new MessageBody { Summary = "Large body", Details = new string('x', 70000) };
        var ciphertext = BodyCrypto.Seal(original, oldKey);
        var body = new SealedBody { Body = new BodyRef { SizeBytes = ciphertext.Ciphertext.Length, Sha256 = ciphertext.Sha256, BlobName = "body/example" }, Encryption = ciphertext.Encryption };
        machine.Server.On(HttpMethod.Get, "/api/w/ws_test/threads/thr_task/messages/msg_old/body", _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(ciphertext.Ciphertext) });
        var result = await TaskReseal.ResealAsync(keys, http, new AttachmentClient(http, http, "ws_test"), Envelope(body, []), new RoleTarget { Role = "new-role" }, Ct);
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
