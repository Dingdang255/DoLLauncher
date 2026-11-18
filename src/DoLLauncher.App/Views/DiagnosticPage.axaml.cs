using Avalonia.Controls;
using Avalonia.Input;
using DoLLauncher.ViewModels;

namespace DoLLauncher.Views;

public partial class DiagnosticPage : UserControl
{
    public DiagnosticPage()
    {
        InitializeComponent();
    }

    private void OnLogUploadCommit(object? sender, PointerCaptureLostEventArgs e) =>
        (DataContext as DiagnosticViewModel)?.SaveLogUpload();
}
