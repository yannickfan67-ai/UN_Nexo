namespace UN.Nexo.Core.Services;

public sealed class NexoPathService
{
    private readonly string? _dataRootOverride;

    public NexoPathService(string? dataRootOverride = null)
    {
        _dataRootOverride = string.IsNullOrWhiteSpace(dataRootOverride)
            ? null
            : Path.GetFullPath(dataRootOverride);
    }

    public string GetMinecraftDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, ".minecraft");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "minecraft");

        return Path.Combine(home, ".minecraft");
    }

    public string GetDataRoot()
    {
        if (_dataRootOverride is not null)
            return _dataRootOverride;

        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "UN_Nexo");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
            return Path.Combine(home, "Library", "Application Support", "UN_Nexo");

        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        return !string.IsNullOrWhiteSpace(xdg)
            ? Path.Combine(xdg, "UN_Nexo")
            : Path.Combine(home, ".local", "share", "UN_Nexo");
    }

    public string GetInstancesRoot() => Path.Combine(GetDataRoot(), "instances");

    public string GetInstanceDirectory(string instanceId)
        => Path.Combine(GetInstancesRoot(), instanceId);

    public string GetInstanceGameDirectory(string instanceId)
        => Path.Combine(GetInstanceDirectory(instanceId), "game");

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(GetDataRoot());
        Directory.CreateDirectory(GetInstancesRoot());
    }
}
