using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using UN.Nexo.Desktop.ViewModels;

namespace UN.Nexo.Desktop.Views;

public sealed partial class LaunchStatusOverlay : UserControl
{
    public LaunchStatusOverlay()
    {
        InitializeComponent();
    }

    private async void CopyDiagnosticSummary_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        try
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
            {
                viewModel.MarkDiagnosticCopyFailed("clipboard service is unavailable");
                return;
            }

            var text = viewModel.GetDiagnosticClipboardText();
            if (string.IsNullOrWhiteSpace(text))
            {
                viewModel.MarkDiagnosticCopyFailed("no diagnosis is available");
                return;
            }

            await clipboard.SetTextAsync(text);
            viewModel.MarkDiagnosticSummaryCopied();
        }
        catch (Exception ex)
        {
            viewModel.MarkDiagnosticCopyFailed(ex.Message);
        }
    }
}
