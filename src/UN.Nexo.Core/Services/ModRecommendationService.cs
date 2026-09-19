using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class ModRecommendationService(
    IModRecommendationProvider provider)
{
    private readonly IModRecommendationProvider _provider =
        provider ?? throw new ArgumentNullException(nameof(provider));

    public async Task<IReadOnlyList<ModProviderRecommendation>> GetAsync(
        string modsDirectory,
        IReadOnlyList<InstalledMod> installedMods,
        string minecraftVersion,
        string loader,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installedMods);
        if (string.IsNullOrWhiteSpace(modsDirectory))
            throw new ArgumentException(
                "A mods directory is required.",
                nameof(modsDirectory));
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "Recommendation limit must be between 1 and 100.");

        var fetchLimit = Math.Min(
            100,
            Math.Max(limit, limit * 2));
        var recommendations = await _provider.RecommendAsync(
            minecraftVersion,
            loader,
            fetchLimit,
            cancellationToken);
        var installed = await _provider.MatchInstalledAsync(
            modsDirectory,
            installedMods,
            cancellationToken);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ModProviderRecommendation>(limit);
        foreach (var recommendation in recommendations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(
                    recommendation.Project.ProviderId,
                    _provider.ProviderId,
                    StringComparison.OrdinalIgnoreCase)
                || installed.ContainsKey(
                    recommendation.Project.ProjectId)
                || !seen.Add(
                    recommendation.Project.ProjectId))
            {
                continue;
            }

            result.Add(recommendation);
            if (result.Count == limit)
                break;
        }

        return result;
    }
}
