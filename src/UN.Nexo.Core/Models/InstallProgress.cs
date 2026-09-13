namespace UN.Nexo.Core.Models;

public sealed record InstallProgress(string Stage, int Completed, int Total, string? CurrentItem = null)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp((double)Completed / Total * 100d, 0d, 100d);
}
