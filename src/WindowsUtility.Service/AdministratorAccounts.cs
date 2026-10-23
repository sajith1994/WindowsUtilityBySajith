using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WindowsUtility.ServiceHost;

/// <summary>
/// Whether whoever sent a request is an administrator account, without an administrator prompt: the request file's
/// owner (set by Windows) is looked up in the local Administrators group, including through domain groups.
/// </summary>
internal static class AdministratorAccounts
{
    private const int IncludeIndirect = 1; // LG_INCLUDE_INDIRECT
    private const int MaxPreferredLength = -1;

    public static bool SentByAdministrator(string requestPath)
    {
        try
        {
            if (new FileInfo(requestPath).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner)
            {
                return false;
            }

            // A file made by an elevated program is owned by the Administrators group itself.
            if (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
            {
                return true;
            }

            return IsMember(owner.Translate(typeof(NTAccount)).Value);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or IdentityNotMappedException or SystemException)
        {
            return false;
        }
    }

    /// <summary>True when the account ("PC\maria", "OFFICE\maria") is in the local Administrators group.</summary>
    internal static bool IsMember(string account)
    {
        string administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Translate(typeof(NTAccount)).Value;
        string groupName = administrators[(administrators.IndexOf('\\') + 1)..];
        int status = NetUserGetLocalGroups(null, account, 0, IncludeIndirect, out IntPtr buffer, MaxPreferredLength, out int read, out _);
        try
        {
            if (status != 0)
            {
                return false;
            }

            // An array of LOCALGROUP_USERS_INFO_0, each a single pointer to the group's name.
            for (int index = 0; index < read; index++)
            {
                string? name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, index * IntPtr.Size));
                if (string.Equals(name, groupName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero)
            {
                NetApiBufferFree(buffer);
            }
        }
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetLocalGroups(string? serverName, string userName, int level, int flags, out IntPtr buffer,
        int preferredMaximumLength, out int entriesRead, out int totalEntries);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}
