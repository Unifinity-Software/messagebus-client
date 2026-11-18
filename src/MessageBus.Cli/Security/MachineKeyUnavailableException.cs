namespace MessageBus.Cli.Security;

/// <summary>A temporary Keychain access failure; never an absent identity.</summary>
public sealed class MachineKeyUnavailableException() : CliException("The Mac's login Keychain is locked or interaction is not allowed.");

/// <summary>
/// The login Keychain is unlocked but its item trusts neither this mbus nor Apple's <c>security</c> tool (the original issue): waiting
/// doesn't help, a new key does.
/// </summary>
public sealed class MachineKeyRefusedException() : CliException("The login Keychain is unlocked but won't let mbus read this machine's key: run mbus login again.");
