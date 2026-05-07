using System.Collections.Concurrent;
using NumaScheduler.Platform;

namespace NumaScheduler;

/// <summary>
/// A <see cref="TaskScheduler"/> that executes tasks exclusively on a dedicated
/// thread pool whose threads are pinned to a single NUMA node, keeping both
/// execution and (ideally) the data it touches local to that node's memory.
/// </summary>
/// <remarks>
/// <para>
/// Thread-count defaults to the number of logical processors on the node.
/// Over-subscribing is possible but rarely beneficial; under-subscribing with
/// one thread per node is useful for latency-sensitive, serialised workloads.
/// </para>
/// <para>
/// Dispose the scheduler when it is no longer needed to release its threads.
/// In-flight tasks will be allowed to complete; queued-but-unstarted tasks may
/// be abandoned after the join timeout.
/// </para>
/// </remarks>
public sealed class NumaNodeScheduler : TaskScheduler, IDisposable
{
    private readonly NumaNode                   _node;
    private readonly Thread[]                   _threads;
    private readonly BlockingCollection<Task>   _queue;
    private readonly CancellationTokenSource    _cts = new();
    private          int                        _disposed;

    // ── Properties ───────────────────────────────────────────────────────────

    /// <summary>The NUMA node this scheduler is pinned to.</summary>
    public NumaNode Node => _node;

    /// <summary>Number of worker threads in the pool.</summary>
    public int ThreadCount => _threads.Length;

    /// <summary>Approximate number of tasks waiting to be executed.</summary>
    public int PendingTaskCount => _queue.Count;

    // ── Construction ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new <see cref="NumaNodeScheduler"/> whose threads are pinned to
    /// <paramref name="node"/>.
    /// </summary>
    /// <param name="node">Target NUMA node.</param>
    /// <param name="threadCount">
    /// Worker-thread count.  Defaults to the logical-processor count of <paramref name="node"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="node"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="threadCount"/> is &lt; 1.</exception>
    public NumaNodeScheduler(NumaNode node, int? threadCount = null)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        int count = threadCount ?? node.ProcessorCount;
        if (count < 1)
            throw new ArgumentOutOfRangeException(nameof(threadCount), "Must be at least 1.");

        _node    = node;
        _queue   = new BlockingCollection<Task>(new ConcurrentQueue<Task>());
        _threads = new Thread[count];

        for (int i = 0; i < count; i++)
        {
            var t = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name         = $"NUMA-N{node.NodeId}-W{i}",
            };
            _threads[i] = t;
            t.Start();
        }
    }

    // ── Worker loop ──────────────────────────────────────────────────────────

    private void WorkerLoop()
    {
        PinCurrentThreadToNode(_node);

        try
        {
            foreach (var task in _queue.GetConsumingEnumerable(_cts.Token))
                TryExecuteTask(task);
        }
        catch (OperationCanceledException) { /* graceful shutdown */ }
    }

    /// <summary>
    /// Pins the calling thread to <paramref name="node"/> via the active
    /// <see cref="INumaPlatform"/> implementation.  Called internally and reused by
    /// <see cref="NumaAwareTaskScheduler"/>.
    /// </summary>
    internal static void PinCurrentThreadToNode(NumaNode node) =>
        NumaPlatformFactory.GetPlatform().PinCurrentThreadToNode(node.AffinityInfo);

    // ── TaskScheduler overrides ───────────────────────────────────────────────

    /// <inheritdoc/>
    protected override void QueueTask(Task task)
    {
        if (_disposed == 1) throw new ObjectDisposedException(GetType().Name);
        _queue.Add(task);
    }

    /// <inheritdoc/>
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
    {
        if (_disposed == 1) return false;
        // Inline only when the calling thread is already running on this NUMA node,
        // so we do not accidentally move work to the wrong node.
        return IsCallerOnThisNode() && TryExecuteTask(task);
    }

    /// <inheritdoc/>
    protected override IEnumerable<Task> GetScheduledTasks() => _queue.ToArray();

    // ── Helpers ──────────────────────────────────────────────────────────────

    private bool IsCallerOnThisNode()
    {
        int nodeId = NumaPlatformFactory.GetPlatform().GetCurrentNodeId();
        return nodeId >= 0 && nodeId == _node.NodeId;
    }

    // ── Disposal ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Signals the worker threads to stop accepting new work, waits up to 5 s for
    /// in-flight tasks to finish, then releases resources.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;

        _cts.Cancel();
        _queue.CompleteAdding();

        foreach (var t in _threads)
            t.Join(millisecondsTimeout: 5_000);

        _queue.Dispose();
        _cts.Dispose();
    }
}
