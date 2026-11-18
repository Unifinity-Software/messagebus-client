using System.Security.Cryptography;
using System.Text;
using MessageBus.Contracts.Machines;
using MessageBus.Contracts.Roles;
using MessageBus.Contracts.Runner;

namespace MessageBus.Cli.Runner;

/// <summary>
/// The machine owner's limits on whose work runs here: the accepts-work-from rule and the daily spend
/// ceiling for other people's work. A refused delivery returns to the queue for another machine.
/// </summary>
/// <param name="policy">The owner's settings.</param>
public sealed class WorkAcceptance(RunnerPolicy policy)
{
    private readonly object _gate = new();
    private DateOnly _day;
    private decimal _spentForOthers;
    private RunnerPolicy _policy = policy;

    /// <summary>The settings in force; replaced when the service sends new ones (the original issue). Today's spend so far is kept.</summary>
    public RunnerPolicy Policy
    {
        get => Volatile.Read(ref _policy);
        set => Volatile.Write(ref _policy, value);
    }

    /// <summary>Why <paramref name="delivery"/> can't run here, or null when it can.</summary>
    public (DeclineReason Reason, string Detail)? Check(TaskDelivery delivery, DateTimeOffset now)
    {
        var policy = Policy;
        var requester = Requester(delivery);
        var own = policy.OwnerSubjectId is not null && requester == policy.OwnerSubjectId;
        var accepted = policy.AcceptsWorkFrom.Mode switch
        {
            AcceptsWorkFromMode.Workspace => true,
            AcceptsWorkFromMode.OwnProjects => own,
            AcceptsWorkFromMode.OwnProjectsAndGroups => own || (requester is not null && policy.GroupMemberSubjectIds.Contains(requester)),
            _ => false,
        };
        if (!accepted)
        {
            return (DeclineReason.NotAccepted, $"This machine only runs work for {Describe(policy.AcceptsWorkFrom.Mode)}.");
        }
        if (!own && policy.DailySpendCeilingUsd is { } ceiling && SpentForOthers(now) >= ceiling)
        {
            return (DeclineReason.SpendCeiling, $"Today's ${ceiling} for other people's work is used up.");
        }
        return null;
    }

    /// <summary>Counts a finished run's estimated cost toward today's total, when it was someone else's work.</summary>
    public void Record(TaskDelivery delivery, decimal? costUsd, DateTimeOffset now)
    {
        var policy = Policy;
        if (costUsd is not { } cost || (policy.OwnerSubjectId is not null && Requester(delivery) == policy.OwnerSubjectId))
        {
            return;
        }
        lock (_gate)
        {
            Roll(now);
            _spentForOthers += cost;
        }
    }

    private decimal SpentForOthers(DateTimeOffset now)
    {
        lock (_gate)
        {
            Roll(now);
            return _spentForOthers;
        }
    }

    private void Roll(DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (today != _day)
        {
            _day = today;
            _spentForOthers = 0;
        }
    }

    /// <summary>The person the work is for: whose session a message to a session inbox wakes (the original issue), else the project's starter, else the sender's owner.</summary>
    private static string? Requester(TaskDelivery delivery) => delivery.InboxOwner ?? delivery.ProjectOwner ?? delivery.Envelope.Sender.SubjectId;

    /// <summary>Whose work a mode runs, as a sentence ends ("This machine only runs work for …").</summary>
    internal static string Describe(AcceptsWorkFromMode mode) => mode switch
    {
        AcceptsWorkFromMode.OwnProjects => "its owner's projects",
        AcceptsWorkFromMode.OwnProjectsAndGroups => "its owner's projects and the chosen groups",
        _ => "people in the workspace",
    };
}

/// <summary>Checks a role's instructions before a run uses them.</summary>
public interface IInstructionVerifier
{
    /// <summary>Null when <paramref name="instructions"/> may be used, else why not.</summary>
    string? Verify(InstructionVersion instructions);
}

/// <summary>
/// Checks that the instructions are the content their hash names. Checking the signature over that
/// hash needs the signer's device public key, which arrives with end-to-end encryption (the original issue):
/// <see cref="SignatureVerifier"/> is where it plugs in.
/// </summary>
/// <param name="signatureVerifier">Verifies <see cref="InstructionVersion.Signature"/>; null until the original issue provides one.</param>
public sealed class InstructionVerifier(Func<InstructionVersion, bool>? signatureVerifier = null) : IInstructionVerifier
{
    /// <summary>The signature check, when available.</summary>
    public Func<InstructionVersion, bool>? SignatureVerifier { get; } = signatureVerifier;

    /// <inheritdoc />
    public string? Verify(InstructionVersion instructions)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(instructions.Content)));
        if (!string.Equals(hash, instructions.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            return $"The {instructions.Role} instructions (version {instructions.Version}) don't match their hash.";
        }
        if (SignatureVerifier is { } verify && !verify(instructions))
        {
            return $"The {instructions.Role} instructions (version {instructions.Version}) aren't signed by a key this machine trusts.";
        }
        return null;
    }
}
