# NumaScheduler

A cross-platform .NET `TaskScheduler` that pins worker threads to **NUMA nodes**, keeping task execution and memory access local to the same CPU socket. Supports **Windows**, **Linux**, and falls back gracefully on **macOS** and other platforms.

[![NuGet](https://img.shields.io/nuget/v/NumaScheduler.svg)](https://www.nuget.org/packages/NumaScheduler)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

---

## Why NUMA-aware scheduling?

On multi-socket servers, accessing memory attached to a remote NUMA node is significantly slower than accessing local memory. By keeping threads and their data on the same node you reduce cross-socket memory traffic and improve throughput and latency for data-intensive workloads.

---

## Supported platforms

| Platform | Topology discovery | Thread pinning |
|---|---|---|
| **Windows** | `GetNumaNodeProcessorMaskEx` (kernel32) | `SetThreadGroupAffinity` |
| **Linux** | `/sys/devices/system/node/` (sysfs) | `sched_setaffinity` |
| **macOS / other** | Single virtual node | No-op (graceful fallback) |

---

## Target frameworks

`netstandard2.0` · `net8.0` · `net9.0` · `net10.0`

---

## Installation

```
dotnet add package NumaScheduler
```

---

## Quick start

### 1. Inspect topology

```csharp
var topology = NumaTopology.Instance;

Console.WriteLine($"NUMA nodes: {topology.NodeCount}, IsNumaSystem: {topology.IsNumaSystem}");
foreach (var node in topology.Nodes)
    Console.WriteLine(node); // "NUMA Node 0 | 16 logical CPU(s) | ..."
```

### 2. Policy-driven scheduler (recommended)

```csharp
// One thread pool per NUMA node; tasks routed to the calling thread's local node.
using var scheduler = new NumaAwareTaskScheduler(NumaSchedulingPolicy.LocalityFirst);

var task = Task.Factory.StartNew(() =>
{
    // Runs on the NUMA node closest to the enqueueing thread.
}, CancellationToken.None, TaskCreationOptions.PreferFairness, scheduler);

await task;
```

Available policies:

| Policy | Description |
|---|---|
| `RoundRobin` | Distribute tasks evenly across all nodes |
| `LocalityFirst` | Route to the enqueueing thread's own node (default for `Shared`) |
| `LeastLoaded` | Route to the node whose queue is shortest |

### 3. Pin a task to a specific node

```csharp
// Bypass the policy and force execution on node 1.
await NumaAwareTaskScheduler.Shared.RunOnNode(nodeIndex: 1, () =>
{
    ProcessChunk(data);
});

// Generic overload returns a result.
double result = await NumaAwareTaskScheduler.Shared.RunOnNode(1, () => ComputeSum(data));
```

### 4. Extension methods

```csharp
// Run on a specific node using the global Shared scheduler.
await ((Action)(() => Process(data))).RunOnNumaNode(nodeIndex: 0);

// Use LocalityFirst policy via the global Shared scheduler.
await ((Action)(() => Process(data))).RunNumaLocal();
```

### 5. Standalone per-node scheduler

Useful when you need a plain `TaskScheduler` reference for an API that doesn't accept `NumaAwareTaskScheduler`.

```csharp
var node = NumaTopology.Instance.Nodes[0];

using var nodeScheduler = new NumaNodeScheduler(node, threadCount: 4);

await Task.Factory.StartNew(() => Work(), CancellationToken.None,
    TaskCreationOptions.None, nodeScheduler);
```

### 6. Process-wide singleton

`NumaAwareTaskScheduler.Shared` is a lazily-initialised process-lifetime singleton using `LocalityFirst`.  
Do **not** dispose it.

```csharp
await Task.Factory.StartNew(Work, CancellationToken.None,
    TaskCreationOptions.PreferFairness, NumaAwareTaskScheduler.Shared);
```

---

## Diagnostics

```csharp
foreach (var (nodeId, pending) in scheduler.GetNodeStats())
    Console.WriteLine($"Node {nodeId}: {pending} pending tasks");
```

---

## Thread safety

All public types are thread-safe. `Dispose` may be called from any thread; subsequent calls are no-ops.

---

## License

[MIT](LICENSE) © 2026 Zoltan Csizmadia
