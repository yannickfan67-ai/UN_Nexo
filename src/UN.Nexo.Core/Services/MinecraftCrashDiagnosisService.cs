using System.Text;
using System.Text.RegularExpressions;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class MinecraftCrashDiagnosisService
{
    private const int DefaultLogTailBytes = 512 * 1024;

    private static readonly Regex ExitCodeRegex = new(
        @"\[launcher\]\s+Exit code:\s*(?<code>-?\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex MissingSharedLibraryRegex = new(
        @"error while loading shared libraries:\s*(?<library>[^:\s]+):\s*cannot open shared object file",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TokenAssignmentRegex = new(
        @"(?im)(?<key>access[_-]?token|auth[_-]?(?:access[_-]?)?token|authorization|client[_-]?token|session(?:id)?)(?<sep>\s*[:=]\s*|\s+)(?<value>[^\s""']{8,})",
        RegexOptions.Compiled);

    private static readonly Regex AccessTokenArgumentRegex = new(
        @"(?im)(?<key>--accessToken\s+)(?<value>\S+)",
        RegexOptions.Compiled);

    private static readonly Regex BearerRegex = new(
        @"(?im)(?<key>Bearer\s+)(?<value>[A-Za-z0-9._~+/-]{8,}=*)",
        RegexOptions.Compiled);

    public CrashDiagnosis Analyze(
        int? exitCode,
        string? logText,
        string? launchError = null,
        bool wasStopped = false)
    {
        if (wasStopped)
        {
            return new CrashDiagnosis(
                "stopped",
                "Game stopped",
                CrashDiagnosisLevel.Info,
                "The Minecraft process was stopped by the launcher or user request.",
                "The stop was intentional; a non-zero process code caused by termination is not treated as a crash.",
                "Start the instance again when you are ready.",
                true);
        }

        var combined = string.Join('\n', new[] { launchError, logText }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

        if (exitCode == 0 && string.IsNullOrWhiteSpace(launchError))
        {
            return new CrashDiagnosis(
                "normal-exit",
                "Minecraft exited normally",
                CrashDiagnosisLevel.Info,
                "The Java process returned exit code 0.",
                EvidenceFor(combined, "Exit code: 0"),
                "No action is required.",
                true);
        }

        if (ContainsAny(combined,
                "UnsupportedClassVersionError",
                "has been compiled by a more recent version of the Java Runtime",
                "only recognizes class file versions up to"))
        {
            return Known(
                "java-version-mismatch",
                "Java version mismatch",
                "Minecraft, its loader, or one of its libraries requires a different Java major version.",
                EvidenceForAny(combined, "UnsupportedClassVersionError", "class file version", "more recent version of the Java Runtime"),
                "Run Repair so Nexo can verify the instance Java requirement and reacquire the matching managed runtime if needed.");
        }

        if (ContainsAny(combined,
                "java.lang.OutOfMemoryError: Java heap space",
                "java.lang.OutOfMemoryError: GC overhead limit exceeded",
                "Requested array size exceeds VM limit"))
        {
            return Known(
                "java-heap-oom",
                "Minecraft ran out of Java heap",
                "The game exhausted the heap available to Java.",
                EvidenceForAny(combined, "OutOfMemoryError", "GC overhead limit exceeded"),
                "Increase the game memory limit moderately in Runtime settings, then retry. Avoid assigning nearly all system RAM.");
        }

        if (ContainsAny(combined,
                "Could not reserve enough space for object heap",
                "There is insufficient memory for the Java Runtime Environment",
                "Native memory allocation (malloc) failed"))
        {
            return Known(
                "jvm-memory-reservation",
                "Java could not reserve enough memory",
                "The configured heap or JVM native-memory requirement could not be reserved.",
                EvidenceForAny(combined, "Could not reserve enough space", "insufficient memory", "Native memory allocation"),
                "Reduce the configured memory limit or close other memory-heavy programs, then retry.");
        }

        var missingSharedLibrary = MissingSharedLibraryRegex.Match(combined);
        if (missingSharedLibrary.Success)
        {
            var library = missingSharedLibrary.Groups["library"].Value;
            return Known(
                "linux-system-library-missing",
                $"Linux system library missing: {library}",
                "The operating system loader could not find a shared library required by Java, LWJGL, or another native component.",
                Clip(missingSharedLibrary.Value.Trim()),
                $"Install the package that provides {library}, then retry. On Debian/Linux Mint, `apt-file search {library}` can identify the package if it is not obvious.");
        }

        if (ContainsAny(combined,
                "java.lang.UnsatisfiedLinkError",
                "no lwjgl in java.library.path",
                "no lwjgl64 in java.library.path",
                "Can't find dependent libraries",
                "Unable to load library"))
        {
            return Known(
                "native-library-failure",
                "A native library could not be loaded",
                "Minecraft or LWJGL failed while loading native code.",
                EvidenceForAny(combined, "UnsatisfiedLinkError", "java.library.path", "dependent libraries", "Unable to load library"),
                "Run Repair to verify native archives and extracted natives. If the evidence names an OS library, install that system dependency too.");
        }

        if (ContainsAny(combined,
                "NoClassDefFoundError",
                "ClassNotFoundException"))
        {
            return Known(
                "missing-java-class",
                "A required Java class is missing",
                "The launch classpath appears incomplete or contains a damaged or incompatible library.",
                EvidenceForAny(combined, "NoClassDefFoundError", "ClassNotFoundException"),
                "Run Repair to verify the client and libraries. For modded instances, also verify the loader and mod dependency set.");
        }

        if (ContainsAny(combined,
                "Pixel format not accelerated",
                "Failed to create display",
                "Failed to create window",
                "Could not create context",
                "GLFW error",
                "OpenGL is not supported",
                "OpenGL version string: null",
                "No OpenGL context found"))
        {
            return Known(
                "graphics-initialization",
                "Graphics initialization failed",
                "Minecraft/LWJGL could not create a usable OpenGL window or context.",
                EvidenceForAny(combined, "Pixel format not accelerated", "Failed to create display", "Failed to create window", "Could not create context", "GLFW error", "OpenGL is not supported", "No OpenGL context found"),
                "Check the GPU driver and OpenGL support for this Minecraft version. Remote or headless sessions may also lack a usable graphics context.");
        }

        if (ContainsAny(combined,
                "EXCEPTION_ACCESS_VIOLATION",
                "A fatal error has been detected by the Java Runtime Environment",
                "Problematic frame:"))
        {
            return Known(
                "native-jvm-crash",
                "Java or native code crashed",
                "The JVM reported a native crash rather than a normal Java exception.",
                EvidenceForAny(combined, "EXCEPTION_ACCESS_VIOLATION", "fatal error has been detected", "Problematic frame:"),
                "Check the generated hs_err log, GPU/audio drivers and native libraries. Run Repair first to rule out damaged instance files.");
        }

        if (!string.IsNullOrWhiteSpace(launchError))
        {
            return new CrashDiagnosis(
                "launcher-error",
                "Launch failed before Minecraft completed startup",
                CrashDiagnosisLevel.Error,
                "Nexo received an exception while preparing or starting the game.",
                Clip(launchError),
                "Use the evidence together with Repair and the launch trace. If the cause is not clear, export a sanitized diagnostic package.",
                false);
        }

        return new CrashDiagnosis(
            "unknown-nonzero-exit",
            exitCode is null ? "Minecraft ended unexpectedly" : $"Minecraft exited with code {exitCode}",
            CrashDiagnosisLevel.Error,
            "The retained log does not match a cause that Nexo can identify reliably.",
            LastMeaningfulLines(combined),
            "Do not infer the cause from the exit code alone. Run Repair, then export the sanitized diagnostic package if the failure repeats.",
            false);
    }

    public async Task<CrashDiagnosis> AnalyzeFileAsync(
        int? exitCode,
        string? logPath,
        string? launchError = null,
        bool wasStopped = false,
        CancellationToken cancellationToken = default)
    {
        var tail = string.Empty;
        if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
            tail = await ReadLogTailAsync(logPath, DefaultLogTailBytes, cancellationToken);
        return Analyze(exitCode, tail, launchError, wasStopped);
    }

    public int? TryReadExitCode(string? logText)
    {
        if (string.IsNullOrWhiteSpace(logText))
            return null;
        var matches = ExitCodeRegex.Matches(logText);
        if (matches.Count == 0)
            return null;
        return int.TryParse(matches[^1].Groups["code"].Value, out var code) ? code : null;
    }

    public string Sanitize(string? text, params string?[] secrets)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var result = text;
        result = AccessTokenArgumentRegex.Replace(result, match => match.Groups["key"].Value + "<REDACTED>");
        result = BearerRegex.Replace(result, match => match.Groups["key"].Value + "<REDACTED>");
        result = TokenAssignmentRegex.Replace(result, match =>
            match.Groups["key"].Value + match.Groups["sep"].Value + "<REDACTED>");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
            result = ReplaceInsensitive(result, home, "<HOME>");

        foreach (var secret in secrets.Where(value => !string.IsNullOrWhiteSpace(value)))
            result = ReplaceInsensitive(result, secret!, "<REDACTED>");

        return result;
    }

    public string BuildSummary(CrashDiagnosis diagnosis)
        => $"{diagnosis.Title}\n\n{diagnosis.Summary}\n\nEvidence:\n{diagnosis.Evidence}\n\nSuggested action:\n{diagnosis.SuggestedAction}";

    private static async Task<string> ReadLogTailAsync(string path, int maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        var start = Math.Max(0L, stream.Length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);
        var bytesToRead = checked((int)(stream.Length - start));
        var buffer = new byte[bytesToRead];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken);
            if (count == 0)
                break;
            read += count;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, read);
        if (start > 0)
        {
            var firstNewLine = text.IndexOf('\n');
            if (firstNewLine >= 0 && firstNewLine + 1 < text.Length)
                text = text[(firstNewLine + 1)..];
        }
        return text;
    }

    private static CrashDiagnosis Known(
        string code,
        string title,
        string summary,
        string evidence,
        string suggestedAction)
        => new(code, title, CrashDiagnosisLevel.Error, summary, evidence, suggestedAction, true);

    private static bool ContainsAny(string text, params string[] needles)
        => needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string EvidenceForAny(string text, params string[] needles)
    {
        foreach (var needle in needles)
        {
            var evidence = EvidenceFor(text, needle);
            if (!string.Equals(evidence, "No matching log line was retained.", StringComparison.Ordinal))
                return evidence;
        }
        return LastMeaningfulLines(text);
    }

    private static string EvidenceFor(string text, string needle)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "No matching log line was retained.";

        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Contains(needle, StringComparison.OrdinalIgnoreCase))
                return Clip(line.Trim());
        }
        return "No matching log line was retained.";
    }

    private static string LastMeaningfulLines(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "No log output was retained.";

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !IsNoiseLine(line))
            .TakeLast(4)
            .ToArray();
        if (lines.Length == 0)
            return "No non-debug error line was retained.";
        return Clip(string.Join(Environment.NewLine, lines), 1200);
    }

    private static bool IsNoiseLine(string line)
        => line.StartsWith("[debug]", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("[trace]", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("[launcher] Exit code:", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("[launcher] Session started:", StringComparison.OrdinalIgnoreCase);

    private static string Clip(string value, int max = 600)
        => value.Length <= max ? value : value[..max] + "…";

    private static string ReplaceInsensitive(string input, string value, string replacement)
    {
        if (string.IsNullOrEmpty(value))
            return input;
        var start = 0;
        while (true)
        {
            var index = input.IndexOf(value, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return input;
            input = input[..index] + replacement + input[(index + value.Length)..];
            start = index + replacement.Length;
        }
    }
}
