using System.Collections.Concurrent;

namespace UN.Nexo.Core.Services;

/// <summary>
/// Process-wide keyed lock for canonical filesystem paths. Entries are reference-counted so
/// completed keys do not remain in the registry, while holders and queued waiters continue
/// to share the same semaphore until the last lease is released.
/// </summary>
internal static class PathKeyedLock
{
    private static readonly ConcurrentDictionary<string, Entry> Entries = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static async ValueTask<Lease> AcquireAsync(string path, CancellationToken cancellationToken = default)
    {
        var entry = Rent(path);
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(entry);
        }
        catch
        {
            Return(entry, releaseSemaphore: false);
            throw;
        }
    }

    public static Lease Acquire(string path)
    {
        var entry = Rent(path);
        try
        {
            entry.Semaphore.Wait();
            return new Lease(entry);
        }
        catch
        {
            Return(entry, releaseSemaphore: false);
            throw;
        }
    }

    private static Entry Rent(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var key = Path.GetFullPath(path);
        while (true)
        {
            var entry = Entries.GetOrAdd(key, static value => new Entry(value));
            lock (entry.Sync)
            {
                if (entry.Removed)
                    continue;
                entry.References++;
                return entry;
            }
        }
    }

    private static void Return(Entry entry, bool releaseSemaphore)
    {
        lock (entry.Sync)
        {
            if (releaseSemaphore)
                entry.Semaphore.Release();

            entry.References--;
            if (entry.References != 0)
                return;

            entry.Removed = true;
            Entries.TryRemove(new KeyValuePair<string, Entry>(entry.Key, entry));
        }
    }

    internal sealed class Entry(string key)
    {
        public string Key { get; } = key;
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public object Sync { get; } = new();
        public int References { get; set; }
        public bool Removed { get; set; }
    }

    internal sealed class Lease : IDisposable
    {
        private Entry? _entry;

        internal Lease(Entry entry) => _entry = entry;

        public void Dispose()
        {
            var entry = Interlocked.Exchange(ref _entry, null);
            if (entry is not null)
                Return(entry, releaseSemaphore: true);
        }
    }
}
