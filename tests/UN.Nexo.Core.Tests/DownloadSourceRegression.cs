using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class DownloadSourceRegression
{
    public static Task RunAsync()
    {
        var service = new DownloadSourceService();
        service.SetSource("bmclapi");

        Equal(
            "https://bmclapi2.bangbang93.com/maven/com/example/test.jar",
            service.GetCandidates("http://libraries.minecraft.net/com/example/test.jar").Single(),
            "legacy library HTTP URL");
        Equal(
            "https://bmclapi2.bangbang93.com/assets/aa/hash",
            service.GetCandidates("http://resources.download.minecraft.net/aa/hash").Single(),
            "legacy asset HTTP URL");
        Equal(
            "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json",
            service.GetCandidates("http://launchermeta.mojang.com/mc/game/version_manifest_v2.json").Single(),
            "legacy metadata HTTP URL");
        Equal(
            "https://bmclapi2.bangbang93.com/v1/objects/test",
            service.GetCandidates("http://launcher.mojang.com/v1/objects/test").Single(),
            "legacy launcher HTTP URL");

        Throws<InvalidDataException>(
            () => service.GetCandidates("http://evil.example.test/file.jar"),
            "unknown HTTP host");
        Throws<InvalidDataException>(
            () => service.GetCandidates("http://user@libraries.minecraft.net/file.jar"),
            "userinfo-bearing legacy URL");
        Throws<InvalidDataException>(
            () => service.GetCandidates("http://libraries.minecraft.net:8080/file.jar"),
            "non-default legacy port");

        service.SetSource("official");
        Equal(
            "https://libraries.minecraft.net/file.jar",
            service.GetCandidates("http://libraries.minecraft.net/file.jar").Single(),
            "official mode must upgrade allowlisted legacy HTTP input before any request");

        var modern = service.GetCandidates("https://libraries.minecraft.net/com/example/test.jar");
        Equal("https://libraries.minecraft.net/com/example/test.jar", modern.Single(), "official HTTPS URL");

        return Task.CompletedTask;
    }

    private static void Equal(string expected, string actual, string message)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'.");
    }

    private static void Throws<TException>(Action action, string message) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"{message}: expected {typeof(TException).Name}.");
    }
}
