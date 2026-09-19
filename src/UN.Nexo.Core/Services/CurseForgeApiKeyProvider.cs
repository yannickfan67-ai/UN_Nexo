namespace UN.Nexo.Core.Services;

public interface ICurseForgeApiKeyProvider
{
    bool IsConfigured { get; }

    ValueTask<string> GetApiKeyAsync(
        CancellationToken cancellationToken = default);
}

public sealed class EnvironmentCurseForgeApiKeyProvider : ICurseForgeApiKeyProvider
{
    public const string EnvironmentVariableName =
        "UN_NEXO_CURSEFORGE_API_KEY";

    public bool IsConfigured
        => TryNormalize(
            Environment.GetEnvironmentVariable(EnvironmentVariableName),
            out _);

    public ValueTask<string> GetApiKeyAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryNormalize(
                Environment.GetEnvironmentVariable(EnvironmentVariableName),
                out var apiKey))
        {
            throw new InvalidOperationException(
                $"CurseForge is not configured. Set {EnvironmentVariableName} for this process/session. UN_Nexo does not embed or persist a CurseForge API key.");
        }

        return ValueTask.FromResult(apiKey);
    }

    private static bool TryNormalize(
        string? value,
        out string normalized)
    {
        normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 8 or > 4096)
            return false;

        return !normalized.Any(character =>
            char.IsControl(character)
            || char.IsWhiteSpace(character));
    }
}
