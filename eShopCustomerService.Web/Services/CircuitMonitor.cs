using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace eShopCustomerService.Web.Services;

public sealed class CircuitMonitor : IDisposable
{
    private readonly ILogger<CircuitMonitor> logger;
    private readonly IDisposable shutdownRegistration;
    private readonly ConcurrentDictionary<string, Circuit> connected = new();
    private readonly ConcurrentDictionary<string, byte> live = new();
    private readonly object drainLock = new();
    private Task<DrainResult>? currentDrain;
    private int opened;
    private int closed;
    private int draining;

    public CircuitMonitor(IHostApplicationLifetime lifetime, ILogger<CircuitMonitor> logger)
    {
        this.logger = logger;
        // Resolve after Kestrel starts. Hold shutdown notification while clients pause,
        // before the server cancels and tears down their connections.
        shutdownRegistration = lifetime.ApplicationStopping.Register(() =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try
            {
                var result = DrainAsync(timeout.Token).GetAwaiter().GetResult();
                if (!result.Completed)
                {
                    logger.LogWarning("Shutdown pause timed out with {Remaining} connected circuits", result.Remaining);
                }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                logger.LogWarning("Shutdown pause exceeded its budget; some state may not be preserved");
            }
        });
    }

    public void Open(Circuit circuit)
    {
        live.TryAdd(circuit.Id, 0);
        Interlocked.Increment(ref opened);
        logger.LogInformation("Circuit {CircuitId} opened", circuit.Id);
    }

    public void Connect(Circuit circuit) => connected[circuit.Id] = circuit;
    public void Disconnect(Circuit circuit) => connected.TryRemove(circuit.Id, out _);

    public void Close(Circuit circuit)
    {
        connected.TryRemove(circuit.Id, out _);
        live.TryRemove(circuit.Id, out _);
        Interlocked.Increment(ref closed);
        logger.LogInformation("Circuit {CircuitId} closed", circuit.Id);
    }

    public object Snapshot(int loads)
    {
        using var process = Process.GetCurrentProcess();
        return new
        {
            processId = Environment.ProcessId,
            connected = connected.Count,
            live = live.Count,
            opened = Volatile.Read(ref opened),
            closed = Volatile.Read(ref closed),
            contextLoads = loads,
            managedBytes = GC.GetTotalMemory(false),
            workingSetBytes = process.WorkingSet64,
            draining = Volatile.Read(ref draining) != 0
        };
    }

    public Task<DrainResult> DrainAsync(CancellationToken ct)
    {
        lock (drainLock)
        {
            if (currentDrain is null || currentDrain.IsCompleted)
            {
                currentDrain = DrainCoreAsync();
            }
            return currentDrain.WaitAsync(ct);
        }
    }

    private async Task<DrainResult> DrainCoreAsync()
    {
        Interlocked.Exchange(ref draining, 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var requested = 0;
            foreach (var circuit in connected.Values.ToArray())
            {
                if (await circuit.RequestCircuitPauseAsync(timeout.Token))
                {
                    requested++;
                }
            }
            var deadline = Stopwatch.StartNew();
            while (!connected.IsEmpty && deadline.Elapsed < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(100, timeout.Token);
            }
            var remaining = connected.Count;
            logger.LogInformation("Requested {Requested} pauses; {Remaining} connections remain", requested, remaining);
            return new DrainResult(requested, remaining, remaining == 0);
        }
        finally
        {
            Volatile.Write(ref draining, 0);
        }
    }

    public void Dispose() => shutdownRegistration.Dispose();
}

public sealed record DrainResult(int Requested, int Remaining, bool Completed);

public sealed class CustomerServiceCircuitHandler(CircuitMonitor monitor) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken ct)
    {
        monitor.Open(circuit);
        return Task.CompletedTask;
    }

    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken ct)
    {
        monitor.Connect(circuit);
        return Task.CompletedTask;
    }

    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken ct)
    {
        monitor.Disconnect(circuit);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken ct)
    {
        monitor.Close(circuit);
        return Task.CompletedTask;
    }
}
