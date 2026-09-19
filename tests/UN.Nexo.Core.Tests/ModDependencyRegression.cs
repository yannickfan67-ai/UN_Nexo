using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class ModDependencyRegression
{
    internal static async Task RunAsync()
    {
        await TestRecursivePlanAndVersionOnlyEdgeAsync();
        await TestCycleAndConflictRejectionAsync();
        await TestInstalledIncompatibilityRejectionAsync();
        await TestProviderCannotEscapeOwnedStagingAsync();
        await TestLinkedProviderStagingIsNotRecursivelyDeletedAsync();
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

    private static async Task TestInstalledIncompatibilityRejectionAsync()
    {
        var projectProvider = new FakeProvider();
        var projectRoot = projectProvider.Add(
            "A",
            "A1",
            [
                new ModProviderDependency(
                    "B",
                    null,
                    ModProviderDependencyType.Incompatible)
            ]);
        var projectPlan = await new ModDependencyPlanner(projectProvider).BuildAsync(
            projectRoot.Project,
            projectRoot.Version,
            "1.21.4",
            "fabric");
        Assert(
            projectPlan.Incompatibilities.Count == 1,
            "The dependency plan must preserve incompatible edges for install-time checks.");

        var installedB1 = new Dictionary<string, ModProviderInstalledMatch>
        {
            ["B"] = new(
                projectProvider.ProviderId,
                "B",
                "B1",
                "1.0.0",
                "b.jar",
                true)
        };
        await ExpectInvalidAsync(
            () =>
            {
                ModDependencyInstaller.ValidateInstalledIncompatibilities(
                    projectProvider.ProviderId,
                    projectPlan,
                    installedB1);
                return Task.CompletedTask;
            },
            "B1");

        var versionProvider = new FakeProvider();
        var versionRoot = versionProvider.Add(
            "A",
            "A1",
            [
                new ModProviderDependency(
                    null,
                    "B1",
                    ModProviderDependencyType.Incompatible)
            ]);
        var versionPlan = await new ModDependencyPlanner(versionProvider).BuildAsync(
            versionRoot.Project,
            versionRoot.Version,
            "1.21.4",
            "fabric");
        await ExpectInvalidAsync(
            () =>
            {
                ModDependencyInstaller.ValidateInstalledIncompatibilities(
                    versionProvider.ProviderId,
                    versionPlan,
                    installedB1);
                return Task.CompletedTask;
            },
            "B1");

        var replacementProvider = new FakeProvider();
        var b2 = replacementProvider.Add("B", "B2", []);
        var replacementRoot = replacementProvider.Add(
            "A",
            "A1",
            [
                new ModProviderDependency(
                    "B",
                    b2.Version.VersionId,
                    ModProviderDependencyType.Required),
                new ModProviderDependency(
                    null,
                    "B1",
                    ModProviderDependencyType.Incompatible)
            ]);
        var replacementPlan = await new ModDependencyPlanner(replacementProvider).BuildAsync(
            replacementRoot.Project,
            replacementRoot.Version,
            "1.21.4",
            "fabric");

        ModDependencyInstaller.ValidateInstalledIncompatibilities(
            replacementProvider.ProviderId,
            replacementPlan,
            new Dictionary<string, ModProviderInstalledMatch>
            {
                ["B"] = new(
                    replacementProvider.ProviderId,
                    "B",
                    "B1",
                    "1.0.0",
                    "b.jar",
                    true)
            });
    }

    private static async Task TestProviderCannotEscapeOwnedStagingAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-provider-staging-tests",
            Guid.NewGuid().ToString("N"));
        var external = Path.Combine(root, "external");
        var dataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(external);
        Directory.CreateDirectory(dataRoot);
        var sentinel = Path.Combine(external, "sentinel.txt");
        var stagedOutside = Path.Combine(external, "outside.jar");
        await File.WriteAllTextAsync(sentinel, "keep");
        await File.WriteAllBytesAsync(stagedOutside, [1, 2, 3]);

        try
        {
            var provider = new FakeProvider
            {
                StageOverride = (project, version, _, _) =>
                    Task.FromResult(new ModProviderStagedInstall(
                        project,
                        version,
                        stagedOutside))
            };
            var rootMod = provider.Add("A", "A1", []);
            var plan = await new ModDependencyPlanner(provider).BuildAsync(
                rootMod.Project,
                rootMod.Version,
                "1.21.4",
                "fabric");
            var installer = new ModDependencyInstaller(
                provider,
                new InstanceModService(new NexoPathService(dataRoot)));

            await ExpectInvalidAsync(
                () => installer.InstallAsync(
                    Guid.NewGuid().ToString("N"),
                    plan,
                    new Dictionary<string, ModProviderInstalledMatch>()),
                "staging");

            Assert(File.Exists(sentinel),
                "Rejecting an out-of-root staged file must not delete the external directory.");
            Assert(File.Exists(stagedOutside),
                "Rejecting an out-of-root staged file must not delete the provider-supplied external file.");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static async Task TestLinkedProviderStagingIsNotRecursivelyDeletedAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-provider-link-tests",
            Guid.NewGuid().ToString("N"));
        var external = Path.Combine(root, "external");
        var dataRoot = Path.Combine(root, "data");
        Directory.CreateDirectory(external);
        Directory.CreateDirectory(dataRoot);
        var sentinel = Path.Combine(external, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "keep");

        string? linkedEntry = null;
        string? operationRoot = null;
        try
        {
            var provider = new FakeProvider
            {
                StageOverride = async (project, version, stagingDirectory, cancellationToken) =>
                {
                    linkedEntry = stagingDirectory;
                    operationRoot = Path.GetDirectoryName(stagingDirectory);
                    Directory.Delete(stagingDirectory);

                    try
                    {
                        Directory.CreateSymbolicLink(stagingDirectory, external);
                    }
                    catch (Exception ex) when (
                        ex is UnauthorizedAccessException
                        or IOException
                        or PlatformNotSupportedException
                        or NotSupportedException)
                    {
                        Directory.CreateDirectory(stagingDirectory);
                        throw new SkipSymlinkRegressionException();
                    }

                    var staged = Path.Combine(stagingDirectory, "linked.jar");
                    await File.WriteAllBytesAsync(
                        staged,
                        [7, 8, 9],
                        cancellationToken);
                    return new ModProviderStagedInstall(
                        project,
                        version,
                        staged);
                }
            };
            var rootMod = provider.Add("A", "A1", []);
            var plan = await new ModDependencyPlanner(provider).BuildAsync(
                rootMod.Project,
                rootMod.Version,
                "1.21.4",
                "fabric");
            var installer = new ModDependencyInstaller(
                provider,
                new InstanceModService(new NexoPathService(dataRoot)));

            try
            {
                await installer.InstallAsync(
                    Guid.NewGuid().ToString("N"),
                    plan,
                    new Dictionary<string, ModProviderInstalledMatch>());
                throw new Exception(
                    "A linked provider staging directory unexpectedly passed validation.");
            }
            catch (SkipSymlinkRegressionException)
            {
                return;
            }
            catch (InvalidDataException)
            {
            }

            Assert(File.Exists(sentinel),
                "Cleanup must never recursively delete through a linked provider staging directory.");
        }
        finally
        {
            if (linkedEntry is not null)
            {
                try
                {
                    if (Directory.Exists(linkedEntry))
                        Directory.Delete(linkedEntry);
                }
                catch
                {
                }
            }
            if (operationRoot is not null)
            {
                try
                {
                    if (Directory.Exists(operationRoot))
                        Directory.Delete(operationRoot, recursive: true);
                }
                catch
                {
                }
            }
            TryDeleteDirectory(root);
        }
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

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class SkipSymlinkRegressionException : Exception
    {
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

        public Func<
            ModProviderProject,
            ModProviderVersion,
            string,
            CancellationToken,
            Task<ModProviderStagedInstall>>? StageOverride { get; init; }

        public async Task<ModProviderStagedInstall> StageAsync(
            ModProviderProject project,
            ModProviderVersion version,
            string stagingDirectory,
            CancellationToken cancellationToken = default)
        {
            if (StageOverride is not null)
                return await StageOverride(
                    project,
                    version,
                    stagingDirectory,
                    cancellationToken);

            var path = Path.Combine(
                stagingDirectory,
                version.SelectPrimaryFile().FileName);
            await File.WriteAllBytesAsync(
                path,
                System.Text.Encoding.UTF8.GetBytes(version.VersionId),
                cancellationToken);
            return new ModProviderStagedInstall(
                project,
                version,
                path);
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
