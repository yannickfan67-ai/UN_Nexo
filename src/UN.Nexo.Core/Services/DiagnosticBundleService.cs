using System.IO.Compression;
using System.Text;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class DiagnosticBundleService
{
    private const int MaxSourceLogs = 8;
    private const int ExportTailBytes = 512 * 1024;
    private const int PreviewTailBytes = 64 * 1024;
    private const int PreviewTextLimit = 12 * 1024;

    private readonly MinecraftCrashDiagnosisService _diagnosis;

    public DiagnosticBundleService(MinecraftCrashDiagnosisService? diagnosis = null)
    {
        _diagnosis = diagnosis ?? new MinecraftCrashDiagnosisService();
    }

    public async Task<DiagnosticPreview> BuildPreviewAsync(
        CrashDiagnosis diagnosis,
        int? exitCode,
        IEnumerable<string?> sourceLogPaths,
        IEnumerable<string?>? secrets = null,
        CancellationToken cancellationToken = default)
    {
        var secretValues = NormalizeSecrets(secrets);
        var logs = NormalizeSourceLogs(sourceLogPaths);
        var preview = new StringBuilder();

        foreach (var path in logs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (preview.Length >= PreviewTextLimit)
                break;

            try
            {
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
        string archivePath,
        IEnumerable<string?>? secrets = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var secretValues = NormalizeSecrets(secrets);
        var logs = NormalizeSourceLogs(sourceLogPaths);
        var sanitizedDiagnosis = SanitizeDiagnosis(diagnosis, secretValues);
        var createdAt = DateTimeOffset.Now;

        var fullArchivePath = Path.GetFullPath(archivePath);
        var directory = Path.GetDirectoryName(fullArchivePath)
            ?? throw new InvalidOperationException("The diagnostic archive path has no parent directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = fullArchivePath + ".partial";
        try
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);

            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.ReadWrite,
                             FileShare.None,
                             64 * 1024,
                             useAsync: true))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                await WriteTextEntryAsync(
                    archive,
                    "diagnosis.txt",
                    _diagnosis.Sanitize(_diagnosis.BuildSummary(sanitizedDiagnosis), secretValues),
                    cancellationToken);

                var manifest = new StringBuilder()
                    .AppendLine("UN_Nexo sanitized diagnostic package")
                    .AppendLine($"Created: {createdAt:O}")
                    .AppendLine($"Exit code: {(exitCode?.ToString() ?? "unknown")}")
                    .AppendLine($"Diagnosis code: {sanitizedDiagnosis.Code}")
                    .AppendLine($"Included log count: {logs.Count}")
                    .AppendLine()
                    .AppendLine("Source logs (paths sanitized):");
                foreach (var path in logs)
                    manifest.AppendLine("- " + _diagnosis.Sanitize(path, secretValues));
                manifest.AppendLine()
                    .AppendLine("Access tokens, explicit secrets, and the current user home path are redacted before files are written to this archive.");

                await WriteTextEntryAsync(archive, "manifest.txt", manifest.ToString(), cancellationToken);

                for (var index = 0; index < logs.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = logs[index];
                    string text;
                    try
                    {
                        text = await _diagnosis.ReadLogTailAsync(path, ExportTailBytes, cancellationToken);
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
                    var entryName = $"logs/{index + 1:D2}-{SafeEntryName(Path.GetFileName(path))}";
                    await WriteTextEntryAsync(archive, entryName, text, cancellationToken);
                }
            }

            if (File.Exists(fullArchivePath))
                File.Delete(fullArchivePath);
            File.Move(temporaryPath, fullArchivePath);
            return new DiagnosticExportResult(fullArchivePath, logs.Count, createdAt);
        }
        catch
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
            throw;
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

    private static List<string> NormalizeSourceLogs(IEnumerable<string?> sourceLogPaths)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        foreach (var candidate in sourceLogPaths)
        {
            if (result.Count >= MaxSourceLogs || string.IsNullOrWhiteSpace(candidate))
                continue;

            try
            {
                var full = Path.GetFullPath(candidate);
                if (!File.Exists(full) || !seen.Add(full))
                    continue;
                result.Add(full);
            }
            catch
            {
                // Ignore invalid or inaccessible path syntax; callers still get a useful package.
            }
        }

        return result;
    }

    private static string?[] NormalizeSecrets(IEnumerable<string?>? secrets)
        => secrets?
            .Where(value => !string.IsNullOrWhiteSpace(value))
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
        await using var writer = new StreamWriter(entryStream, new UTF8Encoding(false), leaveOpen: false);
        await writer.WriteAsync(text.AsMemory(), cancellationToken);
    }
}
