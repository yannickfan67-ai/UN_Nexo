using System.Collections.Concurrent;
using System.Text;

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

        FileStream? lockStream = null;
        try
        {
            var lockDirectory = Path.Combine(
                _paths.GetDataRoot(),
                "locks",
                "instances");
            Directory.CreateDirectory(lockDirectory);
            var lockPath = Path.Combine(
                lockDirectory,
                instanceId + ".lock");

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    lockStream = new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        4096,
                        FileOptions.Asynchronous
                        | FileOptions.WriteThrough);
                    break;
                }
                catch (IOException) when (
                    !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken);
                }
            }

            lockStream.SetLength(0);
            var metadata = Encoding.UTF8.GetBytes(
                $"pid={Environment.ProcessId}\n"
                + $"operation={operation}\n"
                + $"started={DateTimeOffset.UtcNow:O}\n");
            await lockStream.WriteAsync(
                metadata,
                cancellationToken);
            await lockStream.FlushAsync(
                cancellationToken);

            return new Lease(
                gate,
                lockStream);
        }
        catch
        {
            lockStream?.Dispose();
            gate.Release();
            throw;
        }
    }

    private sealed class Lease : IAsyncDisposable
    {
        private SemaphoreSlim? _gate;
        private FileStream? _lockStream;
        private bool _disposed;

        public Lease(
            SemaphoreSlim gate,
            FileStream lockStream)
        {
            _gate = gate;
            _lockStream = lockStream;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
                return ValueTask.CompletedTask;
            _disposed = true;

            var stream = Interlocked.Exchange(
                ref _lockStream,
                null);
            stream?.Dispose();

            var gate = Interlocked.Exchange(
                ref _gate,
                null);
            gate?.Release();

            return ValueTask.CompletedTask;
        }
    }
}
