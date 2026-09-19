using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class PersistedStoreRegression
{
    internal static async Task RunAsync()
    {
        await TestSettingsStoresAsync();
        await TestAccountStoreAsync();
        await TestServerStoreAsync();
        await TestStoreLockRejectsLinkedSidecarAsync();
        await TestServerStoreCrossProcessAsync();
    }

    private static async Task TestSettingsStoresAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-settings-store-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new NexoPathService(root);
            var launcher = new LauncherSettingsService(paths);
            var launcherPath = Path.Combine(root, "settings.json");
            var launcherDefaults = new LauncherSettings();

            var missingLauncher = await launcher.LoadAsync();
            Equal(
                launcherDefaults.DownloadSource,
                missingLauncher.DownloadSource,
                "missing launcher settings should use defaults");

            await launcher.SaveAsync(new LauncherSettings("bmclapi"));
            var storedLauncher = await launcher.LoadAsync();
            Equal("bmclapi", storedLauncher.DownloadSource,
                "valid launcher settings should load stored download source");

            await File.WriteAllTextAsync(
                launcherPath,
                "{\"downloadSource\":\"bmclapi\"");
            var malformedLauncher = await File.ReadAllBytesAsync(launcherPath);
            await ThrowsAsync<InvalidDataException>(
                () => launcher.LoadAsync(),
                "malformed launcher settings must fail explicitly");
            EqualBytes(
                malformedLauncher,
                await File.ReadAllBytesAsync(launcherPath),
                "failed launcher-settings load must preserve original bytes");

            await File.WriteAllTextAsync(launcherPath, "null");
            var nullLauncher = await File.ReadAllBytesAsync(launcherPath);
            await ThrowsAsync<InvalidDataException>(
                () => launcher.LoadAsync(),
                "null launcher settings root must fail explicitly");
            EqualBytes(
                nullLauncher,
                await File.ReadAllBytesAsync(launcherPath),
                "null launcher-settings load must preserve original bytes");

            var runtime = new LauncherRuntimeSettingsService(paths);
            var runtimePath = Path.Combine(root, "runtime-settings.json");
            var runtimeDefaults = new LauncherRuntimeSettings();

            var missingRuntime = await runtime.LoadAsync();
            Equal(
                runtimeDefaults.MemoryMb,
                missingRuntime.MemoryMb,
                "missing runtime settings should use default memory");
            Equal(
                runtimeDefaults.ExtraJvmArguments,
                missingRuntime.ExtraJvmArguments,
                "missing runtime settings should use default JVM options");

            await runtime.SaveAsync(
                new LauncherRuntimeSettings(2048, "-XX:+UseG1GC"));
            var storedRuntime = await runtime.LoadAsync();
            Equal(2048, storedRuntime.MemoryMb,
                "valid runtime settings should load stored memory");
            Equal("-XX:+UseG1GC", storedRuntime.ExtraJvmArguments,
                "valid runtime settings should load stored JVM options");

            await File.WriteAllTextAsync(
                runtimePath,
                "{\"memoryMb\":2048,\"extraJvmArguments\":");
            var malformedRuntime = await File.ReadAllBytesAsync(runtimePath);
            await ThrowsAsync<InvalidDataException>(
                () => runtime.LoadAsync(),
                "malformed runtime settings must fail explicitly");
            EqualBytes(
                malformedRuntime,
                await File.ReadAllBytesAsync(runtimePath),
                "failed runtime-settings load must preserve original bytes");

            await File.WriteAllTextAsync(
                runtimePath,
                "{\"memoryMb\":0,\"extraJvmArguments\":\"-Xmx8G\"}");
            var invalidRuntime = await File.ReadAllBytesAsync(runtimePath);
            await ThrowsAsync<InvalidDataException>(
                () => runtime.LoadAsync(),
                "semantically invalid runtime settings must fail explicitly");
            EqualBytes(
                invalidRuntime,
                await File.ReadAllBytesAsync(runtimePath),
                "invalid runtime-settings load must preserve original bytes");

            await File.WriteAllTextAsync(runtimePath, "null");
            var nullRuntime = await File.ReadAllBytesAsync(runtimePath);
            await ThrowsAsync<InvalidDataException>(
                () => runtime.LoadAsync(),
                "null runtime settings root must fail explicitly");
            EqualBytes(
                nullRuntime,
                await File.ReadAllBytesAsync(runtimePath),
                "null runtime-settings load must preserve original bytes");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task TestAccountStoreAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-account-store-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new NexoPathService(root);
            var store = new AccountStoreService(paths);
            var path = Path.Combine(root, "accounts.json");

            var empty = await store.GetAllAsync();
            Equal(0, empty.Count, "missing account store should be empty");

            var created = await store.CreateOfflineAsync("Player123");
            Equal("offline", created.Type, "offline account type");
            var validBytes = await File.ReadAllBytesAsync(path);
            var validJson = Encoding.UTF8.GetString(validBytes);
            Equal(1, (await store.GetAllAsync()).Count, "valid account should load");

            foreach (var fixture in new[]
            {
                "[null]",
                validJson.TrimEnd().TrimEnd(']') + ",null]",
                "[{\"id\":\"\",\"type\":\"offline\",\"displayName\":\"Player123\",\"uuid\":\"00000000-0000-0000-0000-000000000001\",\"createdAt\":\"2026-01-01T00:00:00Z\"}]",
                "[{\"id\":\"offline:player123\",\"type\":null,\"displayName\":\"Player123\",\"uuid\":\"00000000-0000-0000-0000-000000000001\",\"createdAt\":\"2026-01-01T00:00:00Z\"}]",
                "[{\"id\":\"offline:player123\",\"type\":\"offline\",\"displayName\":\"x\",\"uuid\":\"00000000-0000-0000-0000-000000000001\",\"createdAt\":\"2026-01-01T00:00:00Z\"}]",
                "[{\"id\":\"offline:player123\",\"type\":\"offline\",\"displayName\":\"Player123\",\"uuid\":\"not-a-uuid\",\"createdAt\":\"2026-01-01T00:00:00Z\"}]",
                "[{\"id\":\"other:player123\",\"type\":\"other\",\"displayName\":\"Player123\",\"uuid\":\"00000000-0000-0000-0000-000000000001\",\"createdAt\":\"2026-01-01T00:00:00Z\"}]"
            })
            {
                await File.WriteAllTextAsync(path, fixture);
                await ThrowsAsync<InvalidDataException>(
                    () => store.GetAllAsync(),
                    "semantic account corruption must fail as InvalidDataException");
                var before = await File.ReadAllBytesAsync(path);
                await ThrowsAsync<InvalidDataException>(
                    () => store.CreateOfflineAsync("Another123"),
                    "account mutation must reject semantic corruption");
                EqualBytes(before, await File.ReadAllBytesAsync(path),
                    "failed account mutation must preserve original bytes");
            }

            await File.WriteAllTextAsync(path, "{not-json");
            var malformedBefore = await File.ReadAllBytesAsync(path);
            await ThrowsAsync<InvalidDataException>(
                () => store.GetAllAsync(),
                "malformed account store must fail explicitly instead of appearing empty");
            EqualBytes(malformedBefore, await File.ReadAllBytesAsync(path),
                "failed account read must preserve malformed source bytes");
            await ThrowsAsync<InvalidDataException>(
                () => store.CreateOfflineAsync("Another123"),
                "account mutation should reject malformed JSON through the same store contract");
            EqualBytes(malformedBefore, await File.ReadAllBytesAsync(path),
                "failed account mutation must preserve malformed source bytes");

            await File.WriteAllTextAsync(path, "null");
            var nullBefore = await File.ReadAllBytesAsync(path);
            await ThrowsAsync<InvalidDataException>(
                () => store.GetAllAsync(),
                "null account-store root must fail explicitly");
            EqualBytes(nullBefore, await File.ReadAllBytesAsync(path),
                "null account-store read must preserve original bytes");

            await File.WriteAllBytesAsync(path, validBytes);
            Equal(1, (await store.GetAllAsync()).Count,
                "healthy account store should remain usable after corruption fixtures");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task TestServerStoreAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-server-store-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new NexoPathService(root);
            var store = new ServerStoreService(paths);
            var path = Path.Combine(root, "servers.json");

            var favorite = await store.AddAsync("Example", "example.com");
            Equal("example.com:25565", favorite.Address,
                "server add should canonicalize default port");
            var validBytes = await File.ReadAllBytesAsync(path);
            var validJson = Encoding.UTF8.GetString(validBytes);
            var loaded = await store.GetAllAsync();
            Equal(1, loaded.Count, "valid server favorite should load");
            Equal("example.com:25565", loaded[0].Address,
                "persisted server address should remain canonical");

            foreach (var fixture in new[]
            {
                "[null]",
                validJson.TrimEnd().TrimEnd(']') + ",null]",
                "[{\"id\":\"\",\"name\":\"Example\",\"address\":\"example.com\",\"createdAt\":\"2026-01-01T00:00:00Z\"}]",
                "[{\"id\":\"server-1\",\"name\":\"\",\"address\":\"example.com\",\"createdAt\":\"2026-01-01T00:00:00Z\"}]",
                "[{\"id\":\"server-1\",\"name\":\"Example\",\"address\":\"bad host:abc\",\"createdAt\":\"2026-01-01T00:00:00Z\"}]"
            })
            {
                await File.WriteAllTextAsync(path, fixture);
                await ThrowsAsync<InvalidDataException>(
                    () => store.GetAllAsync(),
                    "semantic server corruption must fail as InvalidDataException");

                foreach (var mutation in new Func<Task>[]
                {
                    async () => { _ = await store.AddAsync("Another", "another.example"); },
                    async () => { _ = await store.SetDefaultInstanceAsync("server-1", "instance-1"); },
                    () => store.RemoveAsync("server-1")
                })
                {
                    var before = await File.ReadAllBytesAsync(path);
                    await ThrowsAsync<InvalidDataException>(
                        mutation,
                        "server mutation must reject semantic corruption");
                    EqualBytes(before, await File.ReadAllBytesAsync(path),
                        "failed server mutation must preserve original bytes");
                }
            }

            await File.WriteAllTextAsync(path, "{not-json");
            var malformedBefore = await File.ReadAllBytesAsync(path);
            await ThrowsAsync<InvalidDataException>(
                () => store.GetAllAsync(),
                "malformed server store must fail explicitly instead of appearing empty");
            EqualBytes(malformedBefore, await File.ReadAllBytesAsync(path),
                "failed server read must preserve malformed source bytes");
            await ThrowsAsync<InvalidDataException>(
                async () => { _ = await store.AddAsync("Another", "another.example"); },
                "server mutation should reject malformed JSON through the same store contract");
            EqualBytes(malformedBefore, await File.ReadAllBytesAsync(path),
                "failed server mutation must preserve malformed source bytes");

            await File.WriteAllTextAsync(path, "null");
            var nullBefore = await File.ReadAllBytesAsync(path);
            await ThrowsAsync<InvalidDataException>(
                () => store.GetAllAsync(),
                "null server-store root must fail explicitly");
            EqualBytes(nullBefore, await File.ReadAllBytesAsync(path),
                "null server-store read must preserve original bytes");

            await File.WriteAllBytesAsync(path, validBytes);
            Equal(1, (await store.GetAllAsync()).Count,
                "healthy server store should remain usable after corruption fixtures");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task TestStoreLockRejectsLinkedSidecarAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-store-lock-link-tests",
            Guid.NewGuid().ToString("N"));
        var outsideRoot = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-store-lock-link-targets",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outsideRoot);

        try
        {
            var storePath = Path.Combine(root, "servers.json");
            var lockPath = storePath + ".lock";
            var outsidePath = Path.Combine(outsideRoot, "outside.txt");
            var marker = Encoding.UTF8.GetBytes(
                "outside-lock-target-must-remain-unchanged");
            await File.WriteAllBytesAsync(outsidePath, marker);

            try
            {
                File.CreateSymbolicLink(lockPath, outsidePath);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException
                or PlatformNotSupportedException
                or IOException)
            {
                Console.WriteLine(
                    "SKIP persisted-store linked lock fixture: "
                    + ex.GetType().Name);
                return;
            }

            await ThrowsAsync<UnauthorizedAccessException>(
                async () =>
                {
                    await using var lease =
                        await PersistedStoreMutationLock.AcquireAsync(
                            storePath);
                },
                "linked persisted-store lock sidecar must be rejected");

            EqualBytes(
                marker,
                await File.ReadAllBytesAsync(outsidePath),
                "rejected persisted-store lock sidecar must not modify its target");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(outsideRoot, recursive: true); } catch { }
        }
    }

    internal static async Task<int> RunServerStoreAddHelperAsync(string[] args)
    {
        if (args.Length != 6)
            return 2;

        var root = args[1];
        var name = args[2];
        var address = args[3];
        var readyPath = args[4];
        var releasePath = args[5];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await File.WriteAllTextAsync(readyPath, "ready", timeout.Token);
        while (!File.Exists(releasePath))
            await Task.Delay(25, timeout.Token);

        var store = new ServerStoreService(new NexoPathService(root));
        await store.AddAsync(name, address, timeout.Token);
        return 0;
    }

    internal static async Task<int> RunStoreLockHolderHelperAsync(string[] args)
    {
        if (args.Length != 3)
            return 2;

        var root = args[1];
        var readyPath = args[2];
        var storePath = Path.Combine(root, "servers.json");

        await using var lease = await PersistedStoreMutationLock.AcquireAsync(storePath);
        await File.WriteAllTextAsync(readyPath, "ready");
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static async Task TestServerStoreCrossProcessAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-server-store-process-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var helpers = new List<Process>();
        try
        {
            var paths = new NexoPathService(root);
            paths.EnsureDirectories();
            var store = new ServerStoreService(paths);
            var storePath = Path.Combine(root, "servers.json");

            // A mutation in another process must remain blocked until the complete
            // read-modify-write transaction owns the store lease.
            var blockedReady = Path.Combine(root, "blocked.ready");
            var blockedGo = Path.Combine(root, "blocked.go");
            await using (var held = await PersistedStoreMutationLock.AcquireAsync(storePath))
            {
                var blocked = StartHelper(
                    "--server-store-add",
                    root,
                    "Blocked",
                    "blocked.example",
                    blockedReady,
                    blockedGo);
                helpers.Add(blocked);
                using var blockedTimeout =
                    new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await WaitForReadyAsync(blocked, blockedReady, blockedTimeout.Token);
                await File.WriteAllTextAsync(blockedGo, "go", blockedTimeout.Token);
                await Task.Delay(200, blockedTimeout.Token);

                Equal(false, blocked.HasExited,
                    "cross-process server mutation must wait for the persisted-store lease");
                Equal(false, File.Exists(storePath),
                    "blocked store mutation must not publish before acquiring the lease");
            }

            await WaitForSuccessAsync(helpers[0], TimeSpan.FromSeconds(10));

            // Cancellation while queued must leave the store byte-for-byte unchanged.
            var stableBytes = await File.ReadAllBytesAsync(storePath);
            await using (var held = await PersistedStoreMutationLock.AcquireAsync(storePath))
            {
                using var cancelled =
                    new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
                await ThrowsAsync<OperationCanceledException>(
                    async () =>
                    {
                        _ = await store.AddAsync(
                            "Cancelled",
                            "cancelled.example",
                            cancelled.Token);
                    },
                    "cancelled store mutation should stop while waiting for the lease");
            }
            EqualBytes(stableBytes, await File.ReadAllBytesAsync(storePath),
                "cancelled store mutation must not alter the persisted store");

            // Two separate Nexo processes released at the same barrier must preserve
            // both additions rather than publishing two snapshots from the same old state.
            var readyA = Path.Combine(root, "a.ready");
            var readyB = Path.Combine(root, "b.ready");
            var go = Path.Combine(root, "concurrent.go");
            var helperA = StartHelper(
                "--server-store-add",
                root,
                "Concurrent A",
                "a.example",
                readyA,
                go);
            var helperB = StartHelper(
                "--server-store-add",
                root,
                "Concurrent B",
                "b.example",
                readyB,
                go);
            helpers.Add(helperA);
            helpers.Add(helperB);

            using (var concurrentTimeout =
                   new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await Task.WhenAll(
                    WaitForReadyAsync(helperA, readyA, concurrentTimeout.Token),
                    WaitForReadyAsync(helperB, readyB, concurrentTimeout.Token));
                await File.WriteAllTextAsync(go, "go", concurrentTimeout.Token);
                await Task.WhenAll(
                    WaitForSuccessAsync(helperA, TimeSpan.FromSeconds(10)),
                    WaitForSuccessAsync(helperB, TimeSpan.FromSeconds(10)));
            }

            var concurrentItems = await store.GetAllAsync();
            Equal(true, concurrentItems.Any(item => item.Address == "a.example:25565"),
                "cross-process add A should be preserved");
            Equal(true, concurrentItems.Any(item => item.Address == "b.example:25565"),
                "cross-process add B should be preserved");

            // The lock file may remain, but killing its owner must release the OS handle.
            var crashReady = Path.Combine(root, "crash.ready");
            var crashHolder = StartHelper(
                "--hold-store-lock",
                root,
                crashReady);
            helpers.Add(crashHolder);
            using (var crashTimeout =
                   new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await WaitForReadyAsync(crashHolder, crashReady, crashTimeout.Token);
                crashHolder.Kill(entireProcessTree: true);
                await crashHolder.WaitForExitAsync(crashTimeout.Token);

                _ = await store.AddAsync(
                    "After crash",
                    "after-crash.example",
                    crashTimeout.Token);
            }

            Equal(true, (await store.GetAllAsync()).Any(
                    item => item.Address == "after-crash.example:25565"),
                "a crashed lock owner must not strand the persisted store");
        }
        finally
        {
            foreach (var helper in helpers)
            {
                try
                {
                    if (!helper.HasExited)
                        helper.Kill(entireProcessTree: true);
                }
                catch
                {
                }
                helper.Dispose();
            }

            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static Process StartHelper(params string[] arguments)
    {
        var processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the current test host.");
        var entryAssembly = Assembly.GetEntryAssembly()?.Location
            ?? throw new InvalidOperationException("Could not determine the core test assembly.");

        var start = new ProcessStartInfo(processPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (Path.GetFileNameWithoutExtension(processPath)
            .Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(entryAssembly);

        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        return Process.Start(start)
            ?? throw new InvalidOperationException("Could not start persisted-store helper process.");
    }

    private static async Task WaitForReadyAsync(
        Process process,
        string readyPath,
        CancellationToken cancellationToken)
    {
        while (!File.Exists(readyPath))
        {
            if (process.HasExited)
            {
                var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
                throw new InvalidOperationException(
                    $"Persisted-store helper exited with {process.ExitCode} before becoming ready: {stderr}");
            }

            await Task.Delay(25, cancellationToken);
        }
    }

    private static async Task WaitForSuccessAsync(
        Process process,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        await process.WaitForExitAsync(cancellation.Token);
        if (process.ExitCode == 0)
            return;

        var stderr = await process.StandardError.ReadToEndAsync(cancellation.Token);
        throw new InvalidOperationException(
            $"Persisted-store helper exited with {process.ExitCode}: {stderr}");
    }

    private static async Task ThrowsAsync<TException>(
        Func<Task> action,
        string message)
        where TException : Exception
    {
        try
        {
            await action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{message}: expected {typeof(TException).Name}.");
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException(
                $"{message}: expected '{expected}', actual '{actual}'.");
    }

    private static void EqualBytes(byte[] expected, byte[] actual, string message)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
            throw new InvalidOperationException(message);
    }
}
