using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class ModRecommendationRegression
{
    internal static async Task RunAsync()
    {
        var provider = new FakeRecommendationProvider();
        var installed = new[]
        {
            new InstalledMod(
                "installed.jar",
                true,
                1,
                DateTimeOffset.UtcNow)
        };

        var result = await new ModRecommendationService(provider).GetAsync(
            "/tmp/mods",
            installed,
            "1.21.4",
            "fabric",
            limit: 2);

        Assert(result.Count == 2,
            "Recommendation service should fill the requested result count after exclusions.");
        Assert(
            result.Select(item => item.Project.ProjectId)
                .SequenceEqual(["new-a", "new-c"]),
            "Installed and duplicate projects must be excluded while preserving provider order.");
        Assert(provider.LastMinecraftVersion == "1.21.4",
            "Recommendation service must forward the selected Minecraft version.");
        Assert(provider.LastLoader == "fabric",
            "Recommendation service must forward the selected loader.");
        Assert(provider.LastLimit >= 2,
            "Recommendation service may over-fetch to replace installed suggestions.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class FakeRecommendationProvider :
        IModRecommendationProvider
    {
        public string ProviderId => "fake";
        public string DisplayName => "Fake";
        public string LastMinecraftVersion { get; private set; } = string.Empty;
        public string LastLoader { get; private set; } = string.Empty;
        public int LastLimit { get; private set; }

        public Task<IReadOnlyList<ModProviderRecommendation>> RecommendAsync(
            string minecraftVersion,
            string loader,
            int limit = 20,
            CancellationToken cancellationToken = default)
        {
            LastMinecraftVersion = minecraftVersion;
            LastLoader = loader;
            LastLimit = limit;

            return Task.FromResult<IReadOnlyList<ModProviderRecommendation>>(
            [
                Recommendation("new-a"),
                Recommendation("installed"),
                Recommendation("new-a"),
                Recommendation("new-c")
            ]);
        }

        public Task<IReadOnlyDictionary<string, ModProviderInstalledMatch>>
            MatchInstalledAsync(
                string modsDirectory,
                IReadOnlyList<InstalledMod> installedMods,
                CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, ModProviderInstalledMatch>>(
                new Dictionary<string, ModProviderInstalledMatch>
                {
                    ["installed"] = new(
                        ProviderId,
                        "installed",
                        "v1",
                        "1.0",
                        "installed.jar",
                        true)
                });

        public Task<IReadOnlyList<ModProviderProject>> SearchAsync(
            string query,
            string minecraftVersion,
            string loader,
            int limit = 20,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ModProviderVersion?> GetLatestCompatibleVersionAsync(
            string projectId,
            string minecraftVersion,
            string loader,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ModProviderInstallResult> InstallAsync(
            string instanceId,
            ModProviderProject project,
            ModProviderVersion version,
            ModProviderInstalledMatch? existing,
            InstanceModService modService,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        private static ModProviderRecommendation Recommendation(
            string id)
            => new(
                new ModProviderProject(
                    "fake",
                    id,
                    id,
                    id,
                    "test",
                    "tester",
                    null,
                    1,
                    "https://example.invalid/" + id),
                "Test signal");
    }
}
