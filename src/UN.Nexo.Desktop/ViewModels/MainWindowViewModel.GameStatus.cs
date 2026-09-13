namespace UN.Nexo.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    private string? _pendingTerminalGameStatus;
    private bool _restoringTerminalGameStatus;

    partial void OnGameStatusChanged(string value)
    {
        if (_restoringTerminalGameStatus)
            return;

        if (IsTerminalGameStatus(value))
        {
            _pendingTerminalGameStatus = value;
            return;
        }

        if (!IsGameRunning
            && _pendingTerminalGameStatus is { Length: > 0 } terminal
            && IsAvailabilityHint(value))
        {
            _pendingTerminalGameStatus = null;
            _restoringTerminalGameStatus = true;
            try
            {
                GameStatus = terminal;
                LauncherStatus = terminal;
            }
            finally
            {
                _restoringTerminalGameStatus = false;
            }
            return;
        }

        if (!IsGameRunning && !IsAvailabilityHint(value))
            _pendingTerminalGameStatus = null;
    }

    private static bool IsTerminalGameStatus(string value)
        => value.StartsWith("Launch failed:", StringComparison.Ordinal)
           || value.StartsWith("Server launch failed:", StringComparison.Ordinal)
           || value.StartsWith("Stopped ", StringComparison.Ordinal)
           || value.Contains(" exited normally", StringComparison.Ordinal)
           || value.Contains(" exited with code ", StringComparison.Ordinal);

    private static bool IsAvailabilityHint(string value)
        => value.StartsWith("Ready", StringComparison.Ordinal)
           || value.StartsWith("Wait for file preparation", StringComparison.Ordinal)
           || value.StartsWith("Scanning environment", StringComparison.Ordinal)
           || value.StartsWith("Create or select an instance", StringComparison.Ordinal)
           || value.StartsWith("Select an offline profile", StringComparison.Ordinal);
}
