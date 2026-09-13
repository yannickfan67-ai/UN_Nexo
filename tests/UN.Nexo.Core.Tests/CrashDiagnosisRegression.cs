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

        var tempDirectory = Path.Combine(Path.GetTempPath(), "nexo-crash-diagnosis");
        Directory.CreateDirectory(tempDirectory);
        var temp = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + ".log");
        try
        {
            var padding = new string('x', 600 * 1024);
            await File.WriteAllTextAsync(temp, "UnsupportedClassVersionError\n" + padding + "\n[stderr] error while loading shared libraries: libTail.so.2: cannot open shared object file\n[launcher] Exit code: 127\n");
            var fileDiagnosis = await service.AnalyzeFileAsync(127, temp);
            Equal("linux-system-library-missing", fileDiagnosis.Code, "file analysis should use bounded recent log tail");
            Contains(fileDiagnosis.Title, "libTail.so.2", "tail analysis should retain recent evidence");
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }

        Console.WriteLine("PASS crash diagnosis regressions");
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
