using System.Text;

namespace UN.Nexo.Core.Services;

/// <summary>
/// Coordinates mutations of one managed instance across launcher services.
/// The process-local key prevents two service objects from mutating the same
/// instance concurrently, while the held file handle detects another UN_Nexo
/// process. Nested operations in the same async flow are re-entrant so
/// Fabric/Repair/Play can call lower-level preparation safely.
/// </summary>
public sealed class InstanceOperationCoordinator
{
    public const string LockFileName = ".nexo-operation.lock";

    private static readonly AsyncLocal<Scope?> CurrentScope = new();
    private readonly NexoPathService _paths;

    public InstanceOperationCoordinator(NexoPathService paths)
        => _paths = paths;

    public ValueTask<Lease> AcquireAsync(
        string instanceId,
        string operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        cancellationToken.ThrowIfCancellationRequested();

        var instanceRoot = Path.GetFullPath(
            _paths.GetInstanceDirectory(instanceId));
        var current = PruneInactive(
            CurrentScope.Value);
        if (!ReferenceEquals(
                current,
                CurrentScope.Value))
            CurrentScope.Value = current;

        if (current is not null
            && current.Contains(instanceRoot))
        {
            var nested = new Scope(
                instanceRoot,
                current,
                active: true);
            CurrentScope.Value = nested;
            return ValueTask.FromResult(
                new Lease(
                    nested,
                    current,
                    keyedLease: null,
                    crossProcessLock: null));
        }

        // Set the ambient scope synchronously, before the first await.
        // AsyncLocal changes made only after an await would not reliably
        // propagate back into the caller's captured execution context.
        var pending = new Scope(
            instanceRoot,
            current,
            active: false);
        CurrentScope.Value = pending;
        return new ValueTask<Lease>(
            AcquireOuterAsync(
                instanceId,
                operation,
                instanceRoot,
                pending,
                current,
                cancellationToken));
    }

    private static async Task<Lease> AcquireOuterAsync(
        string instanceId,
        string operation,
        string instanceRoot,
        Scope pending,
        Scope? previous,
        CancellationToken cancellationToken)
    {
        PathKeyedLock.Lease? keyedLease = null;
        FileStream? crossProcessLock = null;
        try
        {
            Directory.CreateDirectory(instanceRoot);
            keyedLease = await PathKeyedLock.AcquireAsync(
                instanceRoot,
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            var lockPath = Path.Combine(
                instanceRoot,
                LockFileName);
            try
            {
                crossProcessLock = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    4096,
                    FileOptions.WriteThrough);
            }
            catch (IOException ex)
            {
                throw new InvalidOperationException(
                    $"Instance '{instanceId}' is busy in another UN_Nexo process. "
                    + $"Finish that operation before starting '{operation}'.",
                    ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new InvalidOperationException(
                    $"UN_Nexo could not acquire the instance operation lock for '{instanceId}'.",
                    ex);
            }

            var marker = Encoding.UTF8.GetBytes(
                $"pid={Environment.ProcessId}{Environment.NewLine}"
                + $"operation={operation}{Environment.NewLine}"
                + $"started={DateTimeOffset.UtcNow:O}{Environment.NewLine}");
            crossProcessLock.SetLength(0);
            await crossProcessLock.WriteAsync(
                marker,
                cancellationToken);
            await crossProcessLock.FlushAsync(
                cancellationToken);
            crossProcessLock.Position = 0;

            pending.Active = true;
            return new Lease(
                pending,
                previous,
                keyedLease,
                crossProcessLock);
        }
        catch
        {
            pending.Active = false;
            crossProcessLock?.Dispose();
            keyedLease?.Dispose();
            throw;
        }
    }

    private static Scope? PruneInactive(
        Scope? scope)
    {
        while (scope is not null
               && !scope.Active)
            scope = scope.Parent;
        return scope;
    }

    private sealed class Scope(
        string key,
        Scope? parent,
        bool active)
    {
        public string Key { get; } = key;
        public Scope? Parent { get; } = parent;
        public bool Active { get; set; } = active;

        public bool Contains(string candidate)
        {
            for (Scope? scope = this;
                 scope is not null;
                 scope = scope.Parent)
            {
                if (scope.Active
                    && PathEquals(
                        scope.Key,
                        candidate))
                    return true;
            }

            return false;
        }
    }

    private static bool PathEquals(
        string left,
        string right)
        => string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    public sealed class Lease : IDisposable
    {
        private readonly Scope _scope;
        private readonly Scope? _previous;
        private PathKeyedLock.Lease? _keyedLease;
        private FileStream? _crossProcessLock;
        private int _disposed;

        private Lease(
            Scope scope,
            Scope? previous,
            PathKeyedLock.Lease? keyedLease,
            FileStream? crossProcessLock)
        {
            _scope = scope;
            _previous = previous;
            _keyedLease = keyedLease;
            _crossProcessLock = crossProcessLock;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(
                    ref _disposed,
                    1) != 0)
                return;

            var current = PruneInactive(
                CurrentScope.Value);
            if (!ReferenceEquals(
                    current,
                    _scope))
                throw new InvalidOperationException(
                    "Instance operation leases must be disposed in nested order.");

            _scope.Active = false;
            CurrentScope.Value =
                PruneInactive(_previous);

            var file = Interlocked.Exchange(
                ref _crossProcessLock,
                null);
            var keyed = Interlocked.Exchange(
                ref _keyedLease,
                null);

            try
            {
                file?.Dispose();
            }
            finally
            {
                keyed?.Dispose();
            }
        }
    }
}
