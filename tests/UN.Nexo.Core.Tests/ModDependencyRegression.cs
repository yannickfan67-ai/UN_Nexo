using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class ModDependencyRegression
{
    internal static async Task RunAsync()
    {
        await TestRecursivePlanAndVersionOnlyEdgeAsync();
        await TestCycleAndConflictRejectionAsync();
        await TestAtomicBatchRollbackAsync();
    }

    private static async Task TestRecursivePlanAndVersionOnlyEdgeAsync()
    {
        var provider = new FakeProvider();
        var c = provider.Add("C", "C1", []);
        var b = provider.Add(
            "B",
            "B1",
            [
                new ModProviderDependency(
                    null,
                    c.Version.VersionId,
                    ModProviderDependencyType.Required),
                new ModProviderDependency(
                    "D",
                    null,
                    ModProviderDependencyType.Optional)
            ]);
        var a = provider.Add(
            "A",
            "A1",
            [
                new ModProviderDependency(
                    b.Project.ProjectId,
                    null,
                    ModProviderDependencyType.Required)
            ]);

        var plan = await new ModDependencyPlanner(provider).BuildAsync(
            a.Project,
            a.Version,
            "1.21.4",
            "fabric");

        Assert(
            plan.InstallOrder.Select(item => item.Project.ProjectId)
                .SequenceEqual(["C", "B", "A"]),
            "Required dependencies must be ordered before dependants.");
        Assert(plan.RequiredDependencyCount == 2,
            "Required dependency count should exclude the root project.");
        Assert(
            plan.OptionalDependencies.Count == 1
            && plan.OptionalDependencies[0].ProjectId == "D",
            "Optional dependencies should be exposed without being auto-installed.");
    }

    private static async Task TestCycleAndConflictRejectionAsync()
    {
        var cycleProvider = new FakeProvider();
        var aProject = cycleProvider.Project("A");
        var bProject = cycleProvider.Project("B");
        var aVersion = cycleProvider.Version(
            "A",
            "A1",
            [new ModProviderDependency("B", null, ModProviderDependencyType.Required)]);
        var bVersion = cycleProvider.Version(
            "B",
            "B1",
            [new ModProviderDependency("A", null, ModProviderDependencyType.Required)]);
        cycleProvider.Add(aProject, aVersion);
        cycleProvider.Add(bProject, bVersion);

        await ExpectInvalidAsync(
            () => new ModDependencyPlanner(cycleProvider).BuildAsync(
                aProject,
                aVersion,
                "1.21.4",
                "fabric"),
            "Dependency cycle");

        var conflictProvider = new FakeProvider();
        var b1 = conflictProvider.Add("B", "B1", []);
        var b2 = conflictProvider.Version("B", "B2", []);
        conflictProvider.AddVersion(b2);
        var c = conflictProvider.Add(
            "C",
            "C1",
            [new ModProviderDependency("B", "B2", ModProviderDependencyType.Required)]);
        var root = conflictProvider.Add(
            "ROOT",
            "R1",
            [
                new ModProviderDependency("B", b1.Version.VersionId, ModProviderDependencyType.Required),
                new ModProviderDependency("C", c.Version.VersionId, ModProviderDependencyType.Required)
            ]);

        await ExpectInvalidAsync(
            () => new ModDependencyPlanner(conflictProvider).BuildAsync(
                root.Project,
                root.Version,
                "1.21.4",
                "fabric"),
            "Dependency conflict");

        var incompatibleProvider = new FakeProvider();
        var y = incompatibleProvider.Add("Y", "Y1", []);
        var x = incompatibleProvider.Add(
            "X",
            "X1",
            [
                new ModProviderDependency("Y", null, ModProviderDependencyType.Required),
                new ModProviderDependency("Y", null, ModProviderDependencyType.Incompatible)
            ]);

        await ExpectInvalidAsync(
            () => new ModDependencyPlanner(incompatibleProvider).BuildAsync(
                x.Project,
                x.Version,
                "1.21.4",
                "fabric"),
            "incompatible");
    }

    private static async Task TestAtomicBatchRollbackAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-mod-dependency-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new NexoPathService(root);
            var mods = new InstanceModService(paths);
            var instanceId = Guid.NewGuid().ToString("N");
            var sourceRoot = Path.Combine(root, "staged");
            Directory.CreateDirectory(sourceRoot);
            var a = Path.Combine(sourceRoot, "a.jar");
            var b = Path.Combine(sourceRoot, "b.jar");
            await File.WriteAllBytesAsync(a, [1, 2, 3]);
            await File.WriteAllBytesAsync(b, [4, 5, 6]);

            var modsDirectory = mods.GetModsDirectory(instanceId);
            Directory.CreateDirectory(modsDirectory);
            Directory.CreateDirectory(Path.Combine(modsDirectory, "b.jar"));

            try
            {
                await mods.InstallProviderBatchAsync(
                    instanceId,
                    [
                        new ProviderModBatchItem(a, null, true),
                        new ProviderModBatchItem(b, null, true)
                    ]);
                throw new Exception(
                    "A destination-directory collision unexpectedly allowed the batch to publish.");
            }
            catch (IOException)
            {
            }

            Assert(
                !File.Exists(Path.Combine(modsDirectory, "a.jar")),
                "A failed dependency batch must roll back files published earlier in the same batch.");
            Assert(
                Directory.Exists(Path.Combine(modsDirectory, "b.jar")),
                "Rollback must not remove an unrelated pre-existing directory collision.");

            Directory.Delete(Path.Combine(modsDirectory, "b.jar"));
            var installed = await mods.InstallProviderBatchAsync(
                instanceId,
                [
                    new ProviderModBatchItem(a, null, true),
                    new ProviderModBatchItem(b, null, true)
                ]);
            Assert(
                installed.Count == 2
                && File.Exists(Path.Combine(modsDirectory, "a.jar"))
                && File.Exists(Path.Combine(modsDirectory, "b.jar")),
                "A valid dependency batch must publish every staged mod.");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
            }
        }
    }

    private static async Task ExpectInvalidAsync(
        Func<Task> action,
        string expectedText)
    {
        try
        {
            await action();
        }
        catch (InvalidDataException ex)
        {
            Assert(
                ex.Message.Contains(
                    expectedText,
                    StringComparison.OrdinalIgnoreCase),
                $"Expected dependency error to contain '{expectedText}', got '{ex.Message}'.");
            return;
        }

        throw new Exception(
            $"Expected InvalidDataException containing '{expectedText}'.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class FakeProvider : IModDependencyProvider
    {
        private readonly Dictionary<string, ModProviderProject> _projects =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<ModProviderVersion>> _versions =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, ModProviderVersion> _versionsById =
            new(StringComparer.Ordinal);

        public string ProviderId => "fake";
        public string DisplayName => "Fake";

        public (ModProviderProject Project, ModProviderVersion Version) Add(
            string projectId,
            string versionId,
            IReadOnlyList<ModProviderDependency> dependencies)
        {
            var project = Project(projectId);
            var version = Version(projectId, versionId, dependencies);
            Add(project, version);
            return (project, version);
        }

        public void Add(
            ModProviderProject project,
            ModProviderVersion version)
        {
            _projects[project.ProjectId] = project;
            AddVersion(version);
        }

        public void AddVersion(ModProviderVersion version)
        {
            if (!_versions.TryGetValue(version.ProjectId, out var versions))
            {
                versions = [];
                _versions[version.ProjectId] = versions;
            }
            versions.Add(version);
            _versionsById[version.VersionId] = version;
        }

        public ModProviderProject Project(string id)
            => new(
                ProviderId,
                id,
                id.ToLowerInvariant(),
                id,
                string.Empty,
                "tests",
                null,
                0,
                "https://example.invalid/" + id);

        public ModProviderVersion Version(
            string projectId,
            string versionId,
            IReadOnlyList<ModProviderDependency> dependencies)
            => new(
                ProviderId,
                projectId,
                versionId,
                versionId,
                versionId,
                DateTimeOffset.UtcNow,
                [
                    new ModProviderFile(
                        projectId.ToLowerInvariant() + ".jar",
                        "https://example.invalid/" + projectId + ".jar",
                        new string('0', 40),
                        1,
                        true)
                ])
            {
                Dependencies = dependencies
            };

        public Task<IReadOnlyList<ModProviderProject>> SearchAsync(
            string query,
            string minecraftVersion,
            string loader,
            int limit = 20,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ModProviderProject>>([]);

        public Task<ModProviderProject?> GetProjectAsync(
            string projectId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(
                _projects.TryGetValue(projectId, out var project)
                    ? project
                    : null);

        public Task<ModProviderVersion?> GetLatestCompatibleVersionAsync(
            string projectId,
            string minecraftVersion,
            string loader,
            CancellationToken cancellationToken = default)
            => Task.FromResult(
                _versions.TryGetValue(projectId, out var versions)
                    ? versions.LastOrDefault()
                    : null);

        public Task<ModProviderVersion?> GetCompatibleVersionAsync(
            string? projectId,
            string? versionId,
            string minecraftVersion,
            string loader,
            CancellationToken cancellationToken = default)
        {
            if (versionId is not null)
            {
                return Task.FromResult(
                    _versionsById.TryGetValue(versionId, out var exact)
                    && (projectId is null
                        || string.Equals(
                            exact.ProjectId,
                            projectId,
                            StringComparison.Ordinal))
                        ? exact
                        : null);
            }

            return Task.FromResult(
                projectId is not null
                && _versions.TryGetValue(projectId, out var versions)
                    ? versions.LastOrDefault()
                    : null);
        }

        public async Task<ModProviderStagedInstall> StageAsync(
            ModProviderProject project,
            ModProviderVersion version,
            CancellationToken cancellationToken = default)
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "un-nexo-fake-provider",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, version.SelectPrimaryFile().FileName);
            await File.WriteAllBytesAsync(
                path,
                System.Text.Encoding.UTF8.GetBytes(version.VersionId),
                cancellationToken);
            return new ModProviderStagedInstall(
                project,
                version,
                path,
                root);
        }

        public Task<IReadOnlyDictionary<string, ModProviderInstalledMatch>> MatchInstalledAsync(
            string modsDirectory,
            IReadOnlyList<InstalledMod> installedMods,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, ModProviderInstalledMatch>>(
                new Dictionary<string, ModProviderInstalledMatch>());

        public Task<ModProviderInstallResult> InstallAsync(
            string instanceId,
            ModProviderProject project,
            ModProviderVersion version,
            ModProviderInstalledMatch? existing,
            InstanceModService modService,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
