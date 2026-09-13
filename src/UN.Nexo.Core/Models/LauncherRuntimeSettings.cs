namespace UN.Nexo.Core.Models;

public sealed record LauncherRuntimeSettings(
    int MemoryMb = 0,
    string ExtraJvmArguments = "")
{
    public const int MinMemoryMb = 512;
    public const int MaxMemoryMb = 65536;
}
