namespace NumaScheduler;

/// <summary>
/// Extension methods that provide ergonomic access to NUMA-aware scheduling
/// without needing to hold a direct reference to a scheduler instance.
/// </summary>
public static class NumaTaskExtensions
{
    /// <summary>
    /// Runs <paramref name="action"/> on the specified NUMA node using the
    /// <see cref="NumaAwareTaskScheduler.Shared"/> scheduler.
    /// </summary>
    /// <param name="action">The work to perform.</param>
    /// <param name="nodeIndex">Zero-based NUMA node index.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public static Task RunOnNumaNode(
        this Action        action,
        int                nodeIndex,
        CancellationToken  cancellationToken = default) =>
        NumaAwareTaskScheduler.Shared.RunOnNode(nodeIndex, action, cancellationToken);

    /// <summary>
    /// Runs <paramref name="func"/> on the specified NUMA node using the
    /// <see cref="NumaAwareTaskScheduler.Shared"/> scheduler.
    /// </summary>
    /// <typeparam name="TResult">Return type of the function.</typeparam>
    /// <param name="func">The work to perform.</param>
    /// <param name="nodeIndex">Zero-based NUMA node index.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public static Task<TResult> RunOnNumaNode<TResult>(
        this Func<TResult> func,
        int                nodeIndex,
        CancellationToken  cancellationToken = default) =>
        NumaAwareTaskScheduler.Shared.RunOnNode(nodeIndex, func, cancellationToken);

    /// <summary>
    /// Schedules <paramref name="action"/> using the
    /// <see cref="NumaAwareTaskScheduler.Shared"/> scheduler with the
    /// <see cref="NumaSchedulingPolicy.LocalityFirst"/> policy automatically applied.
    /// </summary>
    public static Task RunNumaLocal(
        this Action        action,
        CancellationToken  cancellationToken = default) =>
        Task.Factory.StartNew(action, cancellationToken,
            TaskCreationOptions.PreferFairness, NumaAwareTaskScheduler.Shared);
}
