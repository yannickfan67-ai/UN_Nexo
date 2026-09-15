using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using UN.Nexo.Core.Services;

internal static class FabricLibraryPathRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-fabric-path-regression-" + Guid.NewGuid().ToString("N"));
        var librariesRoot = Path.Combine(root, "game", "libraries");
        Directory.CreateDirectory(librariesRoot);
        try
        {
            InvokeCollect(librariesRoot, "net/fabricmc/example/1.0/example-1.0.jar");
            ExpectInvalidPath(librariesRoot, "../../escape.jar");

            var rootPath = Path.GetPathRoot(librariesRoot)
                ?? Path.DirectorySeparatorChar.ToString();
            var absolute = Path.Combine(rootPath, "un-nexo-fabric-escape.jar");
            ExpectInvalidPath(librariesRoot, absolute);

            if (OperatingSystem.IsWindows())
                ExpectInvalidPath(librariesRoot, @"..\..\escape.jar");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static void ExpectInvalidPath(string librariesRoot, string artifactPath)
    {
        try
        {
            InvokeCollect(librariesRoot, artifactPath);
            throw new Exception($"Unsafe Fabric artifact path was accepted: {artifactPath}");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException)
        {
        }
    }

    private static void InvokeCollect(string librariesRoot, string artifactPath)
    {
        var method = typeof(FabricInstallService).GetMethod(
            "CollectLibraries",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Exception("Fabric CollectLibraries regression target was not found.");

        var json = JsonSerializer.Serialize(new
        {
            libraries = new[]
            {
                new
                {
                    downloads = new
                    {
                        artifact = new
                        {
                            path = artifactPath,
                            url = "https://example.invalid/library.jar"
                        }
                    }
                }
            }
        });
        using var document = JsonDocument.Parse(json);
        _ = method.Invoke(null, [document.RootElement, librariesRoot]);
    }
}
