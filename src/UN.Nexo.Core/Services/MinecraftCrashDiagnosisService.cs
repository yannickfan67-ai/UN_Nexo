using System.Text.RegularExpressions;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class MinecraftCrashDiagnosisService
{
    private static readonly Regex ExitCodeRegex = new(
        @"\[launcher\]\s+Exit code:\s*(?<code>-?\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TokenAssignmentRegex = new(
        @"(?im)(?<key>access[_-]?token|auth[_-]?(?:access[_-]?)?token|authorization|client[_-]?token|session(?:id)?)(?<sep>\s*[:=]\s*|\s+)(?<value>[^\s\"']{8,})",
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
                "No crash diagnosis is needed for an intentional stop.",
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
                "class file version"))
        {
            return Known(
                "java-version-mismatch",
                "Java version mismatch",
                "Minecraft or one of its libraries was started with an incompatible Java major version.",
                EvidenceForAny(combined, "UnsupportedClassVersionError", "class file version", "more recent version of the Java Runtime"),
                "Run Repair so Nexo can verify the instance Java requirement and reacquire the matching managed runtime if needed.");
        }

        if (ContainsAny(combined,
                "java.lang.OutOfMemoryError: Java heap space",
                "GC overhead limit exceeded",
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
                "Could not create the Java Virtual Machine",
                "There is insufficient memory for the Java Runtime Environment"))
        {
            return Known(
                "jvm-memory-reservation",
                "Java could not reserve memory",
                "The configured Java heap or native-memory requirement could not be reserved.",
                EvidenceForAny(combined, "Could not reserve enough space", "insufficient memory", "Could not create the Java Virtual Machine"),
                "Reduce the configured memory limit or close other memory-heavy programs, then retry.");
        }

        if (ContainsAny(combined, "libXtst.so.6", "libX11.so", "libXi.so", "libXrandr.so"))
        {
            return Known(
                "linux-system-library-missing",
                "A Linux system library is missing",
                "Java/LWJGL could not load a required desktop system library.",
                EvidenceForAny(combined, "libXtst.so.6", "libX11.so", "libXi.so", "libXrandr.so"),
                "Install the packaged UN_Nexo DEB/RPM dependencies or install the named system library, then retry. The current Debian/Mint package declares libXtst.");
        }

        if (ContainsAny(combined,
                "java.lang.UnsatisfiedLinkError",
                "no lwjgl in java.library.path",
                "no lwjgl64 in java.library.path",
                "Native Library"))
        {
            return Known(
                "native-library-failure",
                "A native library could not be loaded",
                "Minecraft or LWJGL failed while loading native code.",
                EvidenceForAny(combined, "UnsatisfiedLinkError", "java.library.path", "Native Library"),
                "Run Repair to verify native archives and extracted natives. If the error names an OS library, install that system dependency too.");
        }

        if (ContainsAny(combined,
                "NoClassDefFoundError",
                "ClassNotFoundException"))
        {
            return Known(
                "missing-java-class",
                "A required Java class is missing",
                "The launch classpath appears incomplete or contains a damaged library.",
                EvidenceForAny(combined, "NoClassDefFoundError", "ClassNotFoundException"),
                "Run Repair to verify the client and libraries. For modded instances, also verify the loader and mod dependency set.");
        }

        if (ContainsAny(combined,
                "Pixel format not accelerated",
                "Failed to create display",
                "Could not create context",
                "GLFW error",
                "OpenGL is not supported",
                "OpenGL version string: null"))
        {
            return Known(
                "graphics-initialization",
                "Graphics initialization failed",
                "Minecraft/LWJGL could not create a usable OpenGL window or context.",
                EvidenceForAny(combined, "Pixel format not accelerated", "Failed to create display", "Could not create context", "GLFW error", "OpenGL is not supported"),
                "Check the GPU driver and OpenGL support for this Minecraft version. Remote/headless sessions may also lack a usable graphics context.");
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
                "Use the evidence below together with Repair and the launch trace. If the cause is not clear, export a sanitized diagnostic package.",
                false);
        }

        return new CrashDiagnosis(
            "unknown-nonzero-exit",
            exitCode is null ? "Minecraft ended unexpectedly" : $"Minecraft exited with code {exitCode}",
            CrashDiagnosisLevel.Error,
            "The available log does not match a diagnosis rule that Nexo can identify reliably.",
            LastMeaningfulLines(combined),
            "Do not guess from the exit code alone. Run Repair, then export the sanitized diagnostic package if the failure repeats.",
            false);
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
            .Where(line => line.Length > 0)
            .TakeLast(4);
        return Clip(string.Join(Environment.NewLine, lines), 1200);
    }

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
