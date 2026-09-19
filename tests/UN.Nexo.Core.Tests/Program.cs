using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Process failure cleanup (Unix)", TestProcessFailureCleanupAsync),
            ("Instance-scoped process concurrency (Unix)", ProcessConcurrencyRegression.RunAsync),
            ("Launch log bounds and retention", LaunchLogPolicyRegression.RunAsync),
            ("Java major parsing", TestJavaMajorAsync),
            ("Java discovery process cleanup", JavaDiscoveryRegression.RunAsync),
            ("Managed Java runtime acquisition", TestManagedJavaRuntimeAsync),
            ("Managed Java concurrent provisioning", ManagedJavaConcurrencyRegression.RunAsync),
            ("Managed Java metadata hardening", TestManagedJavaMetadataHardeningAsync),
            ("Managed Java atomic publication", ManagedRuntimePublicationRegression.RunAsync),
            ("Managed Java archive containment", ManagedRuntimeArchiveRegression.RunAsync),
            ("Modrinth provider integration", ModrinthProviderRegression.RunAsync),
            ("Microsoft account authentication", MicrosoftAuthRegression.RunAsync),
            ("Persisted account and server store validation", PersistedStoreRegression.RunAsync),
            ("Instance name uniqueness", InstanceNameUniquenessRegression.RunAsync),
            ("Asset-index id path containment", TestAssetIndexIdContainmentAsync),
            ("Asset-index schema and hash validation", TestAssetIndexSchemaAndHashValidationAsync),
            ("Vanilla metadata path containment", VanillaPathContainmentRegression.RunAsync),
            ("Vanilla required metadata contract", VanillaMetadataContractRegression.RunAsync),
            ("Atomic install-state publication", InstallStateAtomicRegression.RunAsync),
            ("Native extraction containment", NativeExtractionRegression.RunAsync),
            ("Runtime memory and JVM arguments", TestRuntimeLaunchOptionsAsync),
            ("Launch metadata shape validation", LaunchMetadataRegression.RunAsync),
            ("Launch artifact and derived asset integrity", LaunchIntegrityRegression.RunAsync),
            ("Version metadata resolver typing", VersionMetadataResolverRegression.RunAsync),
            ("Rule features and ARM64 compatibility", RuleArchitectureRegression.RunAsync),
            ("Server address parsing", TestServerAddressParsingAsync),
            ("Server favorite instance resolution", ServerFavoriteInstanceRegression.RunAsync),
            ("Server decorator inherited metadata", TestServerDecoratorInheritedMetadataAsync),
            ("Manifest streaming fallback", TestManifestStreamingFallbackAsync),
            ("Version catalog schema and stall fallback", VersionCatalogRegression.RunAsync),
            ("Metadata response size bounds", MetadataSizeRegression.RunAsync),
            ("Trusted download target policy", NetworkTargetRegression.RunAsync),
            ("Modern 1.21.4 launch plan and Quick Play", () => TestLaunchPlanAsync("1.21.4", 21, modern: true)),
            ("Legacy 1.8.9 launch plan and direct connect", () => TestLaunchPlanAsync("1.8.9", 8, modern: false)),
            ("Legacy 1.5.2 launch plan and virtual assets", TestMinecraft152LaunchPlanAsync)
        };

        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                await test.Run();
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {ex}");
            }
        }

        Console.WriteLine($"Regression checks: {tests.Length - failures}/{tests.Length} passed");
        return failures == 0 ? 0 : 1;
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    private static async Task TestProcessFailureCleanupAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP process fixture requires a Unix shell");
            return;
        }

        var temp = Path.Combine(Path.GetTempPath(), "nexo-process-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var oldInterval = LaunchDiagnostics.HeartbeatInterval;
        var oldEnabled = LaunchDiagnostics.Enabled;
        try
        {
            LaunchDiagnostics.Enabled = true;
            LaunchDiagnostics.HeartbeatInterval = TimeSpan.FromMilliseconds(20);
            var service = new MinecraftProcessService(
                new LauncherRuntimeSettingsService(new NexoPathService(temp)));
            var executable = Path.Combine(temp, "fake-java");
            var plan = new MinecraftLaunchPlan(executable, temp, [], Path.Combine(temp, "logs"));

            foreach (var failure in new[] { "started", "stdout", "heartbeat" })
            {
                await File.WriteAllTextAsync(executable, failure == "stdout"
                    ? "#!/bin/sh\nwhile :; do echo output; done\n"
                    : "#!/bin/sh\nwhile :; do sleep 1; done\n");
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var pid = 0;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var progress = new InlineProgress(message =>
                {
                    if (message.StartsWith("Started game process ", StringComparison.Ordinal))
                    {
                        pid = int.Parse(message["Started game process ".Length..]);
                        if (failure == "started") throw new IOException("injected consumer failure");
                    }
                    if ((failure == "stdout" && message.StartsWith("[stdout]", StringComparison.Ordinal))
                        || (failure == "heartbeat" && message.StartsWith("[debug] PID ", StringComparison.Ordinal)))
                        throw new IOException("injected consumer failure");
                });
                try
                {
                    try
                    {
                        await service.RunAsync(plan, progress, timeout.Token);
                        throw new Exception("Expected the consumer failure to propagate");
                    }
                    catch (IOException ex) when (ex.Message == "injected consumer failure") { }
                    Equal(false, timeout.IsCancellationRequested, failure + " must terminate without timeout cancellation");
                    Equal(true, pid > 0, "fixture must start a child process");
                    try
                    {
                        using var child = System.Diagnostics.Process.GetProcessById(pid);
                        Equal(true, child.HasExited, failure + " must not leave Java running");
                    }
                    catch (ArgumentException) { /* Reaped process no longer exists. */ }
                }
                finally
                {
                    if (pid > 0)
                    {
                        try
                        {
                            using var child = System.Diagnostics.Process.GetProcessById(pid);
                            if (!child.HasExited) child.Kill(entireProcessTree: true);
                        }
                        catch (ArgumentException) { }
                    }
                }
            }

            // Reuse the same service after all failures; its running guard must reset.
            await File.WriteAllTextAsync(executable, "#!/bin/sh\necho normal-output\necho normal-error >&2\nexit 7\n");
            var result = await service.RunAsync(plan);
            Equal(7, result.ExitCode, "normal exit code");
            var log = await File.ReadAllTextAsync(result.LogPath);
            ContainsText(log, "[stdout] normal-output", "stdout retained");
            ContainsText(log, "[stderr] normal-error", "stderr retained");
        }
        finally
        {
            LaunchDiagnostics.HeartbeatInterval = oldInterval;
            LaunchDiagnostics.Enabled = oldEnabled;
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static Task TestJavaMajorAsync()
    {
        Equal(8, MinecraftLaunchPlanBuilder.JavaMajor("1.8.0_442"), "Java 8 parsing");
        Equal(8, MinecraftLaunchPlanBuilder.JavaMajor("8.0.442+6"), "Managed Java 8 parsing");
        Equal(17, MinecraftLaunchPlanBuilder.JavaMajor("17.0.13"), "Java 17 parsing");
        Equal(21, MinecraftLaunchPlanBuilder.JavaMajor("21.0.8+9"), "Java 21 parsing");
        return Task.CompletedTask;
    }

    private static async Task TestManagedJavaRuntimeAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), "UN Nexo managed Java", Guid.NewGuid().ToString("N"));
        try
        {
            var archiveBytes = CreateFakeJavaArchive();
            var checksum = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
            var handler = new ManagedJavaHandler(archiveBytes, checksum);
            using var client = new HttpClient(handler);
            var paths = new NexoPathService(temp);
            var service = new JavaRuntimeProvisionService(client, paths);

            var installation = await service.EnsureJavaAsync(8);
            Equal(8, MinecraftLaunchPlanBuilder.JavaMajor(installation.Version), "managed Java major");
            Equal(true, installation.Is64Bit, "managed Java architecture");
            Equal(true, File.Exists(installation.JavaPath), "managed Java executable should exist");
            ContainsText(installation.Source, "Temurin", "managed Java source label");
            Equal(2, handler.RequestCount, "first provision should resolve metadata and download one archive");

            var reused = await service.EnsureJavaAsync(8);
            Equal(installation.JavaPath, reused.JavaPath, "managed runtime should be reused");
            Equal(2, handler.RequestCount, "reusing a managed runtime should not access the network");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static async Task TestManagedJavaMetadataHardeningAsync()
    {
        const int metadataLimit = 1024 * 1024;
        var archiveBytes = CreateFakeJavaArchive();
        var checksum = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        var validLegacy =
            "[{\"binary\":{\"package\":{\"link\":\"https://runtime.example.test/temurin8.zip\",\"checksum\":\""
            + checksum
            + "\"}},\"version_data\":{\"semver\":\"8.0.442+6\"}}]";

        var malformed = new (string Label, string Json)[]
        {
            (
                "numeric package link",
                "[{\"binary\":{\"package\":{\"link\":123,\"checksum\":\"" + checksum + "\"}}}]"
            ),
            (
                "object package checksum",
                "[{\"binary\":{\"package\":{\"link\":\"https://runtime.example.test/temurin8.zip\",\"checksum\":{}}}}]"
            ),
            (
                "array version_data",
                "[{\"binary\":{\"package\":{\"link\":\"https://runtime.example.test/temurin8.zip\",\"checksum\":\"" + checksum + "\"}},\"version_data\":[]}]"
            ),
            (
                "numeric semver",
                "[{\"binary\":{\"package\":{\"link\":\"https://runtime.example.test/temurin8.zip\",\"checksum\":\"" + checksum + "\"}},\"version_data\":{\"semver\":21}}]"
            )
        };

        foreach (var fixture in malformed)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "un-nexo-adoptium-shape-tests",
                Guid.NewGuid().ToString("N"));
            try
            {
                var handler = new ManagedJavaHandler(archiveBytes, checksum, fixture.Json);
                using var client = new HttpClient(handler);
                var service = new JavaRuntimeProvisionService(client, new NexoPathService(root));
                try
                {
                    await service.EnsureJavaAsync(8);
                    throw new Exception($"Malformed Adoptium fixture '{fixture.Label}' should be rejected.");
                }
                catch (InvalidDataException)
                {
                }

                Equal(1, handler.RequestCount,
                    $"Malformed Adoptium fixture '{fixture.Label}' must fail before package download.");
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        var binariesRoot = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-adoptium-binaries-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var binariesJson =
                "[{\"binaries\":[{\"package\":{\"link\":\"https://runtime.example.test/temurin8.zip\",\"checksum\":\""
                + checksum
                + "\"}}],\"version_data\":{\"semver\":\"8.0.442+6\"}}]";
            var handler = new ManagedJavaHandler(archiveBytes, checksum, binariesJson);
            using var client = new HttpClient(handler);
            var installation = await new JavaRuntimeProvisionService(
                client,
                new NexoPathService(binariesRoot)).EnsureJavaAsync(8);
            Equal(8, MinecraftLaunchPlanBuilder.JavaMajor(installation.Version),
                "Adoptium binaries[] metadata should remain supported.");
            Equal(2, handler.RequestCount,
                "Valid binaries[] metadata should proceed to one package download.");
        }
        finally
        {
            try { Directory.Delete(binariesRoot, recursive: true); } catch { }
        }

        var declaredRoot = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-adoptium-size-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var oversized = new DeclaredLengthContent(metadataLimit + 1);
            using var client = new HttpClient(new AdoptiumContentHandler(oversized));
            var service = new JavaRuntimeProvisionService(client, new NexoPathService(declaredRoot));
            try
            {
                await service.EnsureJavaAsync(8);
                throw new Exception("Declared oversized Adoptium metadata should be rejected.");
            }
            catch (InvalidDataException)
            {
            }

            Equal(false, oversized.ReadAttempted,
                "Declared oversized metadata must be rejected before the response body is read.");
        }
        finally
        {
            try { Directory.Delete(declaredRoot, recursive: true); } catch { }
        }

        var streamedRoot = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-adoptium-stream-size-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var content = new StreamContent(new MemoryStream(new byte[metadataLimit + 1]));
            using var client = new HttpClient(new AdoptiumContentHandler(content));
            var service = new JavaRuntimeProvisionService(client, new NexoPathService(streamedRoot));
            try
            {
                await service.EnsureJavaAsync(8);
                throw new Exception("Unknown-length oversized Adoptium metadata should be rejected.");
            }
            catch (InvalidDataException)
            {
            }
        }
        finally
        {
            try { Directory.Delete(streamedRoot, recursive: true); } catch { }
        }

        var nearLimitRoot = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-adoptium-near-limit-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var nearLimitJson = validLegacy.PadRight(metadataLimit - 64, ' ');
            var handler = new ManagedJavaHandler(archiveBytes, checksum, nearLimitJson);
            using var client = new HttpClient(handler);
            var installation = await new JavaRuntimeProvisionService(
                client,
                new NexoPathService(nearLimitRoot)).EnsureJavaAsync(8);
            Equal(8, MinecraftLaunchPlanBuilder.JavaMajor(installation.Version),
                "Just-under-limit Adoptium metadata should remain accepted.");
            Equal(2, handler.RequestCount,
                "Just-under-limit metadata should proceed to package download.");
        }
        finally
        {
            try { Directory.Delete(nearLimitRoot, recursive: true); } catch { }
        }
    }

    private static byte[] CreateFakeJavaArchive()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            var executableName = OperatingSystem.IsWindows() ? "java.exe" : "java";
            var entry = archive.CreateEntry($"jdk8/bin/{executableName}");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("fake managed java");
        }
        return memory.ToArray();
    }

    private static Task TestRuntimeLaunchOptionsAsync()
    {
        const long gib = 1024L * 1024L * 1024L;
        Equal(4096, RuntimeLaunchOptions.RecommendMemoryMb(16 * gib), "Auto memory recommendation");

        var baseArguments = new[]
        {
            "-Xmx2G",
            "-Djava.library.path=/tmp/natives",
            "net.minecraft.client.main.Main",
            "--username",
            "NexoTester"
        };
        var settings = new LauncherRuntimeSettings(
            0,
            "-XX:+UseG1GC \"-Dgreeting=hello world\"");
        var applied = RuntimeLaunchOptions.Apply(baseArguments, settings, 16 * gib);
        Contains(applied, "-Xmx4096M", "Auto memory should replace the fixed launcher value");
        DoesNotContain(applied, "-Xmx2G", "Old fixed memory must be removed");
        Contains(applied, "-XX:+UseG1GC", "Extra JVM option");
        Contains(applied, "-Dgreeting=hello world", "Quoted JVM option should remain one argument");
        Contains(applied, "net.minecraft.client.main.Main", "Main class must remain in the launch plan");

        var custom = RuntimeLaunchOptions.Apply(
            baseArguments,
            new LauncherRuntimeSettings(6144, "-Xms1024M"),
            4 * gib);
        Contains(custom, "-Xmx6144M", "Custom memory setting");
        Contains(custom, "-Xms1024M", "Custom minimum heap");

        Throws<ArgumentException>(
            () => RuntimeLaunchOptions.ParseExtraJvmArguments("-Xmx8G"),
            "Manual -Xmx should be blocked");
        Throws<ArgumentException>(
            () => RuntimeLaunchOptions.ParseExtraJvmArguments("-cp hacked.jar"),
            "Classpath override should be blocked");
        foreach (var option in new[]
        {
            "@options.txt", "\"@options with spaces.txt\"",
            "--class-path=hacked.jar", "-Djava.class.path=hacked.jar",
            "-XX:MaxHeapSize=8G", "-XX:Flags=options.txt",
            "-XX:VMOptionsFile=options.txt", "--module=other/main", "-m"
        })
        {
            Throws<ArgumentException>(
                () => RuntimeLaunchOptions.ParseExtraJvmArguments(option),
                $"Launcher-managed option bypass must be rejected: {option}");
        }
        Contains(RuntimeLaunchOptions.ParseExtraJvmArguments(
            "--add-opens=java.base/java.lang=ALL-UNNAMED -Dexample.class.path=custom"),
            "--add-opens=java.base/java.lang=ALL-UNNAMED", "Valid equals-form JVM option");
        return Task.CompletedTask;
    }

    private static Task TestServerAddressParsingAsync()
    {
        var normal = MinecraftServerTarget.Parse("play.example.net:25566");
        Equal("play.example.net", normal.Host, "hostname parsing");
        Equal(25566, normal.Port, "custom port parsing");
        Equal("play.example.net:25566", normal.Authority, "authority formatting");

        var defaultPort = MinecraftServerTarget.Parse("localhost");
        Equal(25565, defaultPort.Port, "default Minecraft port");

        var ipv6 = MinecraftServerTarget.Parse("[2001:db8::1]:25570");
        Equal("2001:db8::1", ipv6.Host, "IPv6 host parsing");
        Equal("[2001:db8::1]:25570", ipv6.Authority, "IPv6 authority formatting");
        return Task.CompletedTask;
    }

    private static async Task TestAssetIndexIdContainmentAsync()
    {
        var unsafeIds = new[]
        {
            "../escaped",
            "a/b",
            @"a\b",
            ".",
            "..",
            "/rooted",
            @"C:\escaped",
            "name:stream"
        };

        foreach (var assetId in unsafeIds)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "un-nexo-asset-id-tests",
                Guid.NewGuid().ToString("N"));
            try
            {
                var paths = new NexoPathService(root);
                var handler = new AssetIndexIdHandler(assetId);
                using var client = new HttpClient(handler);
                var installer = new MinecraftVanillaInstallService(
                    client,
                    paths,
                    new DownloadSourceService(),
                    TimeSpan.FromSeconds(2));
                var instance = new GameInstance(
                    Guid.NewGuid().ToString("N"),
                    "Asset id test",
                    "asset-id-test",
                    "vanilla",
                    DateTimeOffset.UtcNow);
                var version = new MinecraftVersionInfo(
                    "asset-id-test",
                    "release",
                    "https://piston-meta.mojang.com/version.json",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    string.Empty,
                    0);

                try
                {
                    await installer.InstallAsync(instance, version);
                    throw new Exception($"Unsafe assetIndex.id '{assetId}' should be rejected.");
                }
                catch (InvalidDataException ex)
                {
                    ContainsText(ex.Message, "assetIndex.id", "unsafe asset id rejection");
                }

                Equal(0, handler.AssetIndexRequests,
                    $"unsafe assetIndex.id '{assetId}' must be rejected before the asset-index HTTP request");
                var assetsRoot = Path.Combine(paths.GetInstanceGameDirectory(instance.Id), "assets");
                Equal(false, File.Exists(Path.Combine(assetsRoot, "escaped.json")),
                    "traversal asset id must not publish an escaped index file");
                Equal(false, File.Exists(Path.Combine(assetsRoot, "escaped.json.part")),
                    "traversal asset id must not publish an escaped temporary index file");
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        var validRoot = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-asset-id-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new NexoPathService(validRoot);
            var handler = new AssetIndexIdHandler("1.21");
            using var client = new HttpClient(handler);
            var installer = new MinecraftVanillaInstallService(
                client,
                paths,
                new DownloadSourceService(),
                TimeSpan.FromSeconds(2));
            var instance = new GameInstance(
                    Guid.NewGuid().ToString("N"),
                "Asset id valid",
                "asset-id-test",
                "vanilla",
                DateTimeOffset.UtcNow);
            var version = new MinecraftVersionInfo(
                "asset-id-test",
                "release",
                "https://piston-meta.mojang.com/version.json",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                string.Empty,
                0);

            await installer.InstallAsync(instance, version);
            Equal(1, handler.AssetIndexRequests, "valid asset id should request its index exactly once");
            Equal(true,
                File.Exists(Path.Combine(
                    paths.GetInstanceGameDirectory(instance.Id),
                    "assets",
                    "indexes",
                    "1.21.json")),
                "valid asset id should remain under assets/indexes");
        }
        finally
        {
            try { Directory.Delete(validRoot, recursive: true); } catch { }
        }
    }

    private static async Task TestAssetIndexSchemaAndHashValidationAsync()
    {
        async Task RunInvalidAsync(string indexBody, string label)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "un-nexo-asset-index-validation-tests",
                Guid.NewGuid().ToString("N"));
            try
            {
                var paths = new NexoPathService(root);
                var handler = new AssetIndexContentHandler(indexBody, Encoding.UTF8.GetBytes("unused"));
                using var client = new HttpClient(handler);
                var installer = new MinecraftVanillaInstallService(
                    client,
                    paths,
                    new DownloadSourceService(),
                    TimeSpan.FromSeconds(2));
                var instance = new GameInstance(
                    Guid.NewGuid().ToString("N"),
                    "Asset index validation",
                    "asset-index-validation",
                    "vanilla",
                    DateTimeOffset.UtcNow);
                var version = new MinecraftVersionInfo(
                    "asset-index-validation",
                    "release",
                    "https://piston-meta.mojang.com/version.json",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    string.Empty,
                    0);

                try
                {
                    await installer.InstallAsync(instance, version);
                    throw new Exception($"Invalid asset index fixture '{label}' should be rejected.");
                }
                catch (InvalidDataException)
                {
                }

                Equal(0, handler.ResourceRequests,
                    $"invalid asset index '{label}' must be rejected before resource downloads");
                Equal(false,
                    File.Exists(Path.Combine(paths.GetInstanceDirectory(instance.Id), "install-state.json")),
                    $"invalid asset index '{label}' must not publish prepared state");
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }

        foreach (var fixture in new (string Label, string Body)[]
        {
            ("missing objects", "{}"),
            ("null objects", "{\"objects\":null}"),
            ("array objects", "{\"objects\":[]}"),
            ("numeric asset entry", "{\"objects\":{\"x\":123}}"),
            ("null hash", "{\"objects\":{\"x\":{\"hash\":null}}}"),
            ("numeric hash", "{\"objects\":{\"x\":{\"hash\":123}}}")
        })
            await RunInvalidAsync(fixture.Body, fixture.Label);

        foreach (var hash in new[]
        {
            string.Empty,
            "a",
            new string('a', 39),
            new string('a', 41),
            new string('g', 40),
            "../" + new string('a', 37),
            @"aa\..\..\escape"
        })
        {
            var encodedHash = System.Text.Json.JsonSerializer.Serialize(hash);
            await RunInvalidAsync(
                "{\"objects\":{\"x\":{\"hash\":" + encodedHash + "}}}",
                "hash " + encodedHash);
        }

        var assetBytes = Encoding.UTF8.GetBytes("valid asset payload");
        var lowerHash = Convert.ToHexString(SHA1.HashData(assetBytes)).ToLowerInvariant();
        foreach (var hash in new[] { lowerHash, lowerHash.ToUpperInvariant() })
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "un-nexo-asset-index-validation-tests",
                Guid.NewGuid().ToString("N"));
            try
            {
                var paths = new NexoPathService(root);
                var indexBody =
                    "{\"objects\":{\"minecraft/test.txt\":{\"hash\":\""
                    + hash
                    + "\"}}}";
                var handler = new AssetIndexContentHandler(indexBody, assetBytes);
                using var client = new HttpClient(handler);
                var installer = new MinecraftVanillaInstallService(
                    client,
                    paths,
                    new DownloadSourceService(),
                    TimeSpan.FromSeconds(2));
                var instance = new GameInstance(
                    Guid.NewGuid().ToString("N"),
                    "Asset index valid",
                    "asset-index-validation",
                    "vanilla",
                    DateTimeOffset.UtcNow);
                var version = new MinecraftVersionInfo(
                    "asset-index-validation",
                    "release",
                    "https://piston-meta.mojang.com/version.json",
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    string.Empty,
                    0);

                await installer.InstallAsync(instance, version);

                Equal(1, handler.ResourceRequests,
                    "valid asset hash should download exactly one resource");
                var expected = Path.Combine(
                    paths.GetInstanceGameDirectory(instance.Id),
                    "assets",
                    "objects",
                    lowerHash[..2],
                    lowerHash);
                Equal(true, File.Exists(expected),
                    "valid asset hash should be canonicalized under assets/objects");
                Equal(true,
                    File.Exists(Path.Combine(paths.GetInstanceDirectory(instance.Id), "install-state.json")),
                    "valid asset index should publish prepared state");
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }

    private static async Task TestServerDecoratorInheritedMetadataAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-server-decorator-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new NexoPathService(root);
            var instanceId = Guid.NewGuid().ToString("N");
            var gameRoot = paths.GetInstanceGameDirectory(instanceId);
            var versionsRoot = Path.Combine(gameRoot, "versions");
            Directory.CreateDirectory(versionsRoot);

            async Task WriteVersionAsync(string id, string json)
            {
                var directory = Path.Combine(versionsRoot, id);
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(Path.Combine(directory, id + ".json"), json);
            }

            await WriteVersionAsync(
                "quick-base",
                """
                {
                  "id":"quick-base",
                  "arguments":{
                    "game":[
                      {
                        "rules":[
                          {
                            "action":"allow",
                            "features":{"is_quick_play_multiplayer":true}
                          }
                        ],
                        "value":["--quickPlayMultiplayer","${quickPlayMultiplayer}"]
                      }
                    ]
                  }
                }
                """);
            await WriteVersionAsync(
                "quick-child",
                """
                {"id":"quick-child","inheritsFrom":"quick-base"}
                """);

            var decorator = new MinecraftServerLaunchDecorator(paths);
            var target = MinecraftServerTarget.Parse("play.example.net:25566");
            var basePlan = new MinecraftLaunchPlan(
                "java",
                gameRoot,
                [],
                Path.Combine(gameRoot, "logs", "server-test.log"));
            var quickInstance = new GameInstance(
                instanceId,
                "Inherited Quick Play",
                "quick-child",
                "fabric",
                DateTimeOffset.UtcNow);

            var quickPlan = await decorator.ApplyAsync(basePlan, quickInstance, target);
            Contains(quickPlan.Arguments, "--quickPlayMultiplayer",
                "inherited Quick Play metadata should use Quick Play");
            Contains(quickPlan.Arguments, "play.example.net:25566",
                "Quick Play should use target authority");
            DoesNotContain(quickPlan.Arguments, "--server",
                "Quick Play path must not append legacy server arguments");

            await WriteVersionAsync(
                "legacy-base",
                """
                {"id":"legacy-base","arguments":{"game":["--demo"]}}
                """);
            await WriteVersionAsync(
                "legacy-child",
                """
                {"id":"legacy-child","inheritsFrom":"legacy-base"}
                """);
            var legacyInstance = quickInstance with { VersionId = "legacy-child" };
            var legacyPlan = await decorator.ApplyAsync(basePlan, legacyInstance, target);
            Contains(legacyPlan.Arguments, "--server",
                "metadata without Quick Play should retain legacy server arguments");
            Contains(legacyPlan.Arguments, "play.example.net",
                "legacy server path should include the host");
            Contains(legacyPlan.Arguments, "25566",
                "legacy server path should include the port");

            var malformed = new (string Id, string Json)[]
            {
                ("bad-root", "[]"),
                ("bad-arguments", "{\"id\":\"bad-arguments\",\"arguments\":[]}"),
                ("bad-game", "{\"id\":\"bad-game\",\"arguments\":{\"game\":{}}}"),
                ("bad-item", "{\"id\":\"bad-item\",\"arguments\":{\"game\":[123]}}"),
                ("bad-rules", "{\"id\":\"bad-rules\",\"arguments\":{\"game\":[{\"rules\":{}}]}}"),
                ("bad-rule", "{\"id\":\"bad-rule\",\"arguments\":{\"game\":[{\"rules\":[123]}]}}")
            };

            foreach (var fixture in malformed)
            {
                await WriteVersionAsync(fixture.Id, fixture.Json);
                var instance = quickInstance with { VersionId = fixture.Id };
                try
                {
                    await decorator.ApplyAsync(basePlan, instance, target);
                    throw new Exception($"Malformed server metadata '{fixture.Id}' should be rejected.");
                }
                catch (InvalidDataException)
                {
                }
            }

            await WriteVersionAsync(
                "missing-parent-child",
                """
                {"id":"missing-parent-child","inheritsFrom":"missing-parent"}
                """);
            try
            {
                await decorator.ApplyAsync(
                    basePlan,
                    quickInstance with { VersionId = "missing-parent-child" },
                    target);
                throw new Exception("Missing inherited metadata should be rejected.");
            }
            catch (FileNotFoundException)
            {
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task TestManifestStreamingFallbackAsync()
    {
        var sources = new DownloadSourceService();
        sources.SetSource("bmclapi");
        var handler = new ManifestFallbackHandler();
        using var client = new HttpClient(handler);
        var service = new MinecraftVersionManifestService(client, sources);

        var catalog = await service.GetCatalogAsync();
        Equal("1.21.4", catalog.Latest.LatestRelease, "official fallback release");
        Equal(2, handler.Hosts.Count, "both mirror and official should be attempted");
        Equal("bmclapi2.bangbang93.com", handler.Hosts[0], "mirror should be first");
        Equal("launchermeta.mojang.com", handler.Hosts[1], "official should be fallback");
    }

    private static async Task TestLaunchPlanAsync(string version, int expectedJava, bool modern)
    {
        var temp = Path.Combine(Path.GetTempPath(), "UN Nexo regression with spaces", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new NexoPathService(temp);
            var instance = new GameInstance(
                    Guid.NewGuid().ToString("N"), $"Minecraft {version}", version, "vanilla", DateTimeOffset.UtcNow);
            var accountUuid = Guid.NewGuid();
            var account = new LauncherAccount("local-test", "offline", "NexoTester", accountUuid.ToString(), DateTimeOffset.UtcNow);
            var instanceRoot = paths.GetInstanceDirectory(instance.Id);
            var gameRoot = paths.GetInstanceGameDirectory(instance.Id);
            var versionRoot = Path.Combine(gameRoot, "versions", version);
            var assetsRoot = Path.Combine(gameRoot, "assets");
            Directory.CreateDirectory(versionRoot);
            Directory.CreateDirectory(Path.Combine(assetsRoot, "indexes"));
            await File.WriteAllTextAsync(Path.Combine(instanceRoot, "install-state.json"), "{}");
            await File.WriteAllBytesAsync(Path.Combine(versionRoot, version + ".jar"), [1]);

            var assetId = modern ? "17" : "legacy";
            await File.WriteAllTextAsync(Path.Combine(assetsRoot, "indexes", assetId + ".json"),
                modern ? "{\"objects\":{}}" : "{\"virtual\":true,\"objects\":{}}");

            var metadataTemplate = modern
                ? """
                  {
                    "id":"__VERSION__",
                    "type":"release",
                    "javaVersion":{"majorVersion":21},
                    "downloads":{"client":{}},
                    "assetIndex":{"id":"__ASSET_ID__"},
                    "libraries":[],
                    "mainClass":"net.minecraft.client.main.Main",
                    "arguments":{
                      "jvm":["-Djava.library.path=${natives_directory}","-cp","${classpath}","-Dnexo.launcher=${launcher_version}"],
                      "game":[
                        "--username","${auth_player_name}","--version","${version_name}","--gameDir","${game_directory}","--assetsDir","${assets_root}","--assetIndex","${assets_index_name}","--uuid","${auth_uuid}","--accessToken","${auth_access_token}","--userType","${user_type}","--clientId","${clientid}","--xuid","${auth_xuid}","--versionType","${version_type}",
                        {"rules":[{"action":"allow","features":{"is_quick_play_multiplayer":true}}],"value":["--quickPlayMultiplayer","${quickPlayMultiplayer}"]}
                      ]
                    }
                  }
                  """
                : """
                  {
                    "id":"__VERSION__",
                    "type":"release",
                    "downloads":{"client":{}},
                    "assetIndex":{"id":"__ASSET_ID__"},
                    "libraries":[],
                    "mainClass":"net.minecraft.client.main.Main",
                    "minecraftArguments":"--username ${auth_player_name} --version ${version_name} --gameDir \"${game_directory}\" --assetsDir ${game_assets} --assetIndex ${assets_index_name} --uuid ${auth_uuid} --accessToken ${auth_access_token} --userProperties ${user_properties} --userType ${user_type}"
                  }
                  """;
            var metadata = metadataTemplate
                .Replace("__VERSION__", version, StringComparison.Ordinal)
                .Replace("__ASSET_ID__", assetId, StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(versionRoot, version + ".json"), metadata);

            var java8 = Path.Combine(temp, "java8");
            var java21 = Path.Combine(temp, "java21");
            await File.WriteAllTextAsync(java8, "test");
            await File.WriteAllTextAsync(java21, "test");
            var installations = new[]
            {
                new JavaInstallation(java21, temp, "21.0.8", true, "test"),
                new JavaInstallation(java8, temp, "1.8.0_442", true, "test")
            };

            var plan = await new MinecraftLaunchPlanBuilder(paths).BuildAsync(instance, account, installations);
            Equal(expectedJava == 21 ? java21 : java8, plan.JavaPath, "required Java selection");
            Equal(gameRoot, plan.WorkingDirectory, "instance working directory");
            Contains(plan.Arguments, "NexoTester", "username substitution");
            Contains(plan.Arguments, accountUuid.ToString("N"), "UUID substitution");
            Contains(plan.Arguments, gameRoot, "game directory should remain one argument even with spaces");
            Contains(plan.Arguments, "net.minecraft.client.main.Main", "main class");
            if (modern)
            {
                ContainsPrefix(plan.Arguments, "-Dnexo.launcher=", "launcher version substitution");

                var microsoftUuid = Guid.NewGuid();
                var microsoftAccount = new LauncherAccount(
                    "microsoft:" + microsoftUuid.ToString("N"),
                    "microsoft",
                    "OnlineTester",
                    microsoftUuid.ToString("D"),
                    DateTimeOffset.UtcNow)
                {
                    AuthenticationId = "test-home-account"
                };
                const string minecraftAccessToken = "TEST-LAUNCH-ACCESS-TOKEN";
                const string testXuid = "2814639012345678";
                var microsoftCredentials = new MinecraftLaunchCredentials(
                    microsoftAccount.Id,
                    microsoftAccount.DisplayName,
                    microsoftAccount.Uuid,
                    minecraftAccessToken,
                    MsalMicrosoftAccessTokenProvider.ClientId,
                    testXuid);

                var onlinePlan = await new MinecraftLaunchPlanBuilder(paths).BuildAsync(
                    instance,
                    microsoftAccount,
                    installations,
                    microsoftCredentials);
                Contains(onlinePlan.Arguments, "OnlineTester", "Microsoft username substitution");
                Contains(onlinePlan.Arguments, microsoftUuid.ToString("N"), "Microsoft UUID substitution");
                Contains(onlinePlan.Arguments, minecraftAccessToken, "Minecraft access token substitution");
                Contains(onlinePlan.Arguments, "msa", "Microsoft user type substitution");
                Contains(onlinePlan.Arguments, MsalMicrosoftAccessTokenProvider.ClientId, "Microsoft Client ID substitution");
                Contains(onlinePlan.Arguments, testXuid, "Microsoft XUID substitution");
            }
            else
            {
                Contains(plan.Arguments, "-Djava.library.path=" + Path.Combine(gameRoot, "natives", version), "legacy native path");
            }

            var target = MinecraftServerTarget.Parse("play.example.net:25566");
            var serverPlan = await new MinecraftServerLaunchDecorator(paths).ApplyAsync(plan, instance, target);
            if (modern)
            {
                Contains(serverPlan.Arguments, "--quickPlayMultiplayer", "modern server launch should use Quick Play");
                Contains(serverPlan.Arguments, target.Authority, "Quick Play target");
                DoesNotContain(serverPlan.Arguments, "--server", "modern server launch should not use removed legacy option");
            }
            else
            {
                Contains(serverPlan.Arguments, "--server", "legacy server option");
                Contains(serverPlan.Arguments, target.Host, "legacy server host");
                Contains(serverPlan.Arguments, "--port", "legacy port option");
                Contains(serverPlan.Arguments, target.Port.ToString(), "legacy server port");
            }

            var startInfo = serverPlan.CreateStartInfo();
            Contains(startInfo.ArgumentList, gameRoot, "ProcessStartInfo.ArgumentList must preserve spaced path");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static async Task TestMinecraft152LaunchPlanAsync()
    {
        const string version = "1.5.2";
        var temp = Path.Combine(Path.GetTempPath(), "UN Nexo 1.5.2 regression", Guid.NewGuid().ToString("N"));
        try
        {
            var paths = new NexoPathService(temp);
            var instance = new GameInstance(
                    Guid.NewGuid().ToString("N"), "Minecraft 1.5.2", version, "vanilla", DateTimeOffset.UtcNow);
            var account = new LauncherAccount("local-152", "offline", "LegacyTester", Guid.NewGuid().ToString(), DateTimeOffset.UtcNow);
            var instanceRoot = paths.GetInstanceDirectory(instance.Id);
            var gameRoot = paths.GetInstanceGameDirectory(instance.Id);
            var versionRoot = Path.Combine(gameRoot, "versions", version);
            var assetsRoot = Path.Combine(gameRoot, "assets");
            var librariesRoot = Path.Combine(gameRoot, "libraries");
            Directory.CreateDirectory(versionRoot);
            Directory.CreateDirectory(Path.Combine(assetsRoot, "indexes"));
            Directory.CreateDirectory(Path.Combine(assetsRoot, "objects", "aa"));
            await File.WriteAllTextAsync(Path.Combine(instanceRoot, "install-state.json"), "{}");
            await File.WriteAllBytesAsync(Path.Combine(versionRoot, version + ".jar"), [1]);

            var launchWrapperRelative = "net/minecraft/launchwrapper/1.5/launchwrapper-1.5.jar";
            var launchWrapperPath = Path.Combine(librariesRoot, launchWrapperRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(launchWrapperPath)!);
            await File.WriteAllBytesAsync(launchWrapperPath, [2]);

            var assetBytes = new byte[] { 3 };
            var assetHash = Convert.ToHexString(
                    SHA1.HashData(assetBytes))
                .ToLowerInvariant();
            var objectPath = Path.Combine(
                assetsRoot,
                "objects",
                assetHash[..2],
                assetHash);
            Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
            await File.WriteAllBytesAsync(objectPath, assetBytes);
            await File.WriteAllTextAsync(
                Path.Combine(assetsRoot, "indexes", "legacy.json"),
                "{\"virtual\":true,\"objects\":{\"lang/en_US.lang\":{\"hash\":\"" + assetHash + "\"}}}");

            var metadata = """
                {
                  "id":"1.5.2",
                  "type":"release",
                  "downloads":{"client":{}},
                  "assetIndex":{"id":"legacy"},
                  "libraries":[
                    {
                      "name":"net.minecraft:launchwrapper:1.5",
                      "downloads":{"artifact":{"path":"net/minecraft/launchwrapper/1.5/launchwrapper-1.5.jar","size":1}}
                    }
                  ],
                  "mainClass":"net.minecraft.launchwrapper.Launch",
                  "minecraftArguments":"${auth_player_name} ${auth_session} --gameDir ${game_directory} --assetsDir ${game_assets}"
                }
                """;
            await File.WriteAllTextAsync(Path.Combine(versionRoot, version + ".json"), metadata);

            var java8 = Path.Combine(temp, "java8");
            await File.WriteAllTextAsync(java8, "test");
            var plan = await new MinecraftLaunchPlanBuilder(paths).BuildAsync(
                instance,
                account,
                [new JavaInstallation(java8, temp, "1.8.0_442", true, "test")]);

            Equal(java8, plan.JavaPath, "Minecraft 1.5.2 should default to Java 8");
            Contains(plan.Arguments, "net.minecraft.launchwrapper.Launch", "1.5.2 launchwrapper main class");
            Contains(plan.Arguments, "LegacyTester", "1.5.2 username argument");
            Contains(plan.Arguments, "0", "1.5.2 offline session argument");
            Contains(plan.Arguments, gameRoot, "1.5.2 game directory");
            var virtualAssets = Path.Combine(assetsRoot, "virtual", "legacy");
            Contains(plan.Arguments, virtualAssets, "1.5.2 legacy virtual assets argument");
            Equal(true, File.Exists(Path.Combine(virtualAssets, "lang", "en_US.lang")), "legacy asset should be materialized");
            Any(plan.Arguments,
                value => value.Contains("launchwrapper-1.5.jar", StringComparison.Ordinal)
                         && value.Contains("1.5.2.jar", StringComparison.Ordinal),
                "1.5.2 classpath should include launchwrapper and client");

            var serverPlan = await new MinecraftServerLaunchDecorator(paths).ApplyAsync(
                plan,
                instance,
                MinecraftServerTarget.Parse("localhost:25565"));
            Contains(serverPlan.Arguments, "--server", "1.5.2 direct connect server argument");
            Contains(serverPlan.Arguments, "--port", "1.5.2 direct connect port argument");
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'.");
    }

    private static void Contains(IEnumerable<string> values, string expected, string message)
    {
        if (!values.Contains(expected, StringComparer.Ordinal))
            throw new InvalidOperationException($"{message}: missing '{expected}'.");
    }

    private static void ContainsPrefix(IEnumerable<string> values, string prefix, string message)
    {
        if (!values.Any(value => value.StartsWith(prefix, StringComparison.Ordinal)))
            throw new InvalidOperationException($"{message}: missing prefix '{prefix}'.");
    }

    private static void ContainsText(string value, string expected, string message)
    {
        if (!value.Contains(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{message}: '{value}' does not contain '{expected}'.");
    }

    private static void Any(IEnumerable<string> values, Func<string, bool> predicate, string message)
    {
        if (!values.Any(predicate))
            throw new InvalidOperationException(message);
    }

    private static void DoesNotContain(IEnumerable<string> values, string unexpected, string message)
    {
        if (values.Contains(unexpected, StringComparer.Ordinal))
            throw new InvalidOperationException($"{message}: unexpected '{unexpected}'.");
    }

    private static void Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException($"{message}: expected {typeof(TException).Name}.");
    }

    private sealed class AssetIndexContentHandler(string indexBody, byte[] assetBytes) : HttpMessageHandler
    {
        public int ResourceRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI.");
            if (uri.Host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                const string metadata =
                    "{\"id\":\"asset-index-validation\",\"downloads\":{\"client\":{\"url\":\"https://piston-data.mojang.com/client.jar\"}},\"assetIndex\":{\"id\":\"test-assets\",\"url\":\"https://launchermeta.mojang.com/index.json\"},\"libraries\":[]}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(metadata, Encoding.UTF8, "application/json")
                });
            }

            if (uri.Host.Equals("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("client"))
                });
            }

            if (uri.Host.Equals("launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(indexBody, Encoding.UTF8, "application/json")
                });
            }

            if (uri.Host.Equals("resources.download.minecraft.net", StringComparison.OrdinalIgnoreCase))
            {
                ResourceRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(assetBytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class AssetIndexIdHandler(string assetId) : HttpMessageHandler
    {
        public int AssetIndexRequests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Missing request URI.");
            if (uri.Host.Equals("piston-meta.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                var encodedId = System.Text.Json.JsonSerializer.Serialize(assetId);
                var metadata =
                    "{\"id\":\"asset-id-test\",\"downloads\":{\"client\":{\"url\":\"https://piston-data.mojang.com/client.jar\"}},\"assetIndex\":{\"id\":"
                    + encodedId
                    + ",\"url\":\"https://launchermeta.mojang.com/index.json\"},\"libraries\":[]}";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(metadata, Encoding.UTF8, "application/json")
                });
            }

            if (uri.Host.Equals("piston-data.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("client"))
                });
            }

            if (uri.Host.Equals("launchermeta.mojang.com", StringComparison.OrdinalIgnoreCase))
            {
                AssetIndexRequests++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"objects\":{}}", Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class ManifestFallbackHandler : HttpMessageHandler
    {
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri?.Host ?? string.Empty;
            Hosts.Add(host);
            if (host.Equals("bmclapi2.bangbang93.com", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new InterruptedResponseStream())
                });
            }

            const string manifest = "{\"latest\":{\"release\":\"1.21.4\",\"snapshot\":\"25w01a\"},\"versions\":[]}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(manifest)
            });
        }
    }

    private sealed class ManagedJavaHandler(
        byte[] archiveBytes,
        string checksum,
        string? metadataJson = null) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            var host = request.RequestUri?.Host ?? string.Empty;
            if (host.Equals("api.adoptium.net", StringComparison.OrdinalIgnoreCase))
            {
                var json = metadataJson
                    ?? "[{\"binary\":{\"package\":{\"link\":\"https://runtime.example.test/temurin8.zip\",\"checksum\":\""
                       + checksum
                       + "\"}},\"version_data\":{\"semver\":\"8.0.442+6\"}}]";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                });
            }

            if (host.Equals("runtime.example.test", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(archiveBytes)
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class AdoptiumContentHandler(HttpContent content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            });
    }

    private sealed class DeclaredLengthContent(long declaredLength) : HttpContent
    {
        public bool ReadAttempted { get; private set; }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            ReadAttempted = true;
            throw new InvalidOperationException("Oversized declared content must not be read.");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = declaredLength;
            return true;
        }
    }

    private sealed class InterruptedResponseStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
            => throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely."));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
