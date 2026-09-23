using System.Text.Json;

namespace UN.Nexo.Core.Services;

internal static class AtomicJsonFile
{
    internal static async Task WriteAsync<T>(
        string path,
        T value,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        using var publishLease = await PathKeyedLock.AcquireAsync(
            fullPath,
            cancellationToken);
        await WriteUnlockedAsync(
            fullPath,
            value,
            options,
            cancellationToken);
    }

    internal static async Task WriteUnlockedAsync<T>(
        string path,
        T value,
        JsonSerializerOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("JSON destination has no parent directory.");
        PhysicalPathGuard.EnsureDirectoryForFile(
            fullPath,
            "JSON destination");
        PhysicalPathGuard.EnsureRegularFileOrMissing(
            fullPath,
            "JSON destination");

        var tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            PhysicalPathGuard.EnsureDirectoryChainPhysical(
                directory,
                "JSON destination parent");
            PhysicalPathGuard.EnsureRegularFileOrMissing(
                tempPath,
                "JSON temporary file");

            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    options,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            PhysicalPathGuard.EnsureDirectoryChainPhysical(
                directory,
                "JSON destination parent");
            PhysicalPathGuard.EnsureRegularFileOrMissing(
                tempPath,
                "JSON temporary file");
            PhysicalPathGuard.EnsureRegularFileOrMissing(
                fullPath,
                "JSON destination");

            cancellationToken.ThrowIfCancellationRequested();
            await PublishWithRetryAsync(
                tempPath,
                fullPath,
                cancellationToken);
        }
        finally
        {
            try
            {
                if (PhysicalPathGuard.CanSafelyDeleteRegularFile(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup. If parent provenance changed, leaving a
                // launcher temp artifact is safer than following the new path.
            }
        }
    }

    private static async Task PublishWithRetryAsync(
        string tempPath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 100;
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var directory = Path.GetDirectoryName(destinationPath)
                    ?? throw new InvalidOperationException(
                        "JSON destination has no parent directory.");
                PhysicalPathGuard.EnsureDirectoryChainPhysical(
                    directory,
                    "JSON destination parent");
                PhysicalPathGuard.EnsureRegularFileOrMissing(
                    tempPath,
                    "JSON temporary file");
                PhysicalPathGuard.EnsureRegularFileOrMissing(
                    destinationPath,
                    "JSON destination");

                File.Move(tempPath, destinationPath, overwrite: true);
                return;
            }
            catch (Exception ex) when (
                attempt < maxAttempts
                && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
        }
    }
}
