namespace UN.Nexo.Core.Services;

/// <summary>
/// Serializes persisted-store mutation transactions both within this process and across
/// cooperating Nexo processes. The sidecar file is intentionally retained: ownership is
/// represented by the exclusive OS file handle, so a crashed process releases the lock
/// automatically without relying on lock-file deletion.
/// </summary>
internal static class PersistedStoreMutationLock
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    public static async ValueTask<Lease> AcquireAsync(
        string storePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storePath);

        var fullPath = Path.GetFullPath(storePath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Persisted store path has no parent directory.");
        Directory.CreateDirectory(directory);

        var processLease = await PathKeyedLock.AcquireAsync(
            fullPath,
            cancellationToken);

        CrossProcessFileLock.Lease? crossProcessLease = null;
        try
        {
            crossProcessLease = await CrossProcessFileLock.AcquireAsync(
                fullPath + ".lock",
                RetryDelay,
                cancellationToken);
            return new Lease(processLease, crossProcessLease);
        }
        catch
        {
            if (crossProcessLease is not null)
                await crossProcessLease.DisposeAsync();
            processLease.Dispose();
            throw;
        }
    }

    internal sealed class Lease : IAsyncDisposable
    {
        private PathKeyedLock.Lease? _processLease;
        private CrossProcessFileLock.Lease? _crossProcessLease;
        private bool _disposed;

        internal Lease(
            PathKeyedLock.Lease processLease,
            CrossProcessFileLock.Lease crossProcessLease)
        {
            _processLease = processLease;
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

            Interlocked.Exchange(ref _processLease, null)?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
