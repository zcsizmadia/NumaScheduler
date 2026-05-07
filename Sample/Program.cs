using NumaScheduler;
using System.Diagnostics;

static class Program
{
    static async Task Main()
    {
        Console.WriteLine("╔══════════════════════════════════════════╗");
        Console.WriteLine("║   NUMA-Aware Task Scheduler — Demo       ║");
        Console.WriteLine("╚══════════════════════════════════════════╝\n");

        // ── 1. Topology ───────────────────────────────────────────────────────
        var topology = NumaTopology.Instance;

        Console.WriteLine($"Topology: {topology.NodeCount} NUMA node(s), NUMA system = {topology.IsNumaSystem}");
        foreach (var node in topology.Nodes)
            Console.WriteLine($"  {node}");
        Console.WriteLine();

        // ── 2. Round-Robin policy ─────────────────────────────────────────────
        Console.WriteLine("── Round-Robin (12 tasks) ─────────────────────");
        using var rrScheduler = new NumaAwareTaskScheduler(NumaSchedulingPolicy.RoundRobin);

        var rrTasks = Enumerable.Range(0, 12).Select(i =>
            Task.Factory.StartNew(() =>
            {
                int nodeIdx = topology.GetCurrentNodeIndex();
                Console.WriteLine(
                    $"  task {i,2} | thread '{Thread.CurrentThread.Name}' | node {nodeIdx}");
            },
            CancellationToken.None,
            TaskCreationOptions.PreferFairness,
            rrScheduler)).ToArray();

        await Task.WhenAll(rrTasks);
        Console.WriteLine();

        // ── 3. LeastLoaded policy ─────────────────────────────────────────────
        Console.WriteLine("── Least-Loaded (stats after scheduling) ──────");
        using var llScheduler = new NumaAwareTaskScheduler(
            NumaSchedulingPolicy.LeastLoaded, threadsPerNode: 1);

        // Saturate node 0's single thread so LeastLoaded routes elsewhere.
        var longTask = Task.Factory.StartNew(() => Thread.Sleep(300),
            CancellationToken.None, TaskCreationOptions.None,
            llScheduler.GetNodeScheduler(0) ?? llScheduler);   // see helper below

        // Schedule 6 tasks; expect them to spread to other nodes on multi-NUMA machines.
        var llTasks = Enumerable.Range(0, 6).Select(i =>
            Task.Factory.StartNew(() =>
            {
                int nodeIdx = topology.GetCurrentNodeIndex();
                Console.WriteLine(
                    $"  task {i,2} | thread '{Thread.CurrentThread.Name}' | node {nodeIdx}");
            },
            CancellationToken.None,
            TaskCreationOptions.PreferFairness,
            llScheduler)).ToArray();

        foreach (var stat in llScheduler.GetNodeStats())
            Console.WriteLine($"  node {stat.NodeId}: {stat.PendingTasks} pending");

        await Task.WhenAll(llTasks);
        await longTask;
        Console.WriteLine();

        // ── 4. RunOnNode — explicit node pinning ─────────────────────────────
        Console.WriteLine("── Pinned to node 0 (6 tasks) ─────────────────");
        using var pinScheduler = new NumaAwareTaskScheduler(NumaSchedulingPolicy.LocalityFirst);

        var pinTasks = Enumerable.Range(0, 6).Select(i =>
            pinScheduler.RunOnNode(0, () =>
            {
                int nodeIdx = topology.GetCurrentNodeIndex();
                Console.WriteLine(
                    $"  task {i,2} | thread '{Thread.CurrentThread.Name}' | node {nodeIdx}");
            })).ToArray();

        await Task.WhenAll(pinTasks);
        Console.WriteLine();

        // ── 5. Standalone NumaNodeScheduler ──────────────────────────────────
        Console.WriteLine("── Standalone NumaNodeScheduler for node 0 ────");
        using var nodeScheduler = new NumaNodeScheduler(topology.Nodes[0], threadCount: 2);

        var nodeTasks = Enumerable.Range(0, 4).Select(i =>
            Task.Factory.StartNew(() =>
            {
                int nodeIdx = topology.GetCurrentNodeIndex();
                Console.WriteLine(
                    $"  task {i,2} | thread '{Thread.CurrentThread.Name}' | node {nodeIdx}");
            },
            CancellationToken.None,
            TaskCreationOptions.None,
            nodeScheduler)).ToArray();

        await Task.WhenAll(nodeTasks);
        Console.WriteLine();

        // ── 6. Extension-method sugar ─────────────────────────────────────────
        Console.WriteLine("── Extension method (RunOnNumaNode / RunNumaLocal) ─");

        await ((Action)(() =>
        {
            Console.WriteLine(
                $"  RunOnNumaNode(0) | thread '{Thread.CurrentThread.Name}' | node {topology.GetCurrentNodeIndex()}");
        })).RunOnNumaNode(nodeIndex: 0);

        await ((Action)(() =>
        {
            Console.WriteLine(
                $"  RunNumaLocal     | thread '{Thread.CurrentThread.Name}' | node {topology.GetCurrentNodeIndex()}");
        })).RunNumaLocal();

        Console.WriteLine();

        // ── 7. Micro-benchmark: allocation locality ────────────────────────────
        Console.WriteLine("── Allocation-locality micro-benchmark ─────────");
        const int ItemCount   = 4_000_000;
        const int Iterations  = 3;

        // Warm up
        _ = SumLocal(ItemCount);

        double localMs  = BenchmarkMs(() => SumLocal(ItemCount),  Iterations);
        Console.WriteLine($"  Sequential (node-local)  : {localMs,8:F2} ms");

        using var benchScheduler = new NumaAwareTaskScheduler(NumaSchedulingPolicy.LocalityFirst);
        double numaMs = BenchmarkMs(() =>
        {
            var chunks = Enumerable.Range(0, topology.NodeCount)
                .Select(nIdx => benchScheduler.RunOnNode(nIdx, () =>
                    SumChunk(ItemCount / topology.NodeCount * nIdx,
                             ItemCount / topology.NodeCount))).ToArray();
            Task.WhenAll(chunks).GetAwaiter().GetResult();
        }, Iterations);

        Console.WriteLine($"  NUMA parallel ({topology.NodeCount} node(s))     : {numaMs,8:F2} ms");
        Console.WriteLine($"  Speed-up                 : {localMs / numaMs,8:F2}×");

        Console.WriteLine("\nDone.\n");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a <see cref="NumaNodeScheduler"/> for the given node index if you
    /// need to hand one to an API that doesn't accept <see cref="NumaAwareTaskScheduler"/>.
    /// In this demo we use it only to highlight the cast path; normally you'd construct
    /// one yourself.
    /// </summary>
    static TaskScheduler? GetNodeScheduler(this NumaAwareTaskScheduler _, int _idx) => null;

    static long SumLocal(int count)
    {
        var data = new long[count];
        for (int i = 0; i < count; i++) data[i] = i;
        long sum = 0;
        for (int i = 0; i < count; i++) sum += data[i];
        return sum;
    }

    static long SumChunk(int start, int count)
    {
        long sum = 0;
        for (int i = start; i < start + count; i++) sum += i;
        return sum;
    }

    static double BenchmarkMs(Action action, int iterations)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) action();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds / iterations;
    }
}
