using UN.Nexo.Core.Launching;

namespace UN.Nexo.Desktop.ViewModels;

public partial class MainWindowViewModel
{
    public async Task LaunchServerAsync(string address)
    {
        if (!CanPlay || SelectedInstance is null || SelectedAccount is null)
        {
            GameStatus = "Server launch needs a prepared instance, compatible Java and a selected local profile.";
            LauncherStatus = GameStatus;
            return;
        }

        MinecraftServerTarget target;
        try
        {
            target = MinecraftServerTarget.Parse(address);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            GameStatus = $"Server address: {ex.Message}";
            LauncherStatus = GameStatus;
            return;
        }

        var instance = SelectedInstance;
        var account = SelectedAccount;
        var cancellation = new CancellationTokenSource();
        _gameCancellation = cancellation;
        IsGameRunning = true;
        _gameLogLines.Clear();
        GameLog = string.Empty;
        GameStatus = $"Preparing {instance.Name} for {target.Authority}…";
        LauncherStatus = GameStatus;

        try
        {
            var plan = await _launchBuilder.BuildAsync(
                instance, account, JavaInstallations.ToArray(), cancellation.Token);
            plan = await new MinecraftServerLaunchDecorator(_paths).ApplyAsync(
                plan, instance, target, cancellation.Token);

            GameStatus = $"Launching {instance.Name} → {target.Authority}";
            LauncherStatus = GameStatus;
            AppendGameLog($"Direct connect target: {target.Authority}");
            var result = await _gameProcess.RunAsync(
                plan, new Progress<string>(AppendGameLog), cancellation.Token);
            GameStatus = result.ExitCode == 0
                ? $"{instance.Name} exited normally after server launch."
                : $"{instance.Name} exited with code {result.ExitCode}.";
            AppendGameLog($"Full log: {result.LogPath}");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            GameStatus = $"Stopped {instance.Name}.";
        }
        catch (Exception ex)
        {
            GameStatus = $"Server launch failed: {ex.Message}";
            AppendGameLog(GameStatus);
        }
        finally
        {
            _gameCancellation = null;
            IsGameRunning = false;
            LauncherStatus = GameStatus;
            cancellation.Dispose();
        }
    }
}
