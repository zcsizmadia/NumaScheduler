using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NumaScheduler;

/// <summary>
/// Windows kernel32 API surface needed for NUMA topology discovery and thread affinity.
/// All members are internal — callers should use the higher-level abstractions instead.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    // ── Structures ────────────────────────────────────────────────────────────

    /// <summary>
    /// Describes the processor-group affinity of a NUMA node or a thread.
    /// Maps to the Windows GROUP_AFFINITY structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GROUP_AFFINITY
    {
        /// <summary>Bitmask of logical processors within <see cref="Group"/>.</summary>
        public nuint   Mask;
        /// <summary>Processor group number.</summary>
        public ushort  Group;
        public ushort  Reserved0;
        public ushort  Reserved1;
        public ushort  Reserved2;
    }

    /// <summary>
    /// Identifies a specific logical processor by group and number.
    /// Maps to the Windows PROCESSOR_NUMBER structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESSOR_NUMBER
    {
        /// <summary>Processor group number.</summary>
        public ushort Group;
        /// <summary>Processor number within the group.</summary>
        public byte   Number;
        public byte   Reserved;
    }

    // ── Imports ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the index of the highest-numbered NUMA node on the system.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNumaHighestNodeNumber(out uint HighestNodeNumber);

    /// <summary>
    /// Returns the processor-group affinity mask for a given NUMA node.
    /// Supports systems with more than 64 logical processors (processor groups).
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNumaNodeProcessorMaskEx(
        ushort           Node,
        out GROUP_AFFINITY ProcessorMask);

    /// <summary>
    /// Sets the processor-group affinity of a thread.
    /// Returns the previous affinity in <paramref name="PreviousGroupAffinity"/>.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetThreadGroupAffinity(
        IntPtr           hThread,
        ref GROUP_AFFINITY GroupAffinity,
        out GROUP_AFFINITY PreviousGroupAffinity);

    /// <summary>Returns a pseudo-handle for the current thread (no CloseHandle needed).</summary>
    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetCurrentThread();

    /// <summary>
    /// Returns the group-relative number of the logical processor currently
    /// executing the calling thread.
    /// </summary>
    [DllImport("kernel32.dll")]
    internal static extern void GetCurrentProcessorNumberEx(out PROCESSOR_NUMBER ProcNumber);

    /// <summary>
    /// Returns the NUMA node number for a given logical processor.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetNumaProcessorNodeEx(
        ref PROCESSOR_NUMBER Processor,
        out ushort           NodeNumber);
}
