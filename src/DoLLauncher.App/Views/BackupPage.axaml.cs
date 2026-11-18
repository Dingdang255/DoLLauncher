using Avalonia.Controls;
using Avalonia.Input;
using DoLLauncher.ViewModels;

namespace DoLLauncher.Views;

public partial class BackupPage : UserControl
{
    public BackupPage()
    {
        InitializeComponent();
    }

    private void OnBackupDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is BackupViewModel vm && vm.RestoreSelectedCommand.CanExecute(null))
            vm.RestoreSelectedCommand.Execute(null);
    }
}
