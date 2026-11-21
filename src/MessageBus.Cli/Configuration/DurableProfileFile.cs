using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MessageBus.Cli.Configuration;

// Same-directory atomic publication plus metadata durability. Failure is always reported to the caller.
internal static class DurableProfileFile
{
    // Only newly created directories receive a new ACL. Existing paths are never repaired implicitly.
    internal static void CreatePrivateDirectory(string directory)
    {
        RejectDirectoryAliases(directory);
        if (Directory.Exists(directory)) return;
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User ?? throw new IOException("Cannot identify the profile owner.");
            var security = new DirectorySecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).Create(security);
        }
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ValidatePrivateDirectory(directory);
    }

    internal static void RejectDirectoryAliases(string directory)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
        {
            // File.Exists/Directory.Exists would hide dangling links and access failures.
            try
            {
                if ((File.GetAttributes(current.FullName) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Profile directory aliases and reparse points are not supported.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static void ValidatePrivateDirectory(string directory)
    {
        RejectDirectoryAliases(directory);
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsIdentity.GetCurrent().User ?? throw new IOException("Cannot identify the profile owner.");
            var security = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
            if (!owner.Equals(security.GetOwner(typeof(SecurityIdentifier))))
                throw new IOException("The profile directory must belong to the current user.");
            if (new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl is null)
                throw new IOException("The existing profile directory has unrestricted access; permissions were left unchanged.");
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            if (!security.AreAccessRulesCanonical)
                throw new IOException("The profile directory has an unsupported access policy.");
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow && rule.FileSystemRights != 0
                    && !rule.IdentityReference.Equals(owner) && !rule.IdentityReference.Equals(system)
                    && !rule.IdentityReference.Equals(administrators))
                    throw new IOException("The existing profile directory is not private; its permissions were left unchanged.");
            }
        }
        else
        {
            var mode = File.GetUnixFileMode(directory);
            const UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((mode & others) != 0 || UnixOwner(directory) != GetEffectiveUserId())
                throw new IOException("The existing profile directory must be private and owned by the current user; permissions were left unchanged.");
            if (OperatingSystem.IsMacOS()) ValidateDarwinAcl(directory);
        }
    }

    private static void ValidateDarwinAcl(string directory)
    {
        // Darwin ACL grants are independent of the POSIX mode bits. Read the extended-security
        // attribute directly so absence is explicit, rather than interpreting acl_get_file's ENOENT.
        // Layout: attrreference -> kauth_filesec header (44 bytes), then 24-byte kauth_ace entries.
        const int capacity = 8192;
        var buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            var attributes = new DarwinAttributes { BitmapCount = 5, Common = 0x00400000 };
            if (GetAttributes(directory, ref attributes, buffer, capacity, 1 | 4) != 0)
                throw new IOException("Cannot validate profile ACL.", new Win32Exception(Marshal.GetLastPInvokeError()));
            var returned = Marshal.ReadInt32(buffer);
            if (returned < 12 || returned > capacity) throw new IOException("Incomplete profile ACL metadata.");
            var offset = (long)Marshal.ReadInt32(buffer, 4) + 4;
            var length = Marshal.ReadInt32(buffer, 8);
            if (length == 0) return;
            if (offset < 12 || length < 44 || offset + length > returned)
                throw new IOException("Invalid profile ACL metadata.");
            var security = IntPtr.Add(buffer, (int)offset);
            if (Marshal.ReadInt32(security) != 0x012cc16d) throw new IOException("Unknown profile ACL format.");
            var count = Marshal.ReadInt32(security, 36);
            if (count == -1) return; // KAUTH_FILESEC_NOACL
            if (count < 0 || count > 128 || 44 + count * 24 != length)
                throw new IOException("Invalid profile ACL entries.");
            byte[]? owner = null;
            for (var i = 0; i < count; i++)
            {
                var entry = IntPtr.Add(security, 44 + i * 24);
                var kind = Marshal.ReadInt32(entry, 16) & 0xf;
                if (kind == 2) continue; // A deny entry cannot grant access.
                if (kind != 1) throw new IOException("Unsupported profile ACL entry.");
                if (Marshal.ReadInt32(entry, 20) == 0) continue;
                if (owner is null)
                {
                    owner = new byte[16];
                    if (UserIdToUuid(GetEffectiveUserId(), owner) != 0)
                        throw new IOException("Cannot validate the profile ACL owner.");
                }
                var principal = new byte[16];
                Marshal.Copy(entry, principal, 0, principal.Length);
                if (!principal.AsSpan().SequenceEqual(owner))
                    throw new IOException("The existing profile ACL grants access to another principal; permissions were left unchanged.");
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static uint UnixOwner(string directory)
    {
        var status = Marshal.AllocHGlobal(512);
        try
        {
            // Linux statx has an architecture-independent layout: stx_uid is at byte 20.
            if (OperatingSystem.IsLinux())
            {
                if (Statx(-100, directory, 0x100, 0x8, status) != 0 || (Marshal.ReadInt32(status) & 0x8) == 0)
                    throw new IOException("Cannot validate profile ownership.", new Win32Exception(Marshal.GetLastPInvokeError()));
                return unchecked((uint)Marshal.ReadInt32(status, 20));
            }
            if (OperatingSystem.IsMacOS())
            {
                // getattrlist returns a 32-bit buffer length followed by the requested uid_t.
                // This avoids the architecture-dependent stat/lstat ABI on Darwin.
                var attributes = new DarwinAttributes { BitmapCount = 5, Common = 0x8000 };
                if (GetAttributes(directory, ref attributes, status, 512, 1) != 0 || Marshal.ReadInt32(status) != 8)
                    throw new IOException("Cannot validate profile ownership.", new Win32Exception(Marshal.GetLastPInvokeError()));
                return unchecked((uint)Marshal.ReadInt32(status, 4));
            }
            throw new IOException("Profile ownership validation is unsupported on this platform.");
        }
        finally { Marshal.FreeHGlobal(status); }
    }

    internal static void Replace(string temporary, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            // MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH; both names are on the same volume.
            if (!MoveFileEx(temporary, destination, 0x1 | 0x8))
                throw new IOException("Cannot durably publish profile state.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        else
        {
            File.Move(temporary, destination, overwrite: true);
            FlushDirectory(Path.GetDirectoryName(destination)!);
        }
    }

    internal static void FlushDirectory(string directory)
    {
        // Windows replacements above use WRITE_THROUGH. A lost deletion can only retain a restrictive marker/pin.
        if (OperatingSystem.IsWindows()) return;
        var descriptor = Open(directory, 0);
        if (descriptor < 0) throw new IOException("Cannot open profile metadata for durable flush.", new Win32Exception(Marshal.GetLastPInvokeError()));
        try
        {
            if (Fsync(descriptor) != 0)
                throw new IOException("Cannot flush profile metadata.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        finally { Close(descriptor); }
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existing, string destination, uint flags);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, IntPtr status);
    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinAttributes
    {
        internal ushort BitmapCount;
        internal ushort Reserved;
        internal uint Common, Volume, Directory, File, Fork;
    }
    [DllImport("libc", EntryPoint = "getattrlist", SetLastError = true)]
    private static extern int GetAttributes([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ref DarwinAttributes attributes, IntPtr buffer, nuint size, uint options);
    [DllImport("libc", EntryPoint = "mbr_uid_to_uuid")]
    private static extern int UserIdToUuid(uint user, [Out] byte[] uuid);
}
