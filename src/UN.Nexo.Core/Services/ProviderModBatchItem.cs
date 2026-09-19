namespace UN.Nexo.Core.Services;

internal sealed record ProviderModBatchItem(
    string SourcePath,
    string? ExistingFileName,
    bool ExistingIsEnabled);
