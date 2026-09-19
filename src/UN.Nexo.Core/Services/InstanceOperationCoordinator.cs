using System.Collections.Concurrent;

namespace UN.Nexo.Core.Services;

public sealed class InstanceOperationCoordinator
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    private readonly NexoPathService _paths;

    public InstanceOperationCoordinator(NexoPathService paths)
    {
        _paths = paths;
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        string instanceId,
        string operation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException(
                "Operation name is required.",
                nameof(operation));

        var instanceRoot = _paths.GetInstanceDirectory(instanceId);
        var key = Path.GetFullPath(instanceRoot);
        var gate = Gates.GetOrAdd(
            key,
            static _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken);

        CrossProcessFileLock.Lease? crossProcessLease = null;
        try
        {
            var lockDirectory = Path.Combine(
                _paths.GetDataRoot(),
                "locks",
                "instances");
            var lockPath = Path.Combine(
                lockDirectory,
                instanceId + ".lock");

            crossProcessLease = await CrossProcessFileLock.AcquireAsync(
                lockPath,
                TimeSpan.FromMilliseconds(100),
                cancellationToken);

            return new Lease(
                gate,
                crossProcessLease);
        }
        catch
        {
            if (crossProcessLease is not null)
                await crossProcessLease.DisposeAsync();
            gate.Release();
            throw;
        }
    }

    private sealed class Lease : IAsyncDisposable
    {
        private SemaphoreSlim? _gate;
        private CrossProcessFileLock.Lease? _crossProcessLease;
        private bool _disposed;

        public Lease(
            SemaphoreSlim gate,
            CrossProcessFileLock.Lease crossProcessLease)
        {
            _gate = gate;
            _crossProcessLease = crossProcessLease;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
                return ValueTask.CompletedTask;
            _disposed = true;

            var crossProcessLease = Interlocked.Exchange(
                ref _crossProcessLease,
                null);
            if (crossProcessLease is not null)
                crossProcessLease.DisposeAsync().GetAwaiter().GetResult();

            var gate = Interlocked.Exchange(
                ref _gate,
                null);
            gate?.Release();

            return ValueTask.CompletedTask;
        }
    }
}
