using UN.Nexo.Core.Models;

namespace UN.Nexo.Desktop.ViewModels;

public sealed class ModrinthBrowserItem
{
    public ModrinthBrowserItem(
        ModProviderProject project,
        ModProviderInstalledMatch? installed)
    {
        Project = project;
        Installed = installed;
    }

    public ModProviderProject Project { get; }
    public ModProviderInstalledMatch? Installed { get; }
    public string Title => Project.Title;
    public string Description => Project.Description;
    public string SourceLabel
        => Project.ProviderId.ToLowerInvariant() switch
        {
            "modrinth" => "Modrinth",
            "curseforge" => "CurseForge",
            _ => Project.ProviderId
        };
    public string Subtitle
        => $"by {Project.Author} · {FormatDownloads(Project.Downloads)} downloads";
    public string InstallState
        => Installed is null ? "Not installed" : $"Installed {Installed.VersionNumber}";

    private static string FormatDownloads(long value)
        => value switch
        {
            >= 1_000_000_000 => $"{value / 1_000_000_000d:0.#}B",
            >= 1_000_000 => $"{value / 1_000_000d:0.#}M",
            >= 1_000 => $"{value / 1_000d:0.#}K",
            _ => value.ToString()
        };
}
