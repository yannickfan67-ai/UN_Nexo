using System.IO.Compression;
using System.Runtime.CompilerServices;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class CrashDiagnosisRegression
{
    [ModuleInitializer]
    public static void RunBeforeMain() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var service = new MinecraftCrashDiagnosisService();

        var java = service.Analyze(1, "java.lang.UnsupportedClassVersionError: Foo has been compiled by a more recent version of the Java Runtime (class file version 65.0)");
        Equal("java-version-mismatch", java.Code, "Java mismatch classification");
        Equal(true, java.IsKnownCause, "Java mismatch should be known");

        var missingLibrary = service.Analyze(127, "java: error while loading shared libraries: libExample.so.7: cannot open shared object file: No such file or directory");
        Equal("linux-system-library-missing", missingLibrary.Code, "generic Linux shared library classification");
        Contains(missingLibrary.Title, "libExample.so.7", "missing library name should be surfaced");
        Contains(missingLibrary.Evidence, "libExample.so.7", "missing library evidence should retain exact library");

        var heap = service.Analyze(1, "Exception in thread main java.lang.OutOfMemoryError: Java heap space");
        Equal("java-heap-oom", heap.Code, "heap OOM classification");

        var reservation = service.Analyze(1, "Error occurred during initialization of VM\nCould not reserve enough space for object heap");
        Equal("jvm-memory-reservation", reservation.Code, "JVM reservation classification");

        var graphics = service.Analyze(1, "[stderr] GLFW error 65542: WGL: The driver does not appear to support OpenGL");
        Equal("graphics-initialization", graphics.Code, "graphics classification");

        var unknown = service.Analyze(23, "[stderr] Something project-specific went wrong\n[launcher] Exit code: 23");
        Equal("unknown-nonzero-exit", unknown.Code, "unknown exits must remain unknown");
        Equal(false, unknown.IsKnownCause, "unknown exit must not claim a known cause");
        Contains(unknown.Evidence, "Something project-specific", "unknown diagnosis should preserve raw evidence");

        var normal = service.Analyze(0, "[launcher] Exit code: 0");
        Equal("normal-exit", normal.Code, "normal exit classification");
        var stopped = service.Analyze(null, null, wasStopped: true);
        Equal("stopped", stopped.Code, "intentional stop classification");

        var sanitized = service.Sanitize("--accessToken super-secret-token\nAuthorization: Bearer abcdefghijklmnop\nsecret=keep", "keep");
        Contains(sanitized, "--accessToken <REDACTED>", "access token argument should be redacted");
        Equal(false, sanitized.Contains("super-secret-token", StringComparison.Ordinal), "raw access token must be absent");
        Equal(false, sanitized.Contains("abcdefghijklmnop", StringComparison.Ordinal), "Bearer token must be absent");
        Equal(false, sanitized.Contains("keep", StringComparison.Ordinal), "explicit secret must be absent");

        var tempDirectory = Path.Combine(Path.GetTempPath(), "nexo-crash-diagnosis", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var boundedLog = Path.Combine(tempDirectory, "large.log");
        try
        {
            var padding = new string('x', 600 * 1024);
            await File.WriteAllTextAsync(boundedLog, "UnsupportedClassVersionError\n" + padding + "\n[stderr] error while loading shared libraries: libTail.so.2: cannot open shared object file\n[launcher] Exit code: 127\n");
            var fileDiagnosis = await service.AnalyzeFileAsync(127, boundedLog);
            Equal("linux-system-library-missing", fileDiagnosis.Code, "file analysis should use bounded recent log tail");
            Contains(fileDiagnosis.Title, "libTail.so.2", "tail analysis should retain recent evidence");

            await TestSanitizedBundleAsync(service, tempDirectory);
        }
        finally
        {
            try { Directory.Delete(tempDirectory, recursive: true); } catch { }
        }

        Console.WriteLine("PASS crash diagnosis and sanitized export regressions");
    }

    private static async Task TestSanitizedBundleAsync(MinecraftCrashDiagnosisService diagnosisService, string directory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var token = "nexo-secret-access-token-123456";
        var logPath = Path.Combine(directory, "minecraft.log");
        await File.WriteAllTextAsync(logPath,
            $"[stdout] user-home={home}\n--accessToken {token}\nAuthorization: Bearer abcdefghijklmnop\n[stderr] GLFW error 65542: test\n");

        var diagnosis = diagnosisService.Analyze(1, await File.ReadAllTextAsync(logPath));
        var bundles = new DiagnosticBundleService(diagnosisService);
        var preview = await bundles.BuildPreviewAsync(diagnosis, 1, [logPath], [token]);
        Equal(false, preview.Summary.Contains(token, StringComparison.Ordinal), "preview summary must redact explicit secret");
        Equal(false, preview.LogExcerpt.Contains(token, StringComparison.Ordinal), "preview log must redact explicit secret");
        if (!string.IsNullOrWhiteSpace(home))
            Equal(false, preview.LogExcerpt.Contains(home, StringComparison.OrdinalIgnoreCase), "preview log must redact home path");

        var archivePath = Path.Combine(directory, "diagnostic.zip");
        var result = await bundles.ExportAsync(diagnosis, 1, [logPath], archivePath, [token]);
        Equal(1, result.IncludedLogs, "bundle should include source log");
        Equal(true, File.Exists(result.ArchivePath), "bundle archive should exist");

        using var archive = ZipFile.OpenRead(result.ArchivePath);
        Equal(true, archive.GetEntry("diagnosis.txt") is not null, "bundle diagnosis entry");
        Equal(true, archive.GetEntry("manifest.txt") is not null, "bundle manifest entry");
        Equal(true, archive.Entries.Any(entry => entry.FullName.StartsWith("logs/", StringComparison.Ordinal)), "bundle log entry");

        var combined = string.Empty;
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            combined += await reader.ReadToEndAsync();
        }

        Equal(false, combined.Contains(token, StringComparison.Ordinal), "archive must not contain explicit secret");
        Equal(false, combined.Contains("abcdefghijklmnop", StringComparison.Ordinal), "archive must not contain bearer token");
        if (!string.IsNullOrWhiteSpace(home))
            Equal(false, combined.Contains(home, StringComparison.OrdinalIgnoreCase), "archive must not contain raw home path");
        Contains(combined, "<REDACTED>", "archive should show token redaction marker");
        if (!string.IsNullOrWhiteSpace(home))
            Contains(combined, "<HOME>", "archive should show home redaction marker");
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'.");
    }

    private static void Contains(string value, string expected, string message)
    {
        if (!value.Contains(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{message}: '{expected}' not found in '{value}'.");
    }
}
