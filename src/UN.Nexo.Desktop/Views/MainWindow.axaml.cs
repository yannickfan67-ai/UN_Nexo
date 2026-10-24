using System.Reflection;
using Avalonia.Controls;
using UN.Nexo.Desktop.ViewModels;
namespace UN.Nexo.Desktop.Views;
public sealed partial class MainWindow : Window
{
    private readonly DownloadCenterViewModel _model = new();
    public MainWindow()
    {
        InitializeComponent(); DataContext = _model;
        Title = $"UN_Nexo {typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}";
        Closing += (_, e) => { if (_model.IsBusy) { e.Cancel = true; _model.PauseQueueCommand.Execute(null); _model.Status = "下載已暫停，狀態儲存完成後可關閉視窗。"; } };
    }
    public Task InitializeAsync() => _model.InitializeAsync();
    internal void ReportInitializationFailure(Exception ex) => _model.Status = ex.Message;
    public async Task RevealAsync() { for (var i = 1; i <= 6; i++) { Opacity = i / 6d; await Task.Delay(18); } }
}
