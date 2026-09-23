namespace UN.Nexo.Core.Services;

/// <summary>
/// Exclusive cross-process file lock. The sidecar is intentionally contentless:
/// ownership is represented only by the OS file handle, so acquiring a lock never
/// truncates or writes through a path that could have been replaced with a link.
/// </summary>
internal static class CrossProcessFileLock
{
    internal static async ValueTask<Lease> AcquireAsync(
        string lockPath,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        if (retryDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));

        var fullPath = Path.GetFullPath(lockPath);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("Lock path has no parent directory.");
        PhysicalPathGuard.EnsureDirectoryForFile(
            fullPath,
            "Cross-process lock");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PhysicalPathGuard.EnsureDirectoryChainPhysical(
                directory,
                "Cross-process lock parent");
            RejectLinkedLockPath(fullPath);

            FileStream? stream = null;
            try
            {
                stream = new FileStream(
                    fullPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous);

                // Re-check both the parent chain and final entry after open so
                // replacements at the mutation boundary fail closed.
                PhysicalPathGuard.EnsureDirectoryChainPhysical(
                    directory,
                    "Cross-process lock parent");
                RejectLinkedLockPath(fullPath);
                return new Lease(stream);
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                stream?.Dispose();
                await Task.Delay(retryDelay, cancellationToken);
            }
            catch
            {
                stream?.Dispose();
                throw;
            }
        }
    }

    private static void RejectLinkedLockPath(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException(
                    "Cross-process lock sidecars cannot be symbolic links or reparse points.");
        }
        catch (FileNotFoundException)
        {
            // The normal first-acquisition path: OpenOrCreate will create a regular file.
        }
        catch (DirectoryNotFoundException)
        {
            // Parent creation is handled before acquisition; treat a concurrent removal
            // like an absent file so the following open supplies the normal failure.
        }
    }

    internal sealed class Lease : IAsyncDisposable
    {
        private FileStream? _stream;

        internal Lease(FileStream stream) => _stream = stream;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _stream, null)?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
