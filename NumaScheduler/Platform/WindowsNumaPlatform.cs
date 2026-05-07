using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NumaScheduler.Platform;

// ── Affinity descriptor ───────────────────────────────────────────────────────

[SupportedOSPlatform("windows")]
internal sealed class WindowsAffinityInfo : NumaAffinityInfo
{
    internal NativeMethods.GROUP_AFFINITY GroupAffinity { get; }

    internal WindowsAffinityInfo(int nodeId, NativeMethods.GROUP_AFFINITY affinity)
        : base(nodeId, PopCount((ulong)affinity.Mask))
    {
        GroupAffinity = affinity;
    }

    internal override string GetDescription() =>
        $" | Group {GroupAffinity.Group} | Mask 0x{(ulong)GroupAffinity.Mask:X16}";

    private static int PopCount(ulong v)
    {
        int c = 0;
        while (v != 0) { c += (int)(v & 1); v >>= 1; }
        return c;
    }
}

// ── Platform implementation ───────────────────────────────────────────────────

[SupportedOSPlatform("windows")]
internal sealed class WindowsNumaPlatform : INumaPlatform
{
    public IReadOnlyList<NumaAffinityInfo> DiscoverNodes()
    {
        if (!NativeMethods.GetNumaHighestNodeNumber(out uint highest))
            throw new InvalidOperationException(
                $"GetNumaHighestNodeNumber failed (Win32 error {Marshal.GetLastWin32Error()}).");

        var nodes = new List<NumaAffinityInfo>((int)highest + 1);

        for (ushort i = 0; i <= (ushort)highest; i++)
        {
            if (!NativeMethods.GetNumaNodeProcessorMaskEx(i, out var affinity)) continue;
            if (affinity.Mask == default) continue;
            nodes.Add(new WindowsAffinityInfo(i, affinity));
        }

        if (nodes.Count == 0)
            throw new InvalidOperationException("No active NUMA nodes with processors were found.");

        return nodes;
    }

    public void PinCurrentThreadToNode(NumaAffinityInfo info)
    {
        if (info is not WindowsAffinityInfo wi) return;
        var aff    = wi.GroupAffinity;
        var handle = NativeMethods.GetCurrentThread();
        NativeMethods.SetThreadGroupAffinity(handle, ref aff, out _);
    }

    public int GetCurrentNodeId()
    {
        NativeMethods.GetCurrentProcessorNumberEx(out var proc);
        return NativeMethods.GetNumaProcessorNodeEx(ref proc, out ushort nodeId) ? nodeId : -1;
    }
}
