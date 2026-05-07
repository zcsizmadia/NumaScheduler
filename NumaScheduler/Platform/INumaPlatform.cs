namespace NumaScheduler.Platform;

/// <summary>
/// Abstraction over OS-specific NUMA APIs.
/// One implementation per supported platform, chosen at startup by
/// <see cref="NumaPlatformFactory"/>.
/// </summary>
internal interface INumaPlatform
{
    /// <summary>
    /// Queries the OS for all active NUMA nodes and returns one
    /// <see cref="NumaAffinityInfo"/> descriptor per node, in node-ID order.
    /// Throws only for unrecoverable initialisation failures; silently returns a
    /// single-node fallback for unsupported or degraded environments.
    /// </summary>
    IReadOnlyList<NumaAffinityInfo> DiscoverNodes();

    /// <summary>
    /// Pins the calling thread to the NUMA node described by
    /// <paramref name="info"/>.  Failures (e.g. missing permissions in a
    /// container) are silently ignored — threads still run, just without pinning.
    /// </summary>
    void PinCurrentThreadToNode(NumaAffinityInfo info);

    /// <summary>
    /// Returns the OS-assigned NUMA node ID for the logical processor currently
    /// executing the calling thread, or <c>-1</c> if it cannot be determined.
    /// </summary>
    int GetCurrentNodeId();
}
