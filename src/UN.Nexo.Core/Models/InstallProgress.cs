namespace UN.Nexo.Core.Models;

public sealed record InstallProgress(
    string Stage,
    int Completed,
    int Total,
    string? CurrentItem = null,
    string? Source = null,
    long BytesDownloaded = 0,
    long? TotalBytes = null,
    double BytesPerSecond = 0,
    bool IsFallback = false,
    string? Detail = null)
{
    public double TransferPercent => TotalBytes is > 0
        ? Math.Clamp((double)BytesDownloaded / TotalBytes.Value * 100d, 0d, 100d)
        : 0d;

    public double Percent
    {
        get
        {
            if (Total <= 0)
                return TransferPercent;

            var completed = Math.Clamp(Completed, 0, Total);
            var fractional = completed < Total && TotalBytes is > 0
                ? Math.Clamp((double)BytesDownloaded / TotalBytes.Value, 0d, 1d)
                : 0d;
            return Math.Clamp((completed + fractional) / Total * 100d, 0d, 100d);
        }
    }
}
