namespace UN.Nexo.Core.Models;

public sealed record JavaInstallation(
    string JavaPath,
    string HomePath,
    string Version,
    bool Is64Bit,
    string Source);
