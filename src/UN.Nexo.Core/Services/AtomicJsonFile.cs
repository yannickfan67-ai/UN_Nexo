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
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("JSON destination has no parent directory.");
        Directory.CreateDirectory(directory);

        using var publishLease = await PathKeyedLock.AcquireAsync(
            fullPath,
            cancellationToken);
        var tempPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
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
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup. Never remove or truncate the destination.
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
}
