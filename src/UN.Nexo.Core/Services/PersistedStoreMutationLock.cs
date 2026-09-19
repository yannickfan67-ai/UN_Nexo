using System.Text;

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

        FileStream? crossProcessStream = null;
        try
        {
            var lockPath = fullPath + ".lock";
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    crossProcessStream = new FileStream(
                        lockPath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None,
                        4096,
                        FileOptions.Asynchronous | FileOptions.WriteThrough);
                    break;
                }
                catch (IOException) when (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(RetryDelay, cancellationToken);
                }
            }

            crossProcessStream.SetLength(0);
            var metadata = Encoding.UTF8.GetBytes(
                $"pid={Environment.ProcessId}\n"
                + $"store={Path.GetFileName(fullPath)}\n"
                + $"started={DateTimeOffset.UtcNow:O}\n");
            await crossProcessStream.WriteAsync(metadata, cancellationToken);
            await crossProcessStream.FlushAsync(cancellationToken);

            return new Lease(processLease, crossProcessStream);
        }
        catch
        {
            crossProcessStream?.Dispose();
            processLease.Dispose();
            throw;
        }
    }

    internal sealed class Lease : IAsyncDisposable
    {
        private PathKeyedLock.Lease? _processLease;
        private FileStream? _crossProcessStream;
        private bool _disposed;

        internal Lease(
            PathKeyedLock.Lease processLease,
            FileStream crossProcessStream)
        {
            _processLease = processLease;
            _crossProcessStream = crossProcessStream;
        }

        public ValueTask DisposeAsync()
        {
            if (_disposed)
                return ValueTask.CompletedTask;
            _disposed = true;

            var stream = Interlocked.Exchange(ref _crossProcessStream, null);
            stream?.Dispose();

            var processLease = Interlocked.Exchange(ref _processLease, null);
            processLease?.Dispose();

            return ValueTask.CompletedTask;
        }
    }
}
