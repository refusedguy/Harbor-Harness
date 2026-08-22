using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Harbor.Tools.Mcp;

/// <summary>
///     Cross-platform helpers for killing a whole subprocess tree.
///     <para>
///         Windows: a Job Object with KILL_ON_JOB_CLOSE — closing the handle tears down the tree.
///         Unix: the child is promoted to its own process-group leader, then we signal the whole
///         group with <c>kill(-pid)</c>.
///     </para>
/// </summary>
internal static class ProcessTree
{
    private const int SigKill = 9;

    public static void KillTree(Process process, SafeJobHandle? job)
    {
        if (process.HasExited) return;

        if (job is not null)
        {
            // KILL_ON_JOB_CLOSE guarantees the tree dies when the handle is released.
            job.Dispose();
            return;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try { process.Kill(true); } catch { /* already gone or access denied */ }
            return;
        }

        // Unix: signal the entire process group.
        try
        {
            if (kill(-process.Id, SigKill) != 0)
                process.Kill(true);
        }
        catch
        {
            try { process.Kill(true); } catch { /* ignore */ }
        }
    }

    public static void PromoteToGroupLeader(Process process)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            _ = setpgid(process.Id, process.Id);
    }

    public static SafeJobHandle? KillOnCloseJob(Process process)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return null;

        var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) return null;

        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        if (!SetInformationJobObject(job, JobObjectInfoClass.ExtendedLimitInformation,
                ref info, Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            job.Dispose();
            return null;
        }

        if (!AssignProcessToJobObject(job, process.Handle))
        {
            job.Dispose();
            return null;
        }

        return job;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeJobHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        SafeJobHandle hJob, JobObjectInfoClass infoClass,
        ref JobObjectExtendedLimitInformation lpJobObjectInfo, int cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);

    [DllImport("libc", SetLastError = true)]
    private static extern int setpgid(int pid, int pgid);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    private enum JobObjectInfoClass
    {
        ExtendedLimitInformation = 9
    }

    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
}

/// <summary>
///     Safe handle wrapping a Win32 Job Object. Releasing it (with KILL_ON_JOB_CLOSE set)
///     terminates every process assigned to the job.
/// </summary>
internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SafeJobHandle() : base(true) { }

    protected override bool ReleaseHandle() => ProcessTree.CloseHandle(handle);
}
