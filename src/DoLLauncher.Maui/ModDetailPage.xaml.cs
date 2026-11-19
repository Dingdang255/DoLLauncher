using DoLLauncher.Models;
using DoLLauncher.Platform;
using DoLLauncher.Services;

namespace DoLLauncher.Maui;

public partial class ModDetailPage : ContentPage
{
    private const int ReadmeLimit = 20000;

    private readonly ModInfo _mod;
    private ModDetail? _detail;
    private bool _loading;

    public ModDetailPage(VersionInfo version, ModInfo mod)
    {
        InitializeComponent();
        _mod = mod;
        Title = string.IsNullOrWhiteSpace(mod.Name) ? mod.FileName : mod.Name;
        NameLabel.Text = Title;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_detail == null && !_loading) _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        ModDetail? detail = null;
        SetBusy(true);
        try
        {
            detail = await Task.Run(() => ModService.ReadDetail(_mod));
            _detail = detail;
            ApplyDetail(detail);
        }
        catch (Exception ex)
        {
            LogService.Error("读取模组详情失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("读取模组详情失败：" + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }

        // 元数据补全需要访问市场索引，可能有网络等待，放在页面显示之后进行。
        if (detail != null)
        {
            try
            {
                await CompleteMetadataAsync(detail);
            }
            catch (Exception ex)
            {
                LogService.Error("补全模组元数据失败", ex);
            }
        }
        _loading = false;
    }

    private void ApplyDetail(ModDetail detail)
    {
        NameLabel.Text = string.IsNullOrWhiteSpace(detail.Name) ? detail.FileName : detail.Name;
        VersionLabel.Text = string.IsNullOrWhiteSpace(detail.Version) ? "未知" : detail.Version;
        AuthorLabel.Text = string.IsNullOrWhiteSpace(detail.Author) ? "未知" : detail.Author;
        FileNameLabel.Text = detail.FileName;
        MetaLabel.Text = $"版本 {VersionLabel.Text}　·　作者 {AuthorLabel.Text}";

        var description = string.IsNullOrWhiteSpace(detail.Description)
            ? "（没有简介）"
            : detail.Description;
        DescriptionLabel.Text = description;
        DescriptionPanel.IsVisible = true;

        if (ModService.IsNativePack(detail.FilePath))
        {
            DependencyLabel.Text = "未知（.modpack 是 ModLoader 私有格式，启动器无法解析包内信息）";
        }
        else if (detail.Dependencies.Count == 0)
        {
            DependencyLabel.Text = "无";
        }
        else
        {
            DependencyLabel.Text = string.Join("、", detail.Dependencies.Select(d =>
                d.Version.Length > 0 ? $"{d.Name} {d.Version}" : d.Name));
        }

        var readme = detail.Readme;
        if (string.IsNullOrWhiteSpace(readme))
        {
            readme = "（这个模组没有附带 README 文件）";
        }
        else if (readme.Length > ReadmeLimit)
        {
            readme = readme[..ReadmeLimit] + "\n…（README 过长，已截断）";
        }
        ReadmeEditor.Text = readme;

        UpdateLinks(detail.Homepage);
    }

    /// <summary>作者 / 主页缺失时用市场索引补全，并按设置写回 boot.json。</summary>
    private async Task CompleteMetadataAsync(ModDetail detail)
    {
        var needAuthor = string.IsNullOrWhiteSpace(detail.Author);
        var needHomepage = string.IsNullOrWhiteSpace(detail.Homepage);
        if (!needAuthor && !needHomepage) return;

        var catalog = await ModMarketCache.GetAsync();
        if (catalog == null || catalog.Mods.Count == 0) return;

        var normalized = DownloadService.NormalizeName(detail.Name);
        var market = catalog.Mods.FirstOrDefault(m =>
            DownloadService.NormalizeName(m.Name) == normalized ||
            m.BootNames.Any(b => DownloadService.NormalizeName(b) == normalized));

        if (market != null)
        {
            if (needAuthor && market.Author.Length > 0) detail.Author = market.Author;
            if (string.IsNullOrWhiteSpace(detail.Description) && market.Description.Length > 0)
                detail.Description = market.Description;
            if (needHomepage && market.Repo.Length > 0)
                detail.Homepage = "https://github.com/" + market.Repo;
        }

        if (AppConfig.Load().General.WriteModMetadata)
        {
            var author = needAuthor ? detail.Author : string.Empty;
            var homepage = needHomepage ? detail.Homepage : string.Empty;
            if (author.Length > 0 || homepage.Length > 0)
            {
                var changed = await Task.Run(() => ModService.WriteBackMetadata(_mod, author, homepage));
                if (changed)
                {
                    MetadataNote.IsVisible = true;
                    LogService.Append($"已把市场信息写回模组包：{detail.FileName}");
                }
            }
        }

        ApplyDetail(detail);
    }

    private void UpdateLinks(string homepage)
    {
        HomepageButton.IsVisible = homepage.Length > 0;
    }

    private async void OnHomepageClicked(object? sender, EventArgs e) =>
        await OpenUrlAsync(_detail?.Homepage);

    private async Task OpenUrlAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            await Launcher.Default.OpenAsync(url);
        }
        catch (Exception ex)
        {
            LogService.Error("打开模组主页失败", ex);
            await PlatformServices.Dialogs.ErrorAsync("无法打开链接：" + ex.Message);
        }
    }

    private void SetBusy(bool busy)
    {
        BusyOverlay.IsVisible = busy;
        HomepageButton.IsEnabled = !busy;
    }
}
