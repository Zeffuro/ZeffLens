using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ZeffLens.Diagnostics;

internal static class WindowsProcessMemory
{
    public static (long Commit, long WorkingSet, long? PrivateWorkingSet) Read(Process process)
    {
        if (OperatingSystem.IsWindows() && GetProcessMemoryInfo(process.SafeHandle, out var counters, (uint)Marshal.SizeOf<Counters>()))
            return ((long)counters.PrivateUsage, (long)counters.WorkingSetSize,
                counters.PrivateWorkingSetSize == 0 ? null : (long)counters.PrivateWorkingSetSize);

        process.Refresh();
        return (process.PrivateMemorySize64, process.WorkingSet64, null);
    }

    [DllImport("psapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(SafeHandle process, out Counters counters, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct Counters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }
}
