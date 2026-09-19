using System.IO.Compression;
using System.Text;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class DiagnosticBundleService
{
    public const int MaxSourceLogs = 8;
    public const int ExportTailBytes = 512 * 1024;
    public const int PreviewTailBytes = 64 * 1024;
    public const int PreviewTextLimit = 12 * 1024;

    public static string PreviewScopeNotice =>
        "Preview is a sanitized excerpt, not the complete ZIP contents. "
        + $"Preview reads at most {PreviewTailBytes / 1024} KiB from each source log "
        + $"and displays at most {PreviewTextLimit / 1024} KiB total. "
        + $"Export may include up to {MaxSourceLogs} logs and up to {ExportTailBytes / 1024} KiB from each. "
        + "Export re-reads the selected logs, so content written after preview may also be included.";

    private readonly MinecraftCrashDiagnosisService _diagnosis;

    public DiagnosticBundleService(MinecraftCrashDiagnosisService? diagnosis = null)
    {
        _diagnosis = diagnosis ?? new MinecraftCrashDiagnosisService();
    }

    public async Task<DiagnosticPreview> BuildPreviewAsync(
        CrashDiagnosis diagnosis,
        int? exitCode,
        IEnumerable<string?> sourceLogPaths,
        IEnumerable<string?> allowedLogRoots,
        IEnumerable<string?>? secrets = null,
        CancellationToken cancellationToken = default)
    {
        var secretValues = NormalizeSecrets(secrets);
        var allowedRoots = NormalizeAllowedRoots(allowedLogRoots);
        var logs = NormalizeSourceLogs(sourceLogPaths, allowedRoots);
        var preview = new StringBuilder();

        foreach (var path in logs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (preview.Length >= PreviewTextLimit)
                break;

            try
            {
                EnsureAuthorizedSourceLog(path, allowedRoots);
                var tail = await _diagnosis.ReadLogTailAsync(path, PreviewTailBytes, cancellationToken);
                var sanitized = _diagnosis.Sanitize(tail, secretValues);
                preview.AppendLine($"--- {Path.GetFileName(path)} ---");
                preview.AppendLine(sanitized);
            }
            catch (IOException ex)
            {
                preview.AppendLine($"--- {Path.GetFileName(path)} ---");
                preview.AppendLine($"<could not read log: {ex.Message}>");
            }
            catch (UnauthorizedAccessException ex)
            {
                preview.AppendLine($"--- {Path.GetFileName(path)} ---");
                preview.AppendLine($"<could not read log: {ex.Message}>");
            }
        }

        var previewText = preview.ToString();
        if (previewText.Length > PreviewTextLimit)
            previewText = previewText[..PreviewTextLimit] + Environment.NewLine + "<preview truncated>";

        return new DiagnosticPreview(
            SanitizeDiagnosis(diagnosis, secretValues),
            _diagnosis.Sanitize(_diagnosis.BuildSummary(diagnosis), secretValues),
            previewText,
            logs.Select(path => _diagnosis.Sanitize(path, secretValues)).ToArray(),
            exitCode,
            DateTimeOffset.Now);
    }

    public async Task<DiagnosticExportResult> ExportAsync(
        CrashDiagnosis diagnosis,
        int? exitCode,
        IEnumerable<string?> sourceLogPaths,
        IEnumerable<string?> allowedLogRoots,
        string archivePath,
        IEnumerable<string?>? secrets = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var secretValues = NormalizeSecrets(secrets);
        var allowedRoots = NormalizeAllowedRoots(allowedLogRoots);
        var logs = NormalizeSourceLogs(sourceLogPaths, allowedRoots);
        var sanitizedDiagnosis = SanitizeDiagnosis(diagnosis, secretValues);
        var createdAt = DateTimeOffset.Now;

        var fullArchivePath = Path.GetFullPath(archivePath);
        var directory = Path.GetDirectoryName(fullArchivePath)
            ?? throw new InvalidOperationException("The diagnostic archive path has no parent directory.");
        Directory.CreateDirectory(directory);

        using var publishLease = await PathKeyedLock.AcquireAsync(
            fullArchivePath,
            cancellationToken);
        var temporaryPath =
            fullArchivePath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using (var archive = new ZipArchive(
                           stream,
                           ZipArchiveMode.Create,
                           leaveOpen: true))
                {
                    await WriteTextEntryAsync(
                        archive,
                        "diagnosis.txt",
                        _diagnosis.Sanitize(
                            _diagnosis.BuildSummary(sanitizedDiagnosis),
                            secretValues),
                        cancellationToken);

                    var manifest = new StringBuilder()
                        .AppendLine("UN_Nexo sanitized diagnostic package")
                        .AppendLine($"Created: {createdAt:O}")
                        .AppendLine($"Exit code: {(exitCode?.ToString() ?? "unknown")}")
                        .AppendLine($"Diagnosis code: {sanitizedDiagnosis.Code}")
                        .AppendLine($"Included log count: {logs.Count}")
                        .AppendLine($"Export policy: at most {MaxSourceLogs} logs, {ExportTailBytes / 1024} KiB tail per log.")
                        .AppendLine("Preview policy: preview is a smaller sanitized excerpt and export re-reads source logs.")
                        .AppendLine()
                        .AppendLine("Source logs (paths sanitized):");
                    foreach (var path in logs)
                        manifest.AppendLine("- " + _diagnosis.Sanitize(path, secretValues));
                    manifest.AppendLine()
                        .AppendLine("Access tokens, explicit secrets, and the current user home path are redacted before files are written to this archive.");

                    await WriteTextEntryAsync(
                        archive,
                        "manifest.txt",
                        manifest.ToString(),
                        cancellationToken);

                    for (var index = 0; index < logs.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var path = logs[index];
                        string text;
                        try
                        {
                            EnsureAuthorizedSourceLog(path, allowedRoots);
                            text = await _diagnosis.ReadLogTailAsync(
                                path,
                                ExportTailBytes,
                                cancellationToken);
                        }
                        catch (IOException ex)
                        {
                            text = $"<could not read log: {ex.Message}>";
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            text = $"<could not read log: {ex.Message}>";
                        }

                        text = _diagnosis.Sanitize(text, secretValues);
                        var entryName =
                            $"logs/{index + 1:D2}-{SafeEntryName(Path.GetFileName(path))}";
                        await WriteTextEntryAsync(
                            archive,
                            entryName,
                            text,
                            cancellationToken);
                    }
                }

                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullArchivePath, overwrite: true);
            return new DiagnosticExportResult(
                fullArchivePath,
                logs.Count,
                createdAt);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // Best-effort cleanup; never delete the existing destination.
            }
        }
    }

    private CrashDiagnosis SanitizeDiagnosis(CrashDiagnosis diagnosis, string?[] secrets)
        => diagnosis with
        {
            Title = _diagnosis.Sanitize(diagnosis.Title, secrets),
            Summary = _diagnosis.Sanitize(diagnosis.Summary, secrets),
            Evidence = _diagnosis.Sanitize(diagnosis.Evidence, secrets),
            SuggestedAction = _diagnosis.Sanitize(diagnosis.SuggestedAction, secrets)
        };

    private static List<string> NormalizeSourceLogs(
        IEnumerable<string?> sourceLogPaths,
        IReadOnlyList<string> allowedRoots)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(PathComparer());

        foreach (var candidate in sourceLogPaths)
        {
            if (result.Count >= MaxSourceLogs || string.IsNullOrWhiteSpace(candidate))
                continue;

            try
            {
                var full = Path.GetFullPath(candidate);
                EnsureAuthorizedSourceLog(full, allowedRoots);
                if (seen.Add(full))
                    result.Add(full);
            }
            catch (Exception ex) when (
                ex is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or NotSupportedException)
            {
                // Ignore invalid, linked, outside-root or inaccessible candidates.
                // Callers still get a useful bounded diagnostic package from healthy logs.
            }
        }

        return result;
    }

    private static IReadOnlyList<string> NormalizeAllowedRoots(
        IEnumerable<string?> allowedLogRoots)
    {
        ArgumentNullException.ThrowIfNull(allowedLogRoots);

        var roots = new List<string>();
        var seen = new HashSet<string>(PathComparer());
        foreach (var candidate in allowedLogRoots)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;

            var full = Path.GetFullPath(candidate);
            if (seen.Add(full))
                roots.Add(full);
        }

        if (roots.Count == 0)
            throw new ArgumentException(
                "At least one diagnostic log root is required.",
                nameof(allowedLogRoots));

        return roots;
    }

    private static void EnsureAuthorizedSourceLog(
        string sourcePath,
        IReadOnlyList<string> allowedRoots)
    {
        var full = Path.GetFullPath(sourcePath);
        if (!File.Exists(full))
            throw new IOException("Diagnostic source log no longer exists.");

        var root = allowedRoots
            .Where(candidate => IsContainedByRoot(full, candidate))
            .OrderByDescending(candidate => candidate.Length)
            .FirstOrDefault();
        if (root is null)
            throw new UnauthorizedAccessException(
                "Diagnostic source log is outside the allowed log roots.");

        RejectLinkedPathComponents(full);
    }

    private static bool IsContainedByRoot(string path, string root)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullPath = Path.GetFullPath(path);
        if (fullRoot.Length == 0)
            return false;

        var prefix = fullRoot + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, PathComparison());
    }

    private static void RejectLinkedPathComponents(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException(
                        "Linked or reparse-point paths cannot be used as diagnostic log sources.");
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)
                || string.Equals(parent, current, PathComparison()))
                break;
            current = parent;
        }
    }

    private static StringComparer PathComparer()
        => OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static StringComparison PathComparison()
        => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string?[] NormalizeSecrets(IEnumerable<string?>? secrets)
        => secrets?
            .Where(value => !string.IsNullOrWhiteSpace(value) && value!.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .Take(32)
            .ToArray()
           ?? [];

    private static string SafeEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "log.txt";
        var invalid = Path.GetInvalidFileNameChars();
        var safe = string.Concat(name.Select(character => invalid.Contains(character) ? '_' : character));
        return safe.Length > 120 ? safe[..120] : safe;
    }

    private static async Task WriteTextEntryAsync(
        ZipArchive archive,
        string entryName,
        string text,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, new UTF8Encoding(false), 1024, leaveOpen: false);
        await writer.WriteAsync(text.AsMemory(), cancellationToken);
    }
}
