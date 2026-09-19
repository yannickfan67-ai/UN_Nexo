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

    public async ValueTask<Lease> AcquireAsync(
        string instanceId,
        string operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        cancellationToken.ThrowIfCancellationRequested();

        var instanceRoot = Path.GetFullPath(
            _paths.GetInstanceDirectory(instanceId));
        var current = CurrentScope.Value;
        if (current is not null
            && current.Contains(instanceRoot))
        {
            var nested = new Scope(
                instanceRoot,
                current);
            CurrentScope.Value = nested;
            return new Lease(
                nested,
                previous: current,
                keyedLease: null,
                crossProcessLock: null);
        }

        Directory.CreateDirectory(instanceRoot);
        var keyedLease = await PathKeyedLock.AcquireAsync(
            instanceRoot,
            cancellationToken);

        FileStream? crossProcessLock = null;
        try
        {
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

            var previous = CurrentScope.Value;
            var scope = new Scope(
                instanceRoot,
                previous);
            CurrentScope.Value = scope;
            return new Lease(
                scope,
                previous,
                keyedLease,
                crossProcessLock);
        }
        catch
        {
            crossProcessLock?.Dispose();
            keyedLease.Dispose();
            throw;
        }
    }

    private sealed class Scope(
        string key,
        Scope? parent)
    {
        public string Key { get; } = key;
        public Scope? Parent { get; } = parent;

        public bool Contains(string candidate)
        {
            for (Scope? scope = this;
                 scope is not null;
                 scope = scope.Parent)
            {
                if (PathEquals(
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

            if (!ReferenceEquals(
                    CurrentScope.Value,
                    _scope))
                throw new InvalidOperationException(
                    "Instance operation leases must be disposed in nested order.");

            CurrentScope.Value = _previous;

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
