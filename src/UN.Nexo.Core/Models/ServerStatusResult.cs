namespace UN.Nexo.Core.Models;

public enum ServerStatusState
{
    Online,
    Offline,
    TimedOut
}

public sealed record ServerStatusResult(
    ServerStatusState State,
    string Address,
    long? LatencyMs,
    int? OnlinePlayers,
    int? MaxPlayers,
    string? VersionName,
    int? ProtocolVersion,
    string Motd,
    string? Error)
{
    public bool IsOnline => State == ServerStatusState.Online;

    public string StateLabel => State switch
    {
        ServerStatusState.Online => "Online",
        ServerStatusState.TimedOut => "Timed out",
        _ => "Offline"
    };
}
