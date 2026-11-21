using System.Buffers.Binary;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using MessageBus.Cli.Configuration;
using MessageBus.Cli.Hosting;

namespace MessageBus.Cli.Runner.Service;

internal sealed record ServiceEnrollment(string Server, string ApiKey);
internal sealed record ServiceRequest(string Operation, ServiceEnrollment? Enrollment = null, Dictionary<string, string>? Tokens = null);
internal sealed record ServiceResponse(bool Ok, string[]? TokenNames = null, bool Enrolled = false);

[SupportedOSPlatform("windows")]
internal static class WindowsServiceControl
{
    private const int Limit = 65536;

    internal static async Task<ServiceResponse> SendAsync(string pipeName, WindowsServiceScm.ScHandle service, ServiceRequest request, TimeProvider time, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35), time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeAccessRights.ReadWrite, PipeOptions.Asynchronous,
                TokenImpersonationLevel.Identification, HandleInheritability.None);
            await pipe.ConnectAsync(stop.Token);
            var status = WindowsServiceScm.Status(service);
            if (status.State != System.ServiceProcess.ServiceControllerStatus.Running
                || !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != status.ProcessId)
                throw new CliException("The Windows runner control pipe does not belong to the registered service process.");
            await WriteRequestAsync(pipe, request, stop.Token);
            var result = JsonSerializer.Deserialize<ServiceResponse>(await ReadFrameAsync(pipe, stop.Token));
            if (result is not { Ok: true })
                throw new CliException("The Windows runner service rejected the request. Check its supervisor log for details.");
            return result;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new CliException("The Windows runner service control request timed out.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or JsonException)
        {
            throw new CliException("The Windows runner service control request could not be completed.");
        }
    }

    internal static async Task ServeAsync(string pipeName, Func<ServiceRequest, CancellationToken, Task<ServiceResponse>> handle,
        Action<string> log, TimeProvider time, CancellationToken ct)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var rules = new PipeSecurity();
        rules.SetAccessRuleProtection(true, false);
        rules.AddAccessRule(new PipeAccessRule(WindowsServicePaths.Administrators, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        rules.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 4096, 4096, rules);
        while (true)
        {
            await pipe.WaitForConnectionAsync(ct);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), time);
            using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                var request = await ReadRequestAsync(pipe, requestCancellation.Token);
                ServiceResponse response;
                try { response = await handle(request, requestCancellation.Token); }
                catch (Exception ex) when (ex is CliException or IOException or UnauthorizedAccessException or CryptographicException)
                {
                    log($"Control request failed ({ex.GetType().Name}).");
                    response = new(false);
                }
                await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(response), requestCancellation.Token);
                // DisconnectNamedPipe discards unread data. Let the client close after reading its reply.
                _ = await pipe.ReadAsync(new byte[1], requestCancellation.Token);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or CryptographicException or JsonException
                || ex is OperationCanceledException && timeout.IsCancellationRequested && !ct.IsCancellationRequested) { }
            finally { pipe.Disconnect(); }
        }
    }

    // One ephemeral ECDH exchange per authenticated pipe connection. Only the service receives plaintext.
    internal static async Task WriteRequestAsync(Stream pipe, ServiceRequest request, CancellationToken ct)
    {
        using var agreement = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var peer = await ReadFrameAsync(pipe, ct);
        await WriteFrameAsync(pipe, agreement.ExportSubjectPublicKeyInfo(), ct);
        using var cipher = Cipher(agreement, peer);
        var plain = JsonSerializer.SerializeToUtf8Bytes(request);
        try
        {
            var packet = new byte[12 + 16 + plain.Length];
            RandomNumberGenerator.Fill(packet.AsSpan(0, 12));
            cipher.Encrypt(packet.AsSpan(0, 12), plain, packet.AsSpan(28), packet.AsSpan(12, 16));
            await WriteFrameAsync(pipe, packet, ct);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal static async Task<ServiceRequest> ReadRequestAsync(Stream pipe, CancellationToken ct)
    {
        using var agreement = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        await WriteFrameAsync(pipe, agreement.ExportSubjectPublicKeyInfo(), ct);
        using var cipher = Cipher(agreement, await ReadFrameAsync(pipe, ct));
        var packet = await ReadFrameAsync(pipe, ct);
        if (packet.Length < 28) throw new InvalidDataException("Incomplete service control request.");
        var plain = new byte[packet.Length - 28];
        try
        {
            cipher.Decrypt(packet.AsSpan(0, 12), packet.AsSpan(28), packet.AsSpan(12, 16), plain);
            return JsonSerializer.Deserialize<ServiceRequest>(plain) ?? throw new InvalidDataException("Missing service request.");
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static AesGcm Cipher(ECDiffieHellman local, byte[] peerBytes)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(peerBytes, out _);
        using var publicKey = peer.PublicKey;
        var key = local.DeriveKeyFromHash(publicKey, HashAlgorithmName.SHA256);
        try { return new AesGcm(key, 16); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > Limit) throw new InvalidDataException("Invalid service control frame length.");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return body;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] body, CancellationToken ct)
    {
        if (body.Length is <= 0 or > Limit) throw new InvalidDataException("Invalid service control frame length.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
}

[SupportedOSPlatform("windows")]
internal static class WindowsServiceEnrollment
{
    private static readonly byte[] Entropy = "messagebus.ai windows service enrollment v1"u8.ToArray();

    private static string FilePath(string settings) => Path.Combine(settings, "bootstrap.dpapi");

    internal static bool IsPending(string settings) => File.Exists(FilePath(settings));

    internal static void Clear(string settings) => File.Delete(FilePath(settings));

    internal static void Save(string settings, ServiceEnrollment enrollment)
    {
        DurableProfileFile.CreatePrivateDirectory(settings);
        DurableProfileFile.ValidatePrivateDirectory(settings);
        var plain = JsonSerializer.SerializeToUtf8Bytes(enrollment);
        try
        {
            var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            // Publish an entire, durable checkpoint. Reconfiguration cannot replace an existing identity.
            var path = FilePath(settings);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    output.Write(encrypted);
                    output.Flush(flushToDisk: true);
                }
                File.Move(temp, path);
            }
            finally { File.Delete(temp); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal static ServiceEnrollment? Read(string settings)
    {
        if (!IsPending(settings)) return null;
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath(settings)), Entropy, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<ServiceEnrollment>(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal static ServiceEnrollment Validate(string? server, string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 8192 || key.Any(char.IsControl))
            throw new CliException($"Set {EnvVars.ApiKey} or {EnvVars.ApiKey}_FILE to a runner-scoped API key.");
        if (server is null || server.Length > 2048)
            throw new CliException($"Specify the service's HTTPS server with --server or {EnvVars.Server}.");
        if (!Uri.TryCreate(server.Trim(), UriKind.Absolute, out var address)
            || address.Scheme != Uri.UriSchemeHttps || address.UserInfo.Length != 0
            || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new CliException("The Windows runner service requires an HTTPS server address without credentials.");
        return new(Commands.CommandContext.ServerText(address), key.Trim());
    }
}
