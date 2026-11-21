using System.Buffers.Text;
using System.Globalization;

namespace MessageBus.Contracts.Security;

/// <summary>The frozen, authority-bound ES256/P1363 machine signature domain. V1 remains unchanged.</summary>
public static class MachineRequestSigningV2
{
    /// <summary>The sole supported version header value.</summary>
    public const string Version = "2";

    /// <summary>Builds the ten LF-separated fields, with no trailing LF. Hash the exact transmitted body first.</summary>
    public static string CanonicalString(string canonicalAuthority, string stampId, string machineId,
        string? workspaceHeader, string method, string pathAndQuery, long timestamp, string nonce, string bodySha256Hex)
    {
        if (ProtocolOrigin.Normalize(canonicalAuthority) != canonicalAuthority)
            throw new ArgumentException("Authority must already be normalized.", nameof(canonicalAuthority));
        if (stampId is not ("ca" or "eu" or "us")) throw new ArgumentException("Unsupported stamp.", nameof(stampId));
        if (!ContractIds.IsValid(machineId, ContractIds.Machine)) throw new ArgumentException("Invalid machine ID.", nameof(machineId));
        if (workspaceHeader is not null && (workspaceHeader.Length == 0
            || workspaceHeader.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-'))))
            throw new ArgumentException("Invalid workspace header.", nameof(workspaceHeader));
        if (string.IsNullOrEmpty(method) || method.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c)))
            throw new ArgumentException("Invalid HTTP method.", nameof(method));
        ValidateRawTarget(pathAndQuery);
        if (timestamp < 0 || timestamp > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            throw new ArgumentOutOfRangeException(nameof(timestamp));
        if (!IsCanonicalBase64Url(nonce, 16)) throw new ArgumentException("Nonce must encode exactly 16 bytes.", nameof(nonce));
        if (bodySha256Hex is null || bodySha256Hex.Length != 64 || bodySha256Hex.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("A lowercase SHA-256 hash is required.", nameof(bodySha256Hex));
        return string.Join('\n', "mbus-machine-v2", canonicalAuthority, stampId, machineId, workspaceHeader ?? "",
            method.ToUpperInvariant(), pathAndQuery, timestamp.ToString(CultureInfo.InvariantCulture), nonce, bodySha256Hex);
    }

    /// <summary>Checks a raw escaped origin-form target without decoding, sorting or rewriting it.</summary>
    public static void ValidateRawTarget(string target)
    {
        if (string.IsNullOrEmpty(target) || target[0] != '/' || target.StartsWith("//", StringComparison.Ordinal)
            || target.Any(c => c <= ' ' || c >= 127 || c is '\\' or '#'))
            throw new ArgumentException("An escaped origin-form raw target is required.", nameof(target));
        for (var i = 0; i < target.Length; i++)
        {
            if (target[i] != '%') continue;
            if (i + 2 >= target.Length || !Uri.IsHexDigit(target[i + 1]) || !Uri.IsHexDigit(target[i + 2]))
                throw new ArgumentException("Malformed target escape.", nameof(target));
            i += 2;
        }
    }

    /// <summary>Checks exact unpadded base64url encoding, including unused bits, for a fixed byte count.</summary>
    public static bool IsCanonicalBase64Url(string? value, int byteCount)
    {
        if (value is null || byteCount < 0 || value.Length != (byteCount * 8L + 5) / 6
            || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_'))) return false;
        try
        {
            var bytes = Base64Url.DecodeFromChars(value);
            return bytes.Length == byteCount && Base64Url.EncodeToString(bytes) == value;
        }
        catch (FormatException) { return false; }
    }

    /// <summary>Validates canonical decimal Unix seconds from a single timestamp header value.</summary>
    public static bool TryParseTimestamp(string? value, out long timestamp)
    {
        timestamp = 0;
        return value is { Length: > 0 } && (value.Length == 1 || value[0] != '0')
            && value.All(c => c is >= '0' and <= '9')
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out timestamp)
            && timestamp <= DateTimeOffset.MaxValue.ToUnixTimeSeconds();
    }

    /// <summary>Validates the single version value; duplicates and comma-joined values never select v2.</summary>
    public static bool IsVersionHeader(IReadOnlyList<string> values) => values.Count == 1 && values[0] == Version;
}
