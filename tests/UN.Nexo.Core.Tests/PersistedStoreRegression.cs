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
