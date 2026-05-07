using System.Collections.Concurrent;

namespace NumaScheduler;

// ── Policy enum ──────────────────────────────────────────────────────────────

/// <summary>Controls how <see cref="NumaAwareTaskScheduler"/> picks a NUMA node for each task.</summary>
public enum NumaSchedulingPolicy
{
    /// <summary>
    /// Tasks are distributed across nodes in round-robin order.
    /// Provides even load balance regardless of the enqueueing thread's location.
    /// </summary>
    RoundRobin,

    /// <summary>
    /// Tasks are preferentially routed to the NUMA node that owns the processor
    /// currently executing the enqueueing thread.  Falls back to round-robin when
    /// the calling thread's node cannot be determined.
    /// Best for workloads where producer and consumer share data.
    /// </summary>
    LocalityFirst,

    /// <summary>
    /// Tasks are routed to the node whose worker queue currently has the fewest
    /// pending tasks.  Useful for heterogeneous workloads with uneven task durations.
    /// </summary>
    LeastLoaded,
}

// ── NumaAwareTaskScheduler ───────────────────────────────────────────────────

/// <summary>
/// A <see cref="TaskScheduler"/> that maintains one thread pool per NUMA node and
/// routes tasks across them according to a configurable <see cref="NumaSchedulingPolicy"/>.
/// </summary>
/// <remarks>
/// <para>
/// Use <see cref="RunOnNode(int,Action,CancellationToken)"/> /
/// <see cref="RunOnNode{TResult}(int,Func{TResult},CancellationToken)"/> to pin individual
/// tasks to a specific node.  Use <c>Task.Factory.StartNew</c> with this scheduler
/// as the last argument for policy-driven routing.
/// </para>
/// <para>
/// The <see cref="Shared"/> singleton uses <see cref="NumaSchedulingPolicy.LocalityFirst"/>
/// and is suitable for most producer-consumer pipelines.
/// </para>
/// </remarks>
public sealed class NumaAwareTaskScheduler : TaskScheduler, IDisposable
{
    // ── Inner per-node worker group ───────────────────────────────────────────

    /// <summary>
    /// Owns the threads and work queue for one NUMA node.
    /// Threads call <c>owner.TryExecuteTask</c> so that the task's owning scheduler
    /// is always <see cref="NumaAwareTaskScheduler"/> — required by the TPL contract.
    /// </summary>
    private sealed class NodeGroup : IDisposable
    {
        public  readonly int                         NodeId;
        public  readonly BlockingCollection<Task>    Queue = new(new ConcurrentQueue<Task>());
        private readonly Thread[]                    _threads;
        private readonly CancellationTokenSource     _cts = new();
        private          int                         _disposed;

        public int PendingCount => Queue.Count;

        public NodeGroup(NumaNode node, NumaAwareTaskScheduler owner, int threadCount)
        {
            NodeId   = node.NodeId;
            _threads = new Thread[threadCount];

            for (int i = 0; i < threadCount; i++)
            {
                int idx = i;
                var t = new Thread(() =>
                {
                    NumaNodeScheduler.PinCurrentThreadToNode(node);
                    try
                    {
                        foreach (var task in Queue.GetConsumingEnumerable(_cts.Token))
                            owner.TryExecuteTask(task);  // owner IS the task's scheduler
                    }
                    catch (OperationCanceledException) { }
                })
                {
                    IsBackground = true,
                    Name         = $"NUMA-Aware-N{node.NodeId}-W{idx}",
                };
                _threads[i] = t;
                t.Start();
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
            _cts.Cancel();
            Queue.CompleteAdding();
            foreach (var t in _threads)
                t.Join(millisecondsTimeout: 5_000);
            Queue.Dispose();
            _cts.Dispose();
        }
    }

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly NumaTopology         _topology;
    private readonly NodeGroup[]          _groups;
    private readonly NumaSchedulingPolicy _policy;
    private          int                  _rrIndex;   // round-robin counter
    private          int                  _disposed;

    /// <summary>
    /// Per-thread override: when set by <see cref="RunOnNode"/>, <see cref="QueueTask"/>
    /// routes the next task to this specific node index and then clears the override.
    /// Using nullable int avoids the ambiguity between "not set" and "node 0".
    /// </summary>
    [ThreadStatic]
    private static int? _forcedNodeIndex;

    // ── Singleton ─────────────────────────────────────────────────────────────

    private static readonly Lazy<NumaAwareTaskScheduler> _shared =
        new(() => new NumaAwareTaskScheduler(NumaSchedulingPolicy.LocalityFirst),
            LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Process-wide shared scheduler using <see cref="NumaSchedulingPolicy.LocalityFirst"/>.
    /// Lazily created on first access; never disposed (process lifetime).
    /// </summary>
    public static NumaAwareTaskScheduler Shared => _shared.Value;

    // ── Constructor ───────────────────────────────────────────────────────────

    /// <summary>
    /// Initialises a new <see cref="NumaAwareTaskScheduler"/>.
    /// </summary>
    /// <param name="policy">Routing policy to apply when no explicit node is specified.</param>
    /// <param name="threadsPerNode">
    /// Worker-thread count per NUMA node.
    /// Defaults to the logical-processor count of each node.
    /// </param>
    public NumaAwareTaskScheduler(
        NumaSchedulingPolicy policy          = NumaSchedulingPolicy.LocalityFirst,
        int?                 threadsPerNode  = null)
    {
        _topology = NumaTopology.Instance;
        _policy   = policy;
        _groups   = _topology.Nodes
            .Select(n => new NodeGroup(n, this, threadsPerNode ?? n.ProcessorCount))
            .ToArray();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>The NUMA topology in use.</summary>
    public NumaTopology Topology => _topology;

    /// <summary>Number of NUMA nodes managed by this scheduler.</summary>
    public int NodeCount => _groups.Length;

    /// <summary>
    /// Schedules <paramref name="action"/> to run on NUMA node <paramref name="nodeIndex"/>,
    /// bypassing the normal routing policy.
    /// </summary>
    /// <param name="nodeIndex">Zero-based index into <see cref="NumaTopology.Nodes"/>.</param>
    /// <param name="action">The work to execute on the target node.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public Task RunOnNode(int nodeIndex, Action action,
        CancellationToken cancellationToken = default)
    {
        ValidateNodeIndex(nodeIndex);
        _forcedNodeIndex = nodeIndex;
        try
        {
            return Task.Factory.StartNew(action, cancellationToken,
                TaskCreationOptions.PreferFairness, this);
        }
        finally
        {
            _forcedNodeIndex = null;
        }
    }

    /// <summary>
    /// Schedules <paramref name="func"/> to run on NUMA node <paramref name="nodeIndex"/>,
    /// bypassing the normal routing policy.
    /// </summary>
    /// <param name="nodeIndex">Zero-based index into <see cref="NumaTopology.Nodes"/>.</param>
    /// <param name="func">The work to execute on the target node.</param>
    /// <param name="cancellationToken">Optional cancellation token.</param>
    public Task<TResult> RunOnNode<TResult>(int nodeIndex, Func<TResult> func,
        CancellationToken cancellationToken = default)
    {
        ValidateNodeIndex(nodeIndex);
        _forcedNodeIndex = nodeIndex;
        try
        {
            return Task.Factory.StartNew(func, cancellationToken,
                TaskCreationOptions.PreferFairness, this);
        }
        finally
        {
            _forcedNodeIndex = null;
        }
    }

    /// <summary>
    /// Returns a snapshot of per-node diagnostic statistics.
    /// </summary>
    public IReadOnlyList<(int NodeId, int PendingTasks)> GetNodeStats() =>
        Array.AsReadOnly(
            _groups.Select(g => (g.NodeId, g.PendingCount)).ToArray());

    // ── TaskScheduler overrides ────────────────────────────────────────────────

    /// <inheritdoc/>
    protected override void QueueTask(Task task)
    {
        if (_disposed == 1) throw new ObjectDisposedException(GetType().Name);
        PickGroup().Queue.Add(task);
    }

    /// <inheritdoc/>
    protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
    {
        if (_disposed == 1) return false;
        // Allow inlining: the calling thread is likely already on the "right" node when
        // a NUMA-aware task awaits a child task synchronously.
        return TryExecuteTask(task);
    }

    /// <inheritdoc/>
    protected override IEnumerable<Task> GetScheduledTasks() =>
        _groups.SelectMany(g => g.Queue.ToArray());

    // ── Routing ───────────────────────────────────────────────────────────────

    private NodeGroup PickGroup()
    {
        // Explicit per-call override set by RunOnNode (ThreadStatic, so no race).
        if (_forcedNodeIndex.HasValue)
            return _groups[_forcedNodeIndex.Value];

        return _policy switch
        {
            NumaSchedulingPolicy.RoundRobin    => RoundRobin(),
            NumaSchedulingPolicy.LocalityFirst => LocalityFirst(),
            NumaSchedulingPolicy.LeastLoaded   => LeastLoaded(),
            _                                  => RoundRobin(),
        };
    }

    private NodeGroup RoundRobin()
    {
        // Use unsigned modulo to avoid negative results after overflow.
        int i = (int)((uint)Interlocked.Increment(ref _rrIndex) % (uint)_groups.Length);
        return _groups[i];
    }

    private NodeGroup LocalityFirst()
    {
        int idx = _topology.GetCurrentNodeIndex();
        int clamped = idx < 0 ? 0 : idx >= _groups.Length ? _groups.Length - 1 : idx;
        return _groups[clamped];
    }

    private NodeGroup LeastLoaded()
    {
        NodeGroup best = _groups[0];
        for (int i = 1; i < _groups.Length; i++)
            if (_groups[i].PendingCount < best.PendingCount)
                best = _groups[i];
        return best;
    }

    private void ValidateNodeIndex(int index)
    {
        if ((uint)index >= (uint)_groups.Length)
            throw new ArgumentOutOfRangeException(nameof(index),
                $"Node index must be in [0, {_groups.Length - 1}]. Got {index}.");
    }

    // ── Disposal ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Signals all node worker groups to stop, waits for in-flight tasks, then
    /// releases resources.  Do not dispose the <see cref="Shared"/> singleton.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0) return;
        foreach (var g in _groups) g.Dispose();
    }
}
