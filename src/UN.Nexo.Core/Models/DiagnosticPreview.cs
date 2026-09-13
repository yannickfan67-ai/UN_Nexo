namespace UN.Nexo.Core.Models;

public sealed record DiagnosticPreview(
    CrashDiagnosis Diagnosis,
    string Summary,
    string LogExcerpt,
    IReadOnlyList<string> SourceLogs,
    int? ExitCode,
    DateTimeOffset CreatedAt);

public sealed record DiagnosticExportResult(
    string ArchivePath,
    int IncludedLogs,
    DateTimeOffset CreatedAt);
