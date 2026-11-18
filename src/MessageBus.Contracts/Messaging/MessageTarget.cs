using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace MessageBus.Contracts.Messaging;

/// <summary>
/// Who a message is for. On the wire it is an object with a <c>kind</c> discriminator
/// (<c>{"kind":"role","role":"developer"}</c>); on a command line or in a tool argument it can also be
/// written as text, <c>role:developer</c>, <c>agent:agt_…</c>, <c>inbox:inb_…</c> or
/// <c>capabilities:os:macos,tool:dotnet</c> (see <see cref="TryParse"/>).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(RoleTarget), RoleTarget.Kind)]
[JsonDerivedType(typeof(AgentTarget), AgentTarget.Kind)]
[JsonDerivedType(typeof(CapabilityTarget), CapabilityTarget.Kind)]
[JsonDerivedType(typeof(InboxTarget), InboxTarget.Kind)]
public abstract record MessageTarget
{
    /// <summary>
    /// Parses the text form. Role names and ids are kept as written (validation is the receiver's
    /// job); capabilities are split on commas and trimmed.
    /// </summary>
    /// <param name="text">For example <c>role:developer</c>.</param>
    /// <param name="target">The parsed target, or null.</param>
    public static bool TryParse(string? text, [NotNullWhen(true)] out MessageTarget? target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var colon = text.IndexOf(':');
        if (colon <= 0 || colon == text.Length - 1) return false;
        var kind = text[..colon].Trim();
        var value = text[(colon + 1)..].Trim();
        if (value.Length == 0) return false;

        switch (kind)
        {
            case RoleTarget.Kind when !value.Contains(':'):
                target = new RoleTarget { Role = value };
                return true;
            case AgentTarget.Kind when !value.Contains(':'):
                target = new AgentTarget { AgentId = value };
                return true;
            case InboxTarget.Kind when !value.Contains(':'):
                target = new InboxTarget { InboxId = value };
                return true;
            case CapabilityTarget.Kind:
                var caps = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (caps.Length == 0) return false;
                target = new CapabilityTarget { Capabilities = caps };
                return true;
            default:
                return false;
        }
    }
}

/// <summary>A role's inbox. How the role's agents share it depends on the role's delivery mode.</summary>
public sealed record RoleTarget : MessageTarget
{
    /// <summary>Discriminator and text-form prefix: <c>role</c>.</summary>
    public const string Kind = "role";

    /// <summary>Role name, for example <c>developer</c> (see the role catalog).</summary>
    public required string Role { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Kind}:{Role}";
}

/// <summary>One specific agent. For replies and resumption; discouraged for normal work.</summary>
public sealed record AgentTarget : MessageTarget
{
    /// <summary>Discriminator and text-form prefix: <c>agent</c>.</summary>
    public const string Kind = "agent";

    /// <summary>The agent id (<c>agt_…</c>).</summary>
    public required string AgentId { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Kind}:{AgentId}";
}

/// <summary>
/// Any agent whose machine has every listed capability, without naming a role (capability-query
/// addressing). Delivered as compete: one matching agent gets it.
/// </summary>
public sealed record CapabilityTarget : MessageTarget
{
    /// <summary>Discriminator and text-form prefix: <c>capabilities</c>.</summary>
    public const string Kind = "capabilities";

    /// <summary>Required capabilities, for example <c>os:macos</c> (see <see cref="Machines.CapabilityNames"/>).</summary>
    public required IReadOnlyList<string> Capabilities { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Kind}:{string.Join(',', Capabilities)}";
}

/// <summary>A temporary session inbox, such as the one an orchestrator session opens to collect replies.</summary>
public sealed record InboxTarget : MessageTarget
{
    /// <summary>Discriminator and text-form prefix: <c>inbox</c>.</summary>
    public const string Kind = "inbox";

    /// <summary>The inbox id (<c>inb_…</c>).</summary>
    public required string InboxId { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{Kind}:{InboxId}";
}
