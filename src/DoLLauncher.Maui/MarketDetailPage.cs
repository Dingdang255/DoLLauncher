using DoLLauncher.Maui.Controls;
using DoLLauncher.Models;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

/// <summary>模组市场详情页：标题为模组名，正文可在仓库 README 与 Release 更新日志之间切换。</summary>
public sealed class MarketDetailPage : ContentPage
{
    private readonly MarketMod _mod;
    private readonly GameRelease? _initialRelease;
    private readonly MarkdownView _view = new();
    private readonly Button _readmeTab = new();
    private readonly Button _logTab = new();
    private readonly Picker _releasePicker = new();
    private readonly Grid _releaseRow;

    private readonly List<GameRelease> _releases = new();
    private string? _readme;
    private bool _loadingReadme;
    private bool _loadingReleases;
    private bool _showLog;

    public MarketDetailPage(MarketMod mod, GameRelease? initialRelease = null)
    {
        _mod = mod;
        _initialRelease = initialRelease;
        Title = string.IsNullOrWhiteSpace(mod.Name) ? "模组详情" : mod.Name;

        StyleTab(_readmeTab, "README");
        StyleTab(_logTab, "更新日志");
        _readmeTab.Clicked += async (_, _) => await SwitchToReadmeAsync();
        _logTab.Clicked += async (_, _) => await SwitchToLogAsync();

        _releasePicker.FontSize = 12;
        _releasePicker.VerticalOptions = LayoutOptions.Center;
        _releasePicker.SelectedIndexChanged += (_, _) => RenderReleaseLog();

        _releaseRow = new Grid
        {
            ColumnSpacing = 8,
            IsVisible = false,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
            },
        };
        var releaseLabel = new Label
        {
            Text = "版本",
            FontSize = 12,
            VerticalOptions = LayoutOptions.Center,
        };
        releaseLabel.SetAppThemeColor(Label.TextColorProperty,
            Color.FromArgb("#808080"), Color.FromArgb("#A0A0A0"));
        _releaseRow.Add(releaseLabel);
        _releaseRow.Add(_releasePicker);
        Grid.SetColumn(_releasePicker, 1);

        var tabs = new HorizontalStackLayout { Spacing = 8, Children = { _readmeTab, _logTab } };

        var bodyBorder = new Border
        {
            Padding = 12,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
            Content = new ScrollView { Content = _view },
        };
        bodyBorder.SetAppThemeColor(Border.BackgroundColorProperty,
            Color.FromArgb("#F2F2F2"), Color.FromArgb("#2A2A2A"));

        var root = new Grid
        {
            Padding = new Thickness(16, 12, 16, 12),
            RowSpacing = 10,
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star),
            },
            Children = { tabs, _releaseRow, bodyBorder },
        };
        Grid.SetRow(_releaseRow, 1);
        Grid.SetRow(bodyBorder, 2);

        Content = root;
        UpdateTabs();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (!_showLog) _ = LoadReadmeAsync();
    }

    private static void StyleTab(Button button, string text)
    {
        button.Text = text;
        button.FontSize = 13;
        button.Padding = new Thickness(14, 0);
        button.HeightRequest = 34;
        button.MinimumHeightRequest = 34;
        button.CornerRadius = 8;
    }

    private void UpdateTabs()
    {
        StyleTabState(_readmeTab, !_showLog);
        StyleTabState(_logTab, _showLog);
        _releaseRow.IsVisible = _showLog;
    }

    private static void StyleTabState(Button button, bool active)
    {
        if (active)
        {
            button.SetDynamicResource(Button.BackgroundColorProperty, "AccentColor");
            button.SetDynamicResource(Button.TextColorProperty, "AccentOnColor");
            return;
        }
        button.SetAppThemeColor(Button.BackgroundColorProperty,
            Color.FromArgb("#E0E0E0"), Color.FromArgb("#3C3C3C"));
        button.SetAppThemeColor(Button.TextColorProperty,
            Color.FromArgb("#333333"), Color.FromArgb("#E8E8E8"));
    }

    private async Task SwitchToReadmeAsync()
    {
        _showLog = false;
        UpdateTabs();
        await LoadReadmeAsync();
    }

    private async Task SwitchToLogAsync()
    {
        _showLog = true;
        UpdateTabs();
        await LoadReleasesAsync();
        RenderReleaseLog();
    }

    private async Task LoadReadmeAsync()
    {
        if (_readme != null)
        {
            _view.Markdown = _readme.Length > 0 ? _readme : "（这个模组没有提供 README）";
            return;
        }
        if (_loadingReadme) return;
        _loadingReadme = true;

        _view.Markdown = "（正在读取 README……）";
        try
        {
            var text = await DownloadService.FetchReadmeAsync(_mod);
            _readme = text ?? string.Empty;
            if (!_showLog)
                _view.Markdown = _readme.Length > 0 ? _readme : "（这个模组没有提供 README）";
        }
        catch (Exception ex)
        {
            LogService.Error("读取市场模组 README 失败", ex);
            if (!_showLog) _view.Markdown = "（读取 README 失败：" + ex.Message + "）";
        }
        finally
        {
            _loadingReadme = false;
        }
    }

    private async Task LoadReleasesAsync()
    {
        if (_releases.Count > 0 || _loadingReleases) return;
        _loadingReleases = true;
        try
        {
            var releases = await DownloadService.FetchModReleasesAsync(_mod);
            _releases.AddRange(releases);

            _releasePicker.ItemsSource = _releases.Select(r => r.DisplayName).ToList();
            var index = _initialRelease == null ? 0 : _releases.IndexOf(_initialRelease);
            _releasePicker.SelectedIndex = index >= 0 ? index : 0;
            if (_releases.Count == 0) _view.Markdown = "（这个模组没有可用的 Release 更新日志）";
        }
        catch (Exception ex)
        {
            LogService.Error("读取市场模组 Release 失败", ex);
            _view.Markdown = "（读取更新日志失败：" + ex.Message + "）";
        }
        finally
        {
            _loadingReleases = false;
        }
    }

    private void RenderReleaseLog()
    {
        if (!_showLog || _releases.Count == 0) return;
        var index = _releasePicker.SelectedIndex;
        if (index < 0 || index >= _releases.Count) index = 0;

        var body = _releases[index].Body?.Trim() ?? string.Empty;
        _view.Markdown = body.Length > 0 ? body : "（该版本没有提供更新日志）";
    }
}
