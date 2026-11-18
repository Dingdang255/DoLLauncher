using Avalonia.Controls;
using DoLLauncher.ViewModels;

namespace DoLLauncher.Views;

public partial class DownloadPage : UserControl
{
    public DownloadPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is DownloadViewModel vm) vm.ConfirmInstallAsync = ShowInstallCheckAsync;
        };
    }

    private async Task<bool> ShowInstallCheckAsync(InstallCheckRequest request)
    {
        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return false;
        var dialog = new InstallCheckWindow(request.ModName, request.TargetText, request.Check);
        return await dialog.ShowDialog<bool>(owner);
    }
}
