namespace NumaScheduler.Platform;

// ── Affinity descriptor ───────────────────────────────────────────────────────

/// <summary>
/// Affinity descriptor for platforms that do not expose a NUMA pinning API
/// (macOS, FreeBSD, etc.).  Thread pinning is a no-op on these systems.
/// </summary>
internal sealed class FallbackAffinityInfo : NumaAffinityInfo
{
    internal FallbackAffinityInfo(int nodeId, int processorCount)
        : base(nodeId, processorCount) { }
}

// ── Platform implementation ───────────────────────────────────────────────────

/// <summary>
/// NUMA platform stub for macOS and any OS without dedicated NUMA APIs.
/// Reports a single virtual NUMA node containing all logical processors.
/// Thread pinning and node detection are no-ops.
/// </summary>
internal sealed class FallbackNumaPlatform : INumaPlatform
{
    public IReadOnlyList<NumaAffinityInfo> DiscoverNodes() =>
        [new FallbackAffinityInfo(0, Environment.ProcessorCount)];

    public void PinCurrentThreadToNode(NumaAffinityInfo info)
    {
        // No user-space thread-pinning API available on this platform.
    }

    public int GetCurrentNodeId() => 0;
}
