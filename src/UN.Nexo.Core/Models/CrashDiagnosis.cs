namespace UN.Nexo.Core.Models;

public enum CrashDiagnosisLevel
{
    Info,
    Warning,
    Error
}

public sealed record CrashDiagnosis(
    string Code,
    string Title,
    CrashDiagnosisLevel Level,
    string Summary,
    string Evidence,
    string SuggestedAction,
    bool IsKnownCause);
