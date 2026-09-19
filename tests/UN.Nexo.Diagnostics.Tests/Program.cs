using System.IO.Compression;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Diagnostics.Tests;

internal static class Program
{
    private static async Task<int> Main()
    {
        try
        {
            Console.WriteLine("[diagnostics] classification checks");
            var service = new MinecraftCrashDiagnosisService();

            var java = service.Analyze(1, "java.lang.UnsupportedClassVersionError: class file version 65.0");
            Equal("java-version-mismatch", java.Code, "Java mismatch classification");
            Equal(true, java.IsKnownCause, "Java mismatch should be known");

            var missingLibrary = service.Analyze(127, "java: error while loading shared libraries: libExample.so.7: cannot open shared object file: No such file or directory");
            Equal("linux-system-library-missing", missingLibrary.Code, "Linux shared library classification");
            Contains(missingLibrary.Title, "libExample.so.7", "missing library title");

            Equal("java-heap-oom", service.Analyze(1, "java.lang.OutOfMemoryError: Java heap space").Code, "heap OOM");
            Equal("jvm-memory-reservation", service.Analyze(1, "Could not reserve enough space for object heap").Code, "JVM reservation");
            Equal("graphics-initialization", service.Analyze(1, "GLFW error 65542: WGL: driver does not support OpenGL").Code, "graphics failure");

            var unknown = service.Analyze(23, "[stderr] Something project-specific went wrong\n[launcher] Exit code: 23");
            Equal("unknown-nonzero-exit", unknown.Code, "unknown exit classification");
            Equal(false, unknown.IsKnownCause, "unknown exit must not claim a known cause");
            Contains(unknown.Evidence, "Something project-specific", "unknown evidence");
            Equal("normal-exit", service.Analyze(0, "[launcher] Exit code: 0").Code, "normal exit");
            Equal("stopped", service.Analyze(null, null, wasStopped: true).Code, "intentional stop");

            Console.WriteLine("[diagnostics] sanitizer checks");
            var explicitSecret = "keep-this-secret-private";
            var sanitized = service.Sanitize(
                "--accessToken super-secret-token\nAuthorization: Bearer abcdefghijklmnop\n"
                + "{\"access_token\":\"minecraft-json-token\",\"refresh_token\":\"microsoft-refresh-token\","
                + "\"identityToken\":\"XBL3.0 x=123;xsts-token-value\",\"Token\":\"xbox-token-value\","
                + "\"client_token\":\"client-json-token\",\"session_id\":\"session-json-token\","
                + "\"authorization\":\"json-authorization-secret\"}\n"
                + "https://example.test/callback?access_token=query-access-secret&state=keep-state\n"
                + "https://example.test/callback?client_token=query-client-secret&code=keep-code\n"
                + "access_token=form-access-secret&scope=keep-scope\n"
                + "benign-tokenizer=keep-this-value\n"
                + "secret=" + explicitSecret,
                explicitSecret);
            Contains(sanitized, "--accessToken <REDACTED>", "access token marker");
            DoesNotContain(sanitized, "super-secret-token", "raw access token");
            DoesNotContain(sanitized, "abcdefghijklmnop", "Bearer token");
            DoesNotContain(sanitized, "minecraft-json-token", "JSON Minecraft token");
            DoesNotContain(sanitized, "microsoft-refresh-token", "JSON Microsoft refresh token");
            DoesNotContain(sanitized, "xsts-token-value", "JSON identity token");
            DoesNotContain(sanitized, "xbox-token-value", "JSON Xbox token");
            DoesNotContain(sanitized, "client-json-token", "JSON client token");
            DoesNotContain(sanitized, "session-json-token", "JSON session id");
            DoesNotContain(sanitized, "json-authorization-secret", "JSON authorization value");
            DoesNotContain(sanitized, "query-access-secret", "query access token");
            DoesNotContain(sanitized, "query-client-secret", "query client token");
            DoesNotContain(sanitized, "form-access-secret", "form access token");
            Contains(sanitized, "&state=keep-state", "query state should remain useful");
            Contains(sanitized, "&code=keep-code", "query code should remain useful");
            Contains(sanitized, "&scope=keep-scope", "form scope should remain useful");
            Contains(sanitized, "benign-tokenizer=keep-this-value", "benign token-like text should remain");
            DoesNotContain(sanitized, explicitSecret, "explicit secret");

            Console.WriteLine("[diagnostics] bounded-tail and ZIP checks");
            var tempDirectory = Path.Combine(Path.GetTempPath(), "nexo-diagnostics-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            try
            {
                var largeLog = Path.Combine(tempDirectory, "large.log");
                await File.WriteAllTextAsync(
                    largeLog,
                    "UnsupportedClassVersionError\n" + new string('x', 600 * 1024) +
                    "\n[stderr] error while loading shared libraries: libTail.so.2: cannot open shared object file\n[launcher] Exit code: 127\n");
                var tailDiagnosis = await service.AnalyzeFileAsync(127, largeLog);
                Equal("linux-system-library-missing", tailDiagnosis.Code, "bounded log tail classification");
                Contains(tailDiagnosis.Title, "libTail.so.2", "recent tail evidence");

                var exactTail = Path.Combine(tempDirectory, "exact-tail.log");
                await File.WriteAllTextAsync(exactTail, "old-line\n1234567890");
                var bounded = await service.ReadLogTailAsync(exactTail, 10);
                Equal("1234567890", bounded, "tail must stay within requested byte snapshot");

                var rejectedLimit = false;
                try
                {
                    await service.ReadLogTailAsync(exactTail, 0);
                }
                catch (ArgumentOutOfRangeException)
                {
                    rejectedLimit = true;
                }
                Equal(true, rejectedLimit, "non-positive tail limit must be rejected");

                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var token = "nexo-secret-access-token-123456";
                var minecraftLog = Path.Combine(tempDirectory, "minecraft.log");
                await File.WriteAllTextAsync(
                    minecraftLog,
                    $"[stdout] user-home={home}\n"
                    + $"--accessToken {token}\n"
                    + "Authorization: Bearer abcdefghijklmnop\n"
                    + "{\"access_token\":\"zip-json-secret\",\"client_token\":\"zip-client-secret\"}\n"
                    + "https://example.test/callback?access_token=zip-query-secret&state=keep-state\n"
                    + "access_token=zip-form-secret&scope=keep-scope\n"
                    + "benign-tokenizer=keep-this-value\n"
                    + "[stderr] GLFW error 65542: test\n");

                var diagnosis = service.Analyze(1, await File.ReadAllTextAsync(minecraftLog));
                var bundles = new DiagnosticBundleService(service);
                Contains(
                    DiagnosticBundleService.PreviewScopeNotice,
                    "sanitized excerpt",
                    "preview scope should state that it is an excerpt");
                Contains(
                    DiagnosticBundleService.PreviewScopeNotice,
                    "64 KiB",
                    "preview scope should state the per-log preview limit");
                Contains(
                    DiagnosticBundleService.PreviewScopeNotice,
                    "12 KiB",
                    "preview scope should state the display limit");
                Contains(
                    DiagnosticBundleService.PreviewScopeNotice,
                    "8 logs",
                    "preview scope should state the export log count limit");
                Contains(
                    DiagnosticBundleService.PreviewScopeNotice,
                    "512 KiB",
                    "preview scope should state the per-log export limit");
                Contains(
                    DiagnosticBundleService.PreviewScopeNotice,
                    "re-reads",
                    "preview scope should warn that export re-reads logs");
                var preview = await bundles.BuildPreviewAsync(diagnosis, 1, [minecraftLog], [token]);
                DoesNotContain(preview.Summary, token, "preview summary explicit secret");
                DoesNotContain(preview.LogExcerpt, token, "preview log explicit secret");
                if (!string.IsNullOrWhiteSpace(home))
                    DoesNotContainInsensitive(preview.LogExcerpt, home, "preview log home path");

                var archivePath = Path.Combine(tempDirectory, "diagnostic.zip");
                var result = await bundles.ExportAsync(diagnosis, 1, [minecraftLog], archivePath, [token]);
                Equal(1, result.IncludedLogs, "included log count");
                Equal(true, File.Exists(result.ArchivePath), "archive exists");

                string packageText;
                using (var archive = ZipFile.OpenRead(result.ArchivePath))
                {
                    Equal(true, archive.GetEntry("diagnosis.txt") is not null, "diagnosis entry");
                    Equal(true, archive.GetEntry("manifest.txt") is not null, "manifest entry");
                    Equal(true, archive.Entries.Any(entry => entry.FullName.StartsWith("logs/", StringComparison.Ordinal)), "log entry");

                    var combined = new System.Text.StringBuilder();
                    foreach (var entry in archive.Entries)
                    {
                        using var reader = new StreamReader(entry.Open());
                        combined.Append(await reader.ReadToEndAsync());
                    }
                    packageText = combined.ToString();
                }

                DoesNotContain(packageText, token, "archive explicit secret");
                DoesNotContain(packageText, "abcdefghijklmnop", "archive Bearer token");
                DoesNotContain(packageText, "zip-json-secret", "archive JSON access token");
                DoesNotContain(packageText, "zip-client-secret", "archive JSON client token");
                DoesNotContain(packageText, "zip-query-secret", "archive query token");
                DoesNotContain(packageText, "zip-form-secret", "archive form token");
                Contains(packageText, "&state=keep-state", "archive query state should remain");
                Contains(packageText, "&scope=keep-scope", "archive form scope should remain");
                Contains(packageText, "benign-tokenizer=keep-this-value", "archive benign token-like text should remain");
                if (!string.IsNullOrWhiteSpace(home))
                    DoesNotContainInsensitive(packageText, home, "archive home path");
                Contains(packageText, "<REDACTED>", "archive token redaction marker");
                Contains(packageText, "Export policy: at most 8 logs, 512 KiB tail per log.",
                    "archive manifest should disclose export limits");
                Contains(packageText, "Preview policy: preview is a smaller sanitized excerpt",
                    "archive manifest should distinguish preview from export");
                if (!string.IsNullOrWhiteSpace(home))
                    Contains(packageText, "<HOME>", "archive home marker");

                var previousArchive = await File.ReadAllBytesAsync(archivePath);
                using (var cancelled = new CancellationTokenSource())
                {
                    cancelled.Cancel();
                    try
                    {
                        _ = await bundles.ExportAsync(
                            diagnosis,
                            1,
                            [minecraftLog],
                            archivePath,
                            [token],
                            cancelled.Token);
                        throw new InvalidOperationException(
                            "Cancelled replacement export unexpectedly succeeded.");
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                EqualBytes(
                    previousArchive,
                    await File.ReadAllBytesAsync(archivePath),
                    "cancelled replacement must preserve the existing diagnostic ZIP");

                var overlapLog = Path.Combine(tempDirectory, "overlap.log");
                await File.WriteAllTextAsync(
                    overlapLog,
                    new string('x', 480 * 1024) + "\nAuthorization: Bearer overlap-secret-token\n");
                var firstExport = bundles.ExportAsync(
                    diagnosis,
                    1,
                    [overlapLog],
                    archivePath);
                var secondExport = bundles.ExportAsync(
                    diagnosis,
                    2,
                    [overlapLog],
                    archivePath);
                var overlapping = await Task.WhenAll(firstExport, secondExport);
                Equal(archivePath, overlapping[0].ArchivePath,
                    "first overlapping export destination");
                Equal(archivePath, overlapping[1].ArchivePath,
                    "second overlapping export destination");

                using (var finalArchive = ZipFile.OpenRead(archivePath))
                {
                    Equal(true, finalArchive.GetEntry("manifest.txt") is not null,
                        "overlapping replacement must leave a readable final ZIP");
                    Equal(true, finalArchive.Entries.Any(entry =>
                            entry.FullName.StartsWith("logs/", StringComparison.Ordinal)),
                        "overlapping replacement must retain log entries");
                }

                Equal(false,
                    Directory.EnumerateFiles(
                            tempDirectory,
                            Path.GetFileName(archivePath) + ".*.partial",
                            SearchOption.TopDirectoryOnly)
                        .Any(),
                    "overlapping exports must clean operation-specific partial files");
            }
            finally
            {
                try { Directory.Delete(tempDirectory, recursive: true); } catch { }
            }

            Console.WriteLine("PASS diagnostics regressions");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL diagnostics regressions: " + ex);
            return 1;
        }
    }

    private static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'.");
    }

    private static void Contains(string value, string expected, string message)
    {
        if (!value.Contains(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{message}: '{expected}' not found.");
    }

    private static void DoesNotContain(string value, string forbidden, string message)
    {
        if (value.Contains(forbidden, StringComparison.Ordinal))
            throw new InvalidOperationException($"{message}: forbidden text remained in output.");
    }

    private static void EqualBytes(
        byte[] expected,
        byte[] actual,
        string message)
    {
        if (!expected.AsSpan().SequenceEqual(actual))
            throw new InvalidOperationException(message);
    }

    private static void DoesNotContainInsensitive(string value, string forbidden, string message)
    {
        if (value.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{message}: forbidden text remained in output.");
    }
}
