using DoLLauncher.Models;
using DoLLauncher.Services;

namespace DoLLauncher.Maui.Settings;

public partial class VersionInfoPage : ContentPage
{
    private LauncherConfig _config = new();
    private bool _loading;

    public VersionInfoPage()
    {
        InitializeComponent();
        VersionLabel.Text = BuildInfo.Display;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        Load();
    }

    private void Load()
    {
        _config = SettingsStore.Load();
        _loading = true;
        try
        {
            AutoUpdateSwitch.IsToggled = _config.General.AutoCheckUpdate;
            VersionLabel.Text = BuildInfo.Display;
            LoadCredits();
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>致谢列表：圆形首字头像 + 姓名 / 职位 + 平台链接。</summary>
    private void LoadCredits()
    {
        CreditsList.Children.Clear();
        try
        {
            var entries = CreditsService.Load().Entries;
            CreditsEmptyLabel.IsVisible = entries.Count == 0;
            foreach (var entry in entries)
                CreditsList.Children.Add(BuildCreditRow(entry));
        }
        catch (Exception ex)
        {
            LogService.Error("加载致谢名单失败", ex);
            CreditsEmptyLabel.Text = "致谢名单读取失败：" + ex.Message;
            CreditsEmptyLabel.IsVisible = true;
        }
    }

    private static View BuildCreditRow(CreditEntry entry)
    {
        var avatar = new Border
        {
            WidthRequest = 36,
            HeightRequest = 36,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            VerticalOptions = LayoutOptions.Center,
            BackgroundColor = Colors.Transparent,
        };
        avatar.SetDynamicResource(Border.BackgroundColorProperty, "AccentPaleColor");

        var avatarSource = ResolveAvatarSource(entry.Avatar);
        if (avatarSource != null)
        {
            avatar.Content = new Image
            {
                Source = avatarSource,
                Aspect = Aspect.AspectFill,
                HorizontalOptions = LayoutOptions.Fill,
                VerticalOptions = LayoutOptions.Fill,
            };
        }
        else
        {
            var initial = new Label
            {
                Text = (entry.Name ?? string.Empty).Trim() is { Length: > 0 } name ? name[..1].ToUpperInvariant() : "?",
                FontSize = 15,
                FontAttributes = FontAttributes.Bold,
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
            };
            initial.SetDynamicResource(Label.TextColorProperty, "AccentColor");
            avatar.Content = initial;
        }

        var nameLabel = new Label
        {
            Text = (entry.Name ?? string.Empty).Trim(),
            FontSize = 13,
            FontAttributes = FontAttributes.Bold,
            LineBreakMode = LineBreakMode.TailTruncation,
        };

        var roleText = (entry.Role ?? string.Empty).Trim();
        var roleLabel = new Label
        {
            Text = roleText,
            FontSize = 11,
            IsVisible = roleText.Length > 0,
            LineBreakMode = LineBreakMode.TailTruncation,
        };
        roleLabel.SetAppThemeColor(Label.TextColorProperty,
            Color.FromArgb("#808080"), Color.FromArgb("#A0A0A0"));

        var textColumn = new VerticalStackLayout
        {
            Spacing = 1,
            VerticalOptions = LayoutOptions.Center,
            Children = { nameLabel, roleLabel },
        };

        // 与 WPF 一致：头像 | 姓名/职位 | 链接按钮，全在同一行。
        var links = new HorizontalStackLayout
        {
            Spacing = 6,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Start,
        };
        AddLink(links, "哔哩哔哩", entry.Bilibili);
        AddLink(links, "GitHub", entry.Github);
        AddLink(links, "网站", entry.Website);

        var row = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
            },
            ColumnSpacing = 10,
        };
        row.Add(avatar, 0);
        row.Add(textColumn, 1);
        row.Add(links, 2);
        return row;
    }

    /// <summary>头像可以是 Assets/ 下的内置图片、绝对路径或 http(s) 地址。</summary>
    private static ImageSource? ResolveAvatarSource(string? path)
    {
        var text = (path ?? string.Empty).Trim();
        if (text.Length == 0) return null;

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return new UriImageSource { Uri = uri, CachingEnabled = true };
        }

        var normalized = text.Replace('\\', '/');
        var fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
        if (normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/Assets/", StringComparison.OrdinalIgnoreCase))
        {
            // WPF 端打包在 Assets 里的图片，在 Android 端已复制到 Resources/Images；
            // Android 图片资源名不允许连字符，统一去掉。
            var resource = new string(fileName.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '.').ToArray());
            return resource.Length > 0 ? ImageSource.FromFile(resource) : null;
        }

        return File.Exists(text) ? ImageSource.FromFile(text) : null;
    }

    private static void AddLink(Layout host, string label, string? url)
    {
        var text = (url ?? string.Empty).Trim();
        if (text.Length == 0) return;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;

        var button = new Button
        {
            Text = label,
            FontSize = 10,
            Padding = new Thickness(9, 0),
            HeightRequest = 24,
            MinimumHeightRequest = 24,
            CornerRadius = 6,
            HorizontalOptions = LayoutOptions.Start,
        };
        button.SetDynamicResource(Button.BackgroundColorProperty, "AccentPaleColor");
        button.SetDynamicResource(Button.TextColorProperty, "AccentColor");
        button.Clicked += async (_, _) =>
        {
            try
            {
                await Launcher.Default.OpenAsync(uri);
            }
            catch (Exception ex)
            {
                LogService.Error("打开致谢链接失败", ex);
            }
        };
        host.Add(button);
    }

    private async void OnAutoUpdateToggled(object? sender, ToggledEventArgs e)
    {
        if (_loading) return;
        if (_config.General.AutoCheckUpdate == e.Value) return;

        _config.General.AutoCheckUpdate = e.Value;
        await SettingsStore.SaveAsync(_config, "启动时自动检测更新 = " + (e.Value ? "开" : "关"));
    }

    private async void OnCheckUpdateClicked(object? sender, EventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        UpdateStatusLabel.IsVisible = true;
        UpdateStatusLabel.Text = "正在检查更新…";

        try
        {
            var release = await UpdateService.CheckAsync();
            if (release.Tag.Length == 0)
            {
                UpdateStatusLabel.Text = "没有可用的 Release 信息。";
                return;
            }

            var hasUpdate = BuildInfo.Tag == "dev" || UpdateService.IsNewer(release.Tag, BuildInfo.Tag);
            if (!hasUpdate)
            {
                UpdateStatusLabel.Text = $"已是最新版本（{BuildInfo.Tag}）。";
                return;
            }

            UpdateStatusLabel.Text = BuildInfo.Tag == "dev"
                ? $"当前为开发版构建，最新 Release 为 {release.Tag}。"
                : $"发现新版本 {release.Tag}（当前 {BuildInfo.Tag}）。";
            await UpdateDialogPage.ShowAsync(this, release);
        }
        catch (Exception ex)
        {
            UpdateStatusLabel.Text = ex.Message.Contains("404")
                ? "仓库还没有发布任何 Release。"
                : "检查更新失败：" + ex.Message;
            LogService.Error("检查更新失败", ex);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }
}
