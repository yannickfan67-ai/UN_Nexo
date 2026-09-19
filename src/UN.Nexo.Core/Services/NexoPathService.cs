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

    public string GetRuntimesRoot() => Path.Combine(GetDataRoot(), "runtimes");

    public string GetInstanceDirectory(string instanceId)
    {
        ValidateInstanceId(instanceId);

        ValidateExistingManagedDirectory(GetDataRoot(), "Nexo data root");
        ValidateExistingManagedDirectory(GetInstancesRoot(), "managed instances root");

        var instancesRoot = Path.GetFullPath(GetInstancesRoot());
        var candidate = Path.GetFullPath(Path.Combine(instancesRoot, instanceId));
        var rootWithSeparator = Path.EndsInDirectorySeparator(instancesRoot)
            ? instancesRoot
            : instancesRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!candidate.StartsWith(rootWithSeparator, comparison))
            throw new ArgumentException(
                "Instance path must remain inside the managed instances directory.",
                nameof(instanceId));

        return candidate;
    }

    public string GetInstanceGameDirectory(string instanceId)
        => Path.Combine(GetInstanceDirectory(instanceId), "game");

    public void EnsureDirectories()
    {
        EnsureDataRootPhysical();
        EnsureInstancesRootPhysical();
        EnsureRuntimesRootPhysical();
    }

    public string EnsureDataRootPhysical()
    {
        var root = Path.GetFullPath(GetDataRoot());
        EnsureOrCreatePhysicalDirectory(root, "Nexo data root");
        return root;
    }

    public string EnsureInstancesRootPhysical()
    {
        var dataRoot = EnsureDataRootPhysical();
        var instancesRoot = Path.GetFullPath(GetInstancesRoot());
        EnsureDirectManagedChild(dataRoot, instancesRoot, "managed instances root");
        EnsureOrCreatePhysicalDirectory(instancesRoot, "managed instances root");
        EnsurePhysicalDirectory(dataRoot, "Nexo data root");
        return instancesRoot;
    }

    public string EnsureRuntimesRootPhysical()
    {
        var dataRoot = EnsureDataRootPhysical();
        var runtimesRoot = Path.GetFullPath(GetRuntimesRoot());
        EnsureDirectManagedChild(dataRoot, runtimesRoot, "managed runtimes root");
        EnsureOrCreatePhysicalDirectory(runtimesRoot, "managed runtimes root");
        EnsurePhysicalDirectory(dataRoot, "Nexo data root");
        return runtimesRoot;
    }

    public string EnsureInstanceDirectoryPhysical(string instanceId)
    {
        ValidateInstanceId(instanceId);
        var instancesRoot = EnsureInstancesRootPhysical();
        var instanceRoot = GetInstanceDirectory(instanceId);
        if (!Directory.Exists(instanceRoot))
            throw new DirectoryNotFoundException(
                $"Managed instance directory is missing: {instanceRoot}");

        EnsurePhysicalDirectory(instanceRoot, "managed instance directory");
        EnsurePhysicalDirectory(instancesRoot, "managed instances root");
        return instanceRoot;
    }

    private static void ValidateInstanceId(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("Instance ID is required.", nameof(instanceId));
        if (!Guid.TryParseExact(instanceId, "N", out var parsedId)
            || !parsedId.ToString("N").Equals(instanceId, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Instance ID must be a canonical lowercase GUID-N value.",
                nameof(instanceId));
        }
    }

    private static void EnsureOrCreatePhysicalDirectory(
        string path,
        string label)
    {
        if (Directory.Exists(path))
        {
            EnsurePhysicalDirectory(path, label);
            return;
        }

        if (File.Exists(path))
            throw new InvalidDataException($"{label} is occupied by a file.");

        Directory.CreateDirectory(path);
        EnsurePhysicalDirectory(path, label);
    }

    private static void ValidateExistingManagedDirectory(
        string path,
        string label)
    {
        if (Directory.Exists(path))
            EnsurePhysicalDirectory(path, label);
        else if (File.Exists(path))
            throw new InvalidDataException($"{label} is occupied by a file.");
    }

    private static void EnsurePhysicalDirectory(
        string path,
        string label)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0)
            throw new InvalidDataException($"{label} must be a directory.");
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"{label} must be a physical directory; symbolic links, junctions and other reparse points are not allowed.");
        }
    }

    private static void EnsureDirectManagedChild(
        string parent,
        string child,
        string label)
    {
        var parentFull = Path.GetFullPath(parent)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var expectedParent = Path.GetDirectoryName(Path.GetFullPath(child))
            ?.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (expectedParent is null
            || !string.Equals(parentFull, expectedParent, comparison))
        {
            throw new InvalidDataException(
                $"{label} must be a direct child of the Nexo data root.");
        }
    }
}
