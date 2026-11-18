using System.Security.Cryptography;

namespace MessageBus.Contracts;

/// <summary>
/// Identifier prefixes and the one id format every component uses. An id is
/// <c>{prefix}_{26 lowercase Crockford base32 characters}</c>: 48 bits of milliseconds since the Unix
/// epoch followed by 80 random bits (the ULID layout, lowercased). Ids made later sort after ids made
/// earlier under ordinal comparison, and they are safe inside table keys, blob names and (after
/// mapping <c>_</c> to <c>-</c>) queue names.
/// </summary>
/// <remarks>Workspace ids predate this format (<c>w101</c>, from the template) and are kept as they are.</remarks>
public static class ContractIds
{
    /// <summary>A message (<c>msg_…</c>).</summary>
    public const string Message = "msg";
    /// <summary>A thread, one task and everything said about it (<c>thr_…</c>).</summary>
    public const string Thread = "thr";
    /// <summary>A project (<c>prj_…</c>).</summary>
    public const string Project = "prj";
    /// <summary>An enrolled machine (<c>mch_…</c>).</summary>
    public const string Machine = "mch";
    /// <summary>One coding agent on one machine (<c>agt_…</c>).</summary>
    public const string Agent = "agt";
    /// <summary>One delivery of a message to one agent, with its lease (<c>dlv_…</c>).</summary>
    public const string Delivery = "dlv";
    /// <summary>An attachment blob (<c>att_…</c>).</summary>
    public const string Attachment = "att";
    /// <summary>An event subscription created by the <c>subscribe</c> tool (<c>sub_…</c>).</summary>
    public const string Subscription = "sub";
    /// <summary>A temporary session inbox (an orchestrator's, for replies) (<c>inb_…</c>).</summary>
    public const string Inbox = "inb";
    /// <summary>An API key (<c>key_…</c>).</summary>
    public const string ApiKey = "key";
    /// <summary>A one-time enrollment token for a shared runner (<c>enr_…</c>).</summary>
    public const string EnrollmentToken = "enr";
    /// <summary>An encryption key pair version (<c>ek_…</c>).</summary>
    public const string EncryptionKey = "ek";
    /// <summary>A signed key grant giving a machine a role's private key (<c>grt_…</c>).</summary>
    public const string KeyGrant = "grt";
    /// <summary>A route ("when this happens, send that") (<c>rte_…</c>).</summary>
    public const string Route = "rte";
    /// <summary>An alert rule (<c>alr_…</c>).</summary>
    public const string AlertRule = "alr";
    /// <summary>One firing of an alert (<c>alt_…</c>) (the original issue).</summary>
    public const string Alert = "alt";
    /// <summary>A place alerts are sent: Teams, email or a webhook (<c>ald_…</c>) (the original issue).</summary>
    public const string AlertDestination = "ald";
    /// <summary>An inbound webhook another system posts events to (<c>whk_…</c>) (the original issue).</summary>
    public const string InboundWebhook = "whk";
    /// <summary>A cloud agent: a URL each message for a role is posted to (<c>cag_…</c>) (the original issue).</summary>
    public const string CloudAgent = "cag";
    /// <summary>A notification for one person (<c>ntf_…</c>).</summary>
    public const string Notification = "ntf";
    /// <summary>A request from an agent for a person's approval (<c>apr_…</c>) (the original issue).</summary>
    public const string Approval = "apr";
    /// <summary>An audit event (<c>aud_…</c>).</summary>
    public const string AuditEvent = "aud";
    /// <summary>A role join request (<c>rjr_…</c>).</summary>
    public const string RoleJoinRequest = "rjr";
    /// <summary>A thread event in a <c>watch</c> stream (<c>evt_…</c>).</summary>
    public const string ThreadEvent = "evt";
    /// <summary>An invitation to a workspace (<c>inv_…</c>).</summary>
    public const string Invite = "inv";
    /// <summary>A signed-in dashboard session (<c>ses_…</c>).</summary>
    public const string Session = "ses";
    /// <summary>A staged runner rollout (<c>rol_…</c>, the original issue).</summary>
    public const string Rollout = "rol";

    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    /// <summary>Length of the part after <c>{prefix}_</c>.</summary>
    public const int BodyLength = 26;

    /// <summary>A new id with the given prefix, stamped with <paramref name="now"/> (pass the injected <see cref="TimeProvider"/>'s time).</summary>
    /// <param name="prefix">One of the prefix constants on this class.</param>
    /// <param name="now">The current time; ids sort by it.</param>
    public static string New(string prefix, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(prefix);
        Span<byte> bytes = stackalloc byte[16];
        var ms = (ulong)Math.Max(0, now.ToUnixTimeMilliseconds());
        for (var i = 5; i >= 0; i--)
        {
            bytes[i] = (byte)(ms & 0xFF);
            ms >>= 8;
        }
        RandomNumberGenerator.Fill(bytes[6..]);

        // 128 bits -> 26 base32 characters (the first carries the top 3 bits).
        Span<char> chars = stackalloc char[BodyLength];
        UInt128 value = 0;
        foreach (var b in bytes) value = (value << 8) | b;
        for (var i = BodyLength - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 31)];
            value >>= 5;
        }
        return string.Concat(prefix, "_", chars);
    }

    /// <summary>True when <paramref name="id"/> has the given prefix and a well-formed body.</summary>
    /// <param name="id">The id to check.</param>
    /// <param name="prefix">The expected prefix.</param>
    public static bool IsValid(string? id, string prefix)
    {
        if (id is null || id.Length != prefix.Length + 1 + BodyLength) return false;
        if (!id.StartsWith(prefix, StringComparison.Ordinal) || id[prefix.Length] != '_') return false;
        foreach (var c in id.AsSpan(prefix.Length + 1))
        {
            if (!Alphabet.Contains(c)) return false;
        }
        return true;
    }
}
