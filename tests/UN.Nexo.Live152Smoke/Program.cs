using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

var temp = Path.Combine(Path.GetTempPath(), "UN-Nexo-live-152-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
Console.WriteLine($"Smoke root: {temp}");

try
{
    var paths = new NexoPathService(temp);
    paths.EnsureDirectories();
    var downloadSources = new DownloadSourceService();
    downloadSources.SetSource("official");
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("UN_Nexo-live-1.5.2-smoke/1.0");

    Console.WriteLine("Fetching official Mojang version catalog…");
    var catalog = await new MinecraftVersionManifestService(http, downloadSources).GetCatalogAsync();
    var version = catalog.Versions.FirstOrDefault(item => item.Id == "1.5.2")
        ?? throw new InvalidOperationException("Official catalog no longer exposes Minecraft 1.5.2.");
    Console.WriteLine($"Found 1.5.2 metadata: {version.Url}");

    var instance = new GameInstance(
        "live-152",
        "Minecraft 1.5.2 live smoke",
        "1.5.2",
        "vanilla",
        DateTimeOffset.UtcNow);

    Console.WriteLine("Downloading real 1.5.2 client, libraries, natives and legacy assets…");
    var installer = new MinecraftVanillaInstallService(http, paths, downloadSources);
    var lastStage = string.Empty;
    await installer.InstallAsync(
        instance,
        version,
        new Progress<InstallProgress>(progress =>
        {
            if (!string.Equals(lastStage, progress.Stage, StringComparison.Ordinal))
            {
                lastStage = progress.Stage;
                Console.WriteLine($"Install stage: {progress.Stage} ({progress.Completed}/{progress.Total})");
            }
        }));

    Console.WriteLine("Acquiring real Java 8 through Adoptium/Temurin if needed…");
    var java = await new JavaRuntimeProvisionService(http, paths).EnsureJavaAsync(
        8,
        new Progress<string>(Console.WriteLine));
    Console.WriteLine($"Java: {java.Version} @ {java.JavaPath}");

    var account = new LauncherAccount(
        "live-local",
        "offline",
        "Nexo152Test",
        Guid.NewGuid().ToString(),
        DateTimeOffset.UtcNow);

    var plan = await new MinecraftLaunchPlanBuilder(paths).BuildAsync(instance, account, [java]);
    Console.WriteLine($"Main launch Java: {plan.JavaPath}");
    Console.WriteLine($"Working directory: {plan.WorkingDirectory}");

    if (!plan.Arguments.Contains("net.minecraft.launchwrapper.Launch", StringComparer.Ordinal))
        throw new InvalidOperationException("1.5.2 launch plan does not use LaunchWrapper.");

    var virtualAssets = Path.Combine(paths.GetInstanceGameDirectory(instance.Id), "assets", "virtual", "legacy");
    if (!Directory.Exists(virtualAssets) || !Directory.EnumerateFiles(virtualAssets, "*", SearchOption.AllDirectories).Any())
        throw new InvalidOperationException("1.5.2 legacy virtual assets were not materialized.");

    var natives = Path.Combine(paths.GetInstanceGameDirectory(instance.Id), "natives", "1.5.2");
    if (!Directory.Exists(natives) || !Directory.EnumerateFiles(natives, "*", SearchOption.AllDirectories).Any())
        throw new InvalidOperationException("1.5.2 native libraries were not extracted.");

    Console.WriteLine("Launching real Minecraft 1.5.2 under the CI X display…");
    var outputLines = new List<string>();
    var process = new MinecraftProcessService(new LauncherRuntimeSettingsService(paths));
    using var launchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    var survivedUntilTimeout = false;
    try
    {
        var result = await process.RunAsync(
            plan,
            new Progress<string>(line =>
            {
                outputLines.Add(line);
                if (line.Length < 600)
                    Console.WriteLine(line);
            }),
            launchTimeout.Token);

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Minecraft 1.5.2 exited early with code {result.ExitCode}. Log: {result.LogPath}");
        Console.WriteLine("Minecraft 1.5.2 exited normally before the timeout.");
    }
    catch (OperationCanceledException) when (launchTimeout.IsCancellationRequested)
    {
        survivedUntilTimeout = true;
        Console.WriteLine("Minecraft 1.5.2 stayed alive for 45 seconds; launcher terminated the smoke process as expected.");
    }

    if (!outputLines.Any(line => line.Contains("Started game process", StringComparison.Ordinal)))
        throw new InvalidOperationException("Minecraft process never reached Process.Start.");

    if (outputLines.Any(line => line.Contains("Exception in thread \"main\"", StringComparison.OrdinalIgnoreCase)
        || line.Contains("UnsatisfiedLinkError", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Could not find or load main class", StringComparison.OrdinalIgnoreCase)
        || line.Contains("NoClassDefFoundError", StringComparison.OrdinalIgnoreCase)))
        throw new InvalidOperationException("Minecraft emitted a fatal JVM/class/native error during the smoke launch.");

    Console.WriteLine(survivedUntilTimeout
        ? "LIVE 1.5.2 SMOKE PASS: real files, Java 8, legacy assets/natives, and startup survived the observation window."
        : "LIVE 1.5.2 SMOKE PASS: real files, Java 8, legacy assets/natives, and startup completed without an error.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("LIVE 1.5.2 SMOKE FAIL");
    Console.Error.WriteLine(ex);
    return 1;
}
finally
{
    try { Directory.Delete(temp, recursive: true); } catch { }
}
