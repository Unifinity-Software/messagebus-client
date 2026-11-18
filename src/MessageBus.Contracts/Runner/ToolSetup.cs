using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MessageBus.Contracts.Machines;

namespace MessageBus.Contracts.Runner;

/// <summary>The fixed Windows Git installer catalog. Task text never supplies executable commands.</summary>
public static partial class GitToolSetup
{
    /// <summary>The winget community package identifier.</summary>
    public const string PackageId = "Git.Git";
    /// <summary>The only permitted package source.</summary>
    public const string Source = "winget";
    /// <summary>True for a bounded numeric version that cannot introduce command arguments.</summary>
    public static bool ValidVersion(string? version) => version is { Length: > 0 and <= 32 } && VersionPattern().IsMatch(version);
    [GeneratedRegex(@"\A[0-9]+(?:\.[0-9]+){1,3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
    /// <summary>The exact owner-approved argument vector, without shell evaluation.</summary>
    public static string[] Arguments(string version) => ValidVersion(version)
        ? ["install", "--id", PackageId, "--exact", "--version", version, "--source", Source, "--scope", "user", "--silent", "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity"]
        : throw new ArgumentException("Give a numeric Git package version.", nameof(version));
    /// <summary>The exact install command shown to the owner.</summary>
    public static string Command(string version) => "winget " + string.Join(' ', Arguments(version));
    /// <summary>A rollback note. Uninstalling does not undo changes made by tasks after installation.</summary>
    public const string Rollback = "To remove Git, run winget uninstall --id Git.Git --exact.";
    /// <summary>Binds the package version and previous tool state to a single approval.</summary>
    public static string Digest(ToolSetupPlan plan) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        PackageId + "\n" + Source + "\n" + Command(plan.Version) + "\n" + (plan.BeforeVersion ?? ""))));
}

/// <summary>A locally resolved Git install plan; package, source and commands come from the fixed catalog.</summary>
public sealed record ToolSetupPlan
{
    /// <summary>The installer operating system. Omitted Windows plans keep their existing meaning.</summary>
    public MachineOs Os { get; init; } = MachineOs.Windows;
    /// <summary>For macOS, the exact stable Command Line Tools label offered by Apple's catalog.</summary>
    public string? PackageLabel { get; init; }
    /// <summary>The exact winget package version to install.</summary>
    public required string Version { get; init; }
    /// <summary>The version previously found, or null when Git was missing.</summary>
    public string? BeforeVersion { get; init; }
}

/// <summary>OS-bound, fixed installer catalogs. Neither a task nor a server response supplies executable code.</summary>
public static partial class ToolSetupCatalog
{
    /// <summary>The Apple receipt used to verify the installed toolchain.</summary>
    public const string ApplePackageId = "com.apple.pkg.CLTools_Executables";
    /// <summary>The sole permitted source for the macOS prototype.</summary>
    public const string AppleSource = "Apple Software Update";
    /// <summary>System toolchains cannot be rolled back automatically.</summary>
    public const string AppleRollback = "Command Line Tools is system software shared by other developer tools. There is no automatic rollback; use Apple's Command Line Tools uninstall instructions only after checking other tools no longer need it.";

    /// <summary>Checks the full OS-bound plan, including the canonical Apple label and previous Git state.</summary>
    public static bool Valid(ToolSetupPlan? plan) => plan is not null && GitToolSetup.ValidVersion(plan.Version)
        && (plan.BeforeVersion is null || plan.BeforeVersion.Length <= 80 && !plan.BeforeVersion.Any(char.IsControl))
        && plan.Os switch
        {
            MachineOs.Windows => plan.PackageLabel is null,
            MachineOs.MacOs => AppleVersion(plan.PackageLabel) == plan.Version,
            _ => false,
        };

    /// <summary>Only stable CLT labels containing bounded numeric versions are accepted.</summary>
    public static string? AppleVersion(string? label)
    {
        if (label is not { Length: > 0 and <= 120 }) return null;
        var match = AppleLabel().Match(label);
        var version = match.Success ? match.Groups["version"].Value : null;
        if (!GitToolSetup.ValidVersion(version) || !Version.TryParse(version, out _)) return null;
        if (match.Groups["title"].Success && !NumericVersionsEqual(match.Groups["title"].Value, version!)) return null;
        return version;
    }

    [GeneratedRegex(@"\ACommand Line Tools for Xcode(?: (?<title>[0-9]+(?:\.[0-9]+){1,3}))?-(?<version>[0-9]+(?:\.[0-9]+){1,3})\z", RegexOptions.CultureInvariant)]
    private static partial Regex AppleLabel();

    private static bool NumericVersionsEqual(string left, string right) => Version.TryParse(left, out var l) && Version.TryParse(right, out var r)
        && l.Major == r.Major && l.Minor == r.Minor && Math.Max(l.Build, 0) == Math.Max(r.Build, 0) && Math.Max(l.Revision, 0) == Math.Max(r.Revision, 0);

    /// <summary>The software the owner approves.</summary>
    public static string Name(ToolSetupPlan plan) => Checked(plan).Os == MachineOs.MacOs ? "Apple Command Line Tools (includes Git)" : "Git";
    /// <summary>The fixed package or receipt identifier.</summary>
    public static string PackageId(ToolSetupPlan plan) => Checked(plan).Os == MachineOs.MacOs ? ApplePackageId : GitToolSetup.PackageId;
    /// <summary>The fixed source for this operating system.</summary>
    public static string Source(ToolSetupPlan plan) => Checked(plan).Os == MachineOs.MacOs ? AppleSource : GitToolSetup.Source;
    /// <summary>The rollback note shown before approval.</summary>
    public static string Rollback(ToolSetupPlan plan) => Checked(plan).Os == MachineOs.MacOs ? AppleRollback : GitToolSetup.Rollback;

    /// <summary>The precise command shown in approval and executed by the local platform.</summary>
    public static string Command(ToolSetupPlan plan) => Checked(plan).Os == MachineOs.Windows ? GitToolSetup.Command(plan.Version)
        : "/usr/sbin/softwareupdate --install '" + plan.PackageLabel + "' --verbose && /usr/bin/xcode-select --switch /Library/Developer/CommandLineTools";

    /// <summary>A fixed AppleScript authorization helper; no credentials or unvalidated text are interpolated.</summary>
    public static string AppleAuthorizationScript(ToolSetupPlan plan)
    {
        if (Checked(plan).Os != MachineOs.MacOs) throw new ArgumentException("This is not an Apple installation plan.", nameof(plan));
        // A valid label contains only the literal product name, spaces, digits, dots and a hyphen.
        return "do shell script \"" + Command(plan) + "\" with administrator privileges";
    }

    /// <summary>Preserves Windows claim digests; Apple claims additionally bind OS, receipt and exact catalog label.</summary>
    public static string Digest(ToolSetupPlan plan) => Checked(plan).Os == MachineOs.Windows ? GitToolSetup.Digest(plan)
        : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("macos\n" + PackageId(plan) + "\n" + Source(plan) + "\n" + Command(plan) + "\n" + (plan.BeforeVersion ?? ""))));

    private static ToolSetupPlan Checked(ToolSetupPlan plan) => Valid(plan) ? plan : throw new ArgumentException("The OS, version and package label must match an allowed installer plan.", nameof(plan));
}

/// <summary>The runner's setup state for the dashboard and terminal.</summary>
public sealed record ToolSetupReport
{
    /// <summary>False when local recovery is required before another installation can be requested.</summary>
    public bool CanStart { get; set; } = true;
    /// <summary>disabled, available, planning, awaiting-approval, installing, succeeded, failed, declined, or installed (Git already works; nothing to set up).</summary>
    public required string State { get; init; }
    /// <summary>A short explanation or the next step for the owner.</summary>
    public string? Detail { get; init; }
    /// <summary>The exact plan, once resolved.</summary>
    public ToolSetupPlan? Plan { get; init; }
    /// <summary>The owner's dashboard approval, when requested.</summary>
    public string? ApprovalId { get; init; }
    /// <summary>The Git version before installation.</summary>
    public string? BeforeVersion { get; init; }
    /// <summary>The Git version verified after installation.</summary>
    public string? AfterVersion { get; init; }
    /// <summary>The installer process exit code, when it ran.</summary>
    public int? ExitCode { get; init; }
}

/// <summary>Create, poll or claim approval for the same immutable plan.</summary>
public sealed record ToolSetupApprovalRequest
{
    /// <summary>The locally resolved plan.</summary>
    public required ToolSetupPlan Plan { get; init; }
    /// <summary>Null to request approval; otherwise the existing approval to poll or claim.</summary>
    public string? ApprovalId { get; init; }
}

/// <summary>An owner's approval state. An approved poll still requires a successful one-use execution claim.</summary>
public sealed record ToolSetupApprovalState
{
    /// <summary>The owner-only dashboard approval.</summary>
    public required string ApprovalId { get; init; }
    /// <summary>pending, approved or declined.</summary>
    public required string State { get; init; }
    /// <summary>The deadline for claiming the approved plan, even if the owner has already approved it.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
    /// <summary>Why the plan is waiting or was declined.</summary>
    public string? Detail { get; init; }
}

/// <summary>The runner's final report for an execution it previously claimed.</summary>
public sealed record ToolSetupResultRequest
{
    /// <summary>The original immutable plan.</summary>
    public required ToolSetupPlan Plan { get; init; }
    /// <summary>The claimed approval.</summary>
    public required string ApprovalId { get; init; }
    /// <summary>The result and verified before/after versions.</summary>
    public required ToolSetupReport Report { get; init; }
}
