using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CentricDeviceMonitor.Services;

public sealed class SystemPowerService
{
    public void ShutdownNow(bool forceCloseApplications) =>
        InitiatePowerAction(restart: false, forceCloseApplications, timeoutSeconds: 0);

    public void RestartNow(bool forceCloseApplications) =>
        InitiatePowerAction(restart: true, forceCloseApplications, timeoutSeconds: 0);

    public void ScheduleShutdown(int delayMinutes, bool forceCloseApplications) =>
        InitiatePowerAction(restart: false, forceCloseApplications, checked((uint)Math.Clamp(delayMinutes, 0, 120) * 60));

    public void ScheduleRestart(int delayMinutes, bool forceCloseApplications) =>
        InitiatePowerAction(restart: true, forceCloseApplications, checked((uint)Math.Clamp(delayMinutes, 0, 120) * 60));

    public void CancelScheduledPowerAction()
    {
        EnableShutdownPrivilege();
        if (!AbortSystemShutdownW(null))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, "Windows could not cancel the pending shutdown/restart request.");
        }
    }

    private static void InitiatePowerAction(bool restart, bool forceCloseApplications, uint timeoutSeconds)
    {
        EnableShutdownPrivilege();

        const uint ShutdownReasonMajorApplication = 0x00040000;
        const uint ShutdownReasonFlagPlanned = 0x80000000;
        uint reason = ShutdownReasonMajorApplication | ShutdownReasonFlagPlanned;
        string actionName = restart ? "restart" : "shutdown";
        string message = $"Windows Utility scheduled this Windows {actionName}.";

        if (!InitiateSystemShutdownExW(
                null,
                message,
                timeoutSeconds,
                forceCloseApplications,
                restart,
                reason))
        {
            int error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"Windows could not schedule the {actionName} request.");
        }
    }

    private static void EnableShutdownPrivilege()
    {
        const uint TokenAdjustPrivileges = 0x0020;
        const uint TokenQuery = 0x0008;
        const uint SePrivilegeEnabled = 0x00000002;
        const int ErrorNotAllAssigned = 1300;

        if (!OpenProcessToken(
                System.Diagnostics.Process.GetCurrentProcess().Handle,
                TokenAdjustPrivileges | TokenQuery,
                out IntPtr tokenHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to open the process token for shutdown privileges.");
        }

        try
        {
            if (!LookupPrivilegeValueW(null, "SeShutdownPrivilege", out Luid luid))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to resolve the Windows shutdown privilege.");
            }

            TokenPrivileges privileges = new()
            {
                PrivilegeCount = 1,
                Luid = luid,
                Attributes = SePrivilegeEnabled
            };

            if (!AdjustTokenPrivileges(tokenHandle, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to enable the Windows shutdown privilege.");
            }

            int lastError = Marshal.GetLastWin32Error();
            if (lastError == ErrorNotAllAssigned)
            {
                throw new Win32Exception(lastError, "The current account does not have the Windows shutdown privilege.");
            }
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public Luid Luid;
        public uint Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitiateSystemShutdownExW(
        string? lpMachineName,
        string? lpMessage,
        uint dwTimeout,
        [MarshalAs(UnmanagedType.Bool)] bool bForceAppsClosed,
        [MarshalAs(UnmanagedType.Bool)] bool bRebootAfterShutdown,
        uint dwReason);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AbortSystemShutdownW(string? lpMachineName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out Luid lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr tokenHandle,
        [MarshalAs(UnmanagedType.Bool)] bool disableAllPrivileges,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
