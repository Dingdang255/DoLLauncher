using System.Windows;
using DoLLauncher.Core.Import;
using DoLLauncher.Models;
using DoLLauncher.Services;

namespace DoLLauncher;

public partial class ExportDialog : Window
{
    public ExportDialog(VersionInfo version)
    {
        InitializeComponent();
        SkinService.ApplyDialogBackground(this);
        ThemeService.Attach(this);
        AppDialog.AttachDialogChrome(this, Width);
        UiAnimations.AttachWindow(this);
        InfoText.Text = $"{version.DisplayName}（{version.Folder}）· 游戏版本 {version.GameVersion ?? "未知"}";

        // Lyra 的内置模组写在 HTML 里，mods/ 为空时“仅导出模组”会得到空包
        if (LyraPackName.TryParse(version.Folder, out _) && ModService.Load(version.FullPath).Count == 0)
        {
            IncludeGameCheck.IsChecked = true;
            IncludeGameCheck.IsEnabled = false;
            IncludeGameCheck.Content = "包含游戏本体（Lyra 内置模组在 HTML 中，只能整包导出）";
            IncludeGameHint.Text = "Lyra 整合包的模组内置在 HTML 中，导出时必须包含游戏本体。";
        }
    }

    public bool IncludeGame => IncludeGameCheck.IsChecked == true;

    public string Note => NoteBox.Text.Trim();

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
