using System.Windows;
using DoLLauncher.Models;
using DoLLauncher.Services;

namespace DoLLauncher;

public partial class ExportDialog : Window
{
    public ExportDialog(VersionInfo version)
    {
        InitializeComponent();
        ThemeService.Attach(this);
        UiAnimations.AttachWindow(this);
        InfoText.Text = $"{version.DisplayName}（{version.Folder}）· 游戏版本 {version.GameVersion ?? "未知"}";
    }

    public bool IncludeGame => IncludeGameCheck.IsChecked == true;

    public string Note => NoteBox.Text.Trim();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
