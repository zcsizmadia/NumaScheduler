namespace NumaScheduler.Platform;

/// <summary>
/// Opaque per-node data produced by <see cref="INumaPlatform.DiscoverNodes"/> and
/// consumed by <see cref="INumaPlatform.PinCurrentThreadToNode"/>.
/// Platform implementations subclass this type to carry OS-specific affinity state.
/// </summary>
internal abstract class NumaAffinityInfo
{
    /// <summary>OS-assigned NUMA node identifier.</summary>
    internal int NodeId { get; }

    /// <summary>Number of logical processors on this node.</summary>
    internal int ProcessorCount { get; }

    protected NumaAffinityInfo(int nodeId, int processorCount)
    {
        NodeId         = nodeId;
        ProcessorCount = processorCount;
    }

    /// <summary>
    /// Short human-readable description of the platform-specific affinity data
    /// appended to <see cref="NumaNode.ToString"/>.  May return an empty string.
    /// </summary>
    internal virtual string GetDescription() => string.Empty;
}
