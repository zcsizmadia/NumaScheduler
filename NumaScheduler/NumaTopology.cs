using System.Collections.ObjectModel;
using NumaScheduler.Platform;

namespace NumaScheduler;

/// <summary>
/// Describes a single NUMA node discovered by <see cref="NumaTopology"/>.
/// </summary>
public sealed class NumaNode
{
    /// <summary>Zero-based NUMA node identifier assigned by the OS.</summary>
    public int NodeId { get; }

    /// <summary>Number of logical processors on this node.</summary>
    public int ProcessorCount { get; }

    /// <summary>Platform-specific affinity data used for thread pinning.</summary>
    internal NumaAffinityInfo AffinityInfo { get; }

    internal NumaNode(NumaAffinityInfo info)
    {
        NodeId         = info.NodeId;
        ProcessorCount = info.ProcessorCount;
        AffinityInfo   = info;
    }

    /// <inheritdoc/>
    public override string ToString() =>
        $"NUMA Node {NodeId} | {ProcessorCount} logical CPU(s){AffinityInfo.GetDescription()}";
}

/// <summary>
/// Discovers and caches the NUMA topology of the current system.
/// Access the singleton via <see cref="Instance"/>.
/// </summary>
/// <remarks>
/// Supports Windows (processor groups), Linux (sysfs + sched_setaffinity),
/// and other platforms (single-node graceful fallback).
/// On systems with a single NUMA node all scheduling policies still operate
/// correctly; the locality benefit is simply absent.
/// </remarks>
public sealed class NumaTopology
{
    private static readonly Lazy<NumaTopology> _lazy =
        new(() => new NumaTopology(), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Process-wide singleton; lazily initialised on first access.</summary>
    public static NumaTopology Instance => _lazy.Value;

    /// <summary>All active NUMA nodes, ordered by node identifier.</summary>
    public ReadOnlyCollection<NumaNode> Nodes { get; }

    /// <summary>Total number of active NUMA nodes.</summary>
    public int NodeCount => Nodes.Count;

    /// <summary><c>true</c> when more than one NUMA node is active.</summary>
    public bool IsNumaSystem => NodeCount > 1;

    private readonly INumaPlatform _platform;

    // ── Construction ─────────────────────────────────────────────────────────

    private NumaTopology()
    {
        _platform = NumaPlatformFactory.GetPlatform();
        var infos  = _platform.DiscoverNodes();

        if (infos.Count == 0)
            throw new InvalidOperationException("No active NUMA nodes with processors were found.");

        Nodes = infos.Select(info => new NumaNode(info)).ToList().AsReadOnly();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the index (into <see cref="Nodes"/>) of the NUMA node that owns the
    /// logical processor currently executing the calling thread.
    /// Returns 0 if the mapping cannot be determined.
    /// </summary>
    public int GetCurrentNodeIndex()
    {
        int nodeId = _platform.GetCurrentNodeId();
        if (nodeId < 0) return 0;

        for (int i = 0; i < Nodes.Count; i++)
            if (Nodes[i].NodeId == nodeId) return i;

        return 0;
    }
}
