using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NumaScheduler.Platform;

// ── cpu_set_t (128 bytes = 1024 CPU bits) ────────────────────────────────────

/// <summary>
/// Mirrors Linux <c>cpu_set_t</c> from <c>&lt;sched.h&gt;</c>:
/// 16 × <see cref="ulong"/> = 128 bytes, covering up to 1024 logical CPUs.
/// Passed by reference to <c>sched_setaffinity</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct LinuxCpuSet
{
    // 16 explicit fields — avoids requiring AllowUnsafeBlocks while still
    // producing the correct ABI layout for the kernel call.
    private ulong _b0,  _b1,  _b2,  _b3,  _b4,  _b5,  _b6,  _b7,
                  _b8,  _b9,  _b10, _b11, _b12, _b13, _b14, _b15;

    internal void SetCpu(int cpu)
    {
        if ((uint)cpu >= 1024u) return;
        ulong mask = 1UL << (cpu & 63);
        switch (cpu >> 6)
        {
            case  0: _b0  |= mask; break; case  1: _b1  |= mask; break;
            case  2: _b2  |= mask; break; case  3: _b3  |= mask; break;
            case  4: _b4  |= mask; break; case  5: _b5  |= mask; break;
            case  6: _b6  |= mask; break; case  7: _b7  |= mask; break;
            case  8: _b8  |= mask; break; case  9: _b9  |= mask; break;
            case 10: _b10 |= mask; break; case 11: _b11 |= mask; break;
            case 12: _b12 |= mask; break; case 13: _b13 |= mask; break;
            case 14: _b14 |= mask; break; case 15: _b15 |= mask; break;
        }
    }

    internal readonly bool IsEmpty =>
        (_b0 | _b1 | _b2 | _b3 | _b4 | _b5 | _b6 | _b7 |
         _b8 | _b9 | _b10 | _b11 | _b12 | _b13 | _b14 | _b15) == 0;
}

// ── Linux P/Invoke ────────────────────────────────────────────────────────────

[SupportedOSPlatform("linux")]
internal static class LinuxNative
{
    private const string Libc = "libc";

    /// <summary>Returns the number of the CPU currently executing the calling thread.</summary>
    [DllImport(Libc, EntryPoint = "sched_getcpu", SetLastError = false)]
    internal static extern int SchedGetCpu();

    /// <summary>
    /// Sets the CPU affinity mask of thread <paramref name="pid"/> (0 = calling thread).
    /// <paramref name="cpuSetSize"/> must equal <c>sizeof(cpu_set_t)</c> = 128.
    /// </summary>
    [DllImport(Libc, EntryPoint = "sched_setaffinity", SetLastError = true)]
    internal static extern int SchedSetAffinity(int pid, nuint cpuSetSize, ref LinuxCpuSet mask);
}

// ── Affinity descriptor ───────────────────────────────────────────────────────

[SupportedOSPlatform("linux")]
internal sealed class LinuxAffinityInfo : NumaAffinityInfo
{
    internal LinuxCpuSet CpuSet { get; }

    internal LinuxAffinityInfo(int nodeId, int processorCount, LinuxCpuSet cpuSet)
        : base(nodeId, processorCount)
    {
        CpuSet = cpuSet;
    }
}

// ── Platform implementation ───────────────────────────────────────────────────

[SupportedOSPlatform("linux")]
internal sealed class LinuxNumaPlatform : INumaPlatform
{
    private const string SysNodeRoot = "/sys/devices/system/node";

    // Populated by DiscoverNodes(); maps logical CPU number → OS NUMA node ID.
    private int[] _cpuToNodeId = [];

    // ── INumaPlatform ─────────────────────────────────────────────────────────

    public IReadOnlyList<NumaAffinityInfo> DiscoverNodes()
    {
        if (!Directory.Exists(SysNodeRoot))
            return SingleNodeFallback();

        // Collect (nodeId, cpuList) pairs from sysfs, sorted by nodeId.
        var nodeInfos = new List<(int nodeId, List<int> cpus)>();

        foreach (var dir in new DirectoryInfo(SysNodeRoot)
                     .GetDirectories("node*")
                     .OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            if (!int.TryParse(dir.Name.Substring(4), out int nodeId)) continue;
            string cpuListFile = Path.Combine(dir.FullName, "cpulist");
            if (!File.Exists(cpuListFile)) continue;

            var cpus = ParseCpuList(File.ReadAllText(cpuListFile).Trim()).ToList();
            if (cpus.Count == 0) continue;

            nodeInfos.Add((nodeId, cpus));
        }

        if (nodeInfos.Count == 0)
            return SingleNodeFallback();

        // Build CPU→nodeId lookup array.
        int maxCpu = nodeInfos.SelectMany(n => n.cpus).Max();
        _cpuToNodeId = new int[maxCpu + 1];

        var result = new List<NumaAffinityInfo>(nodeInfos.Count);

        foreach (var (nodeId, cpus) in nodeInfos)
        {
            var cpuSet = new LinuxCpuSet();
            foreach (int cpu in cpus)
            {
                cpuSet.SetCpu(cpu);
                if (cpu < _cpuToNodeId.Length)
                    _cpuToNodeId[cpu] = nodeId;
            }
            result.Add(new LinuxAffinityInfo(nodeId, cpus.Count, cpuSet));
        }

        return result;
    }

    public void PinCurrentThreadToNode(NumaAffinityInfo info)
    {
        if (info is not LinuxAffinityInfo li) return;
        var set = li.CpuSet;
        if (set.IsEmpty) return;
        // pid=0 → calling thread; cpuSetSize=128 (standard cpu_set_t)
        LinuxNative.SchedSetAffinity(0, 128, ref set);
        // Failure is non-fatal (EPERM in restricted containers, etc.)
    }

    public int GetCurrentNodeId()
    {
        int cpu = LinuxNative.SchedGetCpu();
        if (cpu < 0 || cpu >= _cpuToNodeId.Length) return -1;
        return _cpuToNodeId[cpu];
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private IReadOnlyList<NumaAffinityInfo> SingleNodeFallback()
    {
        int n = Environment.ProcessorCount;
        _cpuToNodeId = new int[n]; // all map to node 0
        var set = new LinuxCpuSet();
        for (int i = 0; i < n; i++) set.SetCpu(i);
        return [new LinuxAffinityInfo(0, n, set)];
    }

    /// <summary>
    /// Parses Linux CPU list format: comma-separated integers and ranges,
    /// e.g. <c>"0-3,8,12-15"</c>.
    /// </summary>
    private static IEnumerable<int> ParseCpuList(string cpuList)
    {
        foreach (var part in cpuList.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int dash = part.IndexOf('-');
            if (dash < 0)
            {
                if (int.TryParse(part, out int cpu)) yield return cpu;
            }
            else
            {
                if (int.TryParse(part.Substring(0, dash),    out int start) &&
                    int.TryParse(part.Substring(dash + 1),   out int end))
                    for (int c = start; c <= end; c++) yield return c;
            }
        }
    }
}
