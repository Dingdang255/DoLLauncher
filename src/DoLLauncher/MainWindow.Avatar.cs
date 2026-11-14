using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DoLLauncher.Core.Ui;
using DoLLauncher.Services;
using Microsoft.Win32;

namespace DoLLauncher;

/// <summary>玩家头像：手动上传本地图片，未设置时使用内置占位图。</summary>
public partial class MainWindow
{
    private const string DefaultAvatarUri = "pack://application:,,,/Assets/dol-favicon.png";

    /// <summary>读取配置并刷新左上角与设置页的头像预览。</summary>
    private void LoadPlayerAvatar()
    {
        var configured = (AppConfig.Load().General.AvatarPath ?? string.Empty).Trim();
        ApplyPlayerAvatar(configured, out var reason);
        AvatarStatusText.Text = configured.Length == 0
            ? "当前使用内置占位图；点击头像或「上传头像」可选择本地图片。"
            : reason.Length > 0
                ? "头像文件不可用，已回退内置占位图：" + reason
                : "当前头像：" + Path.GetFileName(configured);
    }

    private void Avatar_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => PickPlayerAvatar();

    private void Avatar_MouseEnter(object sender, MouseEventArgs e) => AnimateAvatarMask(0.28);

    private void Avatar_MouseLeave(object sender, MouseEventArgs e) => AnimateAvatarMask(0);

    /// <summary>悬停时给头像盖一层黑色遮罩，提示此处可点击更换。</summary>
    private void AnimateAvatarMask(double target)
    {
        if (!UiAnimations.Active)
        {
            AvatarHoverMask.Opacity = target;
            return;
        }

        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        AvatarHoverMask.BeginAnimation(UIElement.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void AvatarUpload_Click(object sender, RoutedEventArgs e) => PickPlayerAvatar();

    private void PickPlayerAvatar()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择玩家头像",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true) return;

        var (ok, reason) = AvatarRules.Validate(dialog.FileName);
        if (!ok)
        {
            AppDialog.Warn(reason, "玩家头像");
            return;
        }

        try
        {
            var userDir = Path.Combine(Paths.Root, "user");
            Directory.CreateDirectory(userDir);
            var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            var dest = Path.Combine(userDir, "avatar" + ext);
            var sourceFull = Path.GetFullPath(dialog.FileName);
            var destFull = Path.GetFullPath(dest);

            // 用户可能直接选中「user\avatar.*」自身：此时只更新配置，不要先删后拷
            foreach (var old in Directory.GetFiles(userDir, "avatar.*"))
            {
                if (string.Equals(Path.GetFullPath(old), sourceFull, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(old); } catch { }
            }

            if (!string.Equals(sourceFull, destFull, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourceFull, destFull, overwrite: true);
            }

            var relative = Path.Combine("user", "avatar" + ext).Replace('\\', '/');
            var cfg = AppConfig.Load();
            cfg.General.AvatarPath = relative;
            AppConfig.Save(cfg);

            ApplyPlayerAvatar(relative, out _);
            AvatarStatusText.Text = "当前头像：" + Path.GetFileName(relative);
            Log("设置：已更新玩家头像 = " + relative);
        }
        catch (Exception ex)
        {
            Log("保存玩家头像失败：" + ex.Message);
            AppDialog.Error("保存头像失败：" + ex.Message, "玩家头像");
        }
    }

    private void AvatarReset_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var cfg = AppConfig.Load();
            cfg.General.AvatarPath = string.Empty;
            AppConfig.Save(cfg);
            ApplyPlayerAvatar(string.Empty, out _);
            AvatarStatusText.Text = "当前使用内置占位图；点击头像或「上传头像」可选择本地图片。";
            Log("设置：已恢复默认头像");
        }
        catch (Exception ex)
        {
            Log("恢复默认头像失败：" + ex.Message);
        }
    }

    private void ApplyPlayerAvatar(string? configured, out string reason)
    {
        reason = string.Empty;
        var text = (configured ?? string.Empty).Trim();
        if (text.Length > 0)
        {
            var full = Path.IsPathRooted(text) ? text : Path.Combine(Paths.Root, text);
            var (ok, why) = AvatarRules.Validate(full);
            if (ok)
            {
                var image = TryLoadAvatar(full, 96);
                if (image != null)
                {
                    AvatarImage.Source = image;
                    AvatarPreviewImage.Source = image;
                    return;
                }
                reason = "图片无法解码。";
            }
            else
            {
                reason = why;
            }
        }

        var placeholder = new BitmapImage(new Uri(DefaultAvatarUri, UriKind.Absolute));
        AvatarImage.Source = placeholder;
        AvatarPreviewImage.Source = placeholder;
    }
}
