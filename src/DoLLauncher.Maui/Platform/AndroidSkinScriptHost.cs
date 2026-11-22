using System.Globalization;
using DoLLauncher.Core.Skin;
using DoLLauncher.Maui.Settings;
using DoLLauncher.Services;

namespace DoLLauncher.Maui.Platform;

/// <summary>
/// 安卓端的美化包轻量脚本宿主：把 <see cref="ISkinScriptHost"/> 的白名单能力接到 MAUI 启动器。
/// 脚本本身不能访问文件、网络或反射，所有副作用都由这里显式转成内部调用。
/// </summary>
public static class AndroidSkinScriptHost
{
    private static readonly Dictionary<string, SkinScriptValue> Variables = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Warned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>供引擎与数据绑定使用的宿主实例。</summary>
    public static ISkinScriptHost Instance { get; } = new ScriptHost();

    /// <summary>清空脚本变量（切换或卸载美化包时调用）。</summary>
    public static void Clear()
    {
        Variables.Clear();
        Warned.Clear();
    }

    /// <summary>写入一个脚本变量（交互组件回写 <c>vars.*</c> 时使用）。</summary>
    public static void SetVariable(string name, string value)
    {
        var key = (name ?? string.Empty).Trim();
        if (key.Length == 0) return;
        Variables[key] = SkinScriptValue.From(value);
    }

    /// <summary>解析并执行一段事件脚本；失败只写日志，不打断界面。</summary>
    public static void Run(string? source)
    {
        var text = source ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text)) return;

        if (!SkinScriptEngine.TryParse(text, out var program, out var error))
        {
            LogService.Append("美化包：事件脚本无法解析（" + error + "）。");
            return;
        }

        Run(program);
    }

    /// <summary>执行已解析的脚本（组件创建时只解析一次，之后直接复用）。</summary>
    public static void Run(SkinScriptProgram program)
    {
        if (program == null || program.IsEmpty) return;

        try
        {
            if (!program.Run(Instance)) LogService.Append("美化包：事件脚本执行失败。");
        }
        catch (Exception ex)
        {
            LogService.Error("执行美化包事件脚本失败", ex);
        }
    }

    /// <summary>通知所有 <c>bind</c> 重新求值。</summary>
    public static void NotifyBindingsChanged()
    {
        try
        {
            MauiSkinComponentService.RefreshBindings();
        }
        catch (Exception ex)
        {
            LogService.Error("刷新美化包数据绑定失败", ex);
        }
    }

    /// <summary>写设置项（组件 <c>bindTo</c> 与 <c>setTheme</c> 等动作共用）。</summary>
    public static bool SetSetting(string key, string value)
    {
        var name = (key ?? string.Empty).Trim();
        if (name.Length == 0) return false;

        if (name.Equals("theme", StringComparison.OrdinalIgnoreCase)) return ApplyTheme(value);
        if (name.Equals("motion", StringComparison.OrdinalIgnoreCase)) return ApplyMotion(value);

        var folder = AndroidSkinStore.CurrentFolder;
        if (folder.Length == 0) return false;
        return AndroidSkinSettingsStore.SetValue(folder, name, value ?? string.Empty);
    }

    // ---------- 设置动作 ----------

    private static bool ApplyTheme(string raw)
    {
        var mode = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (mode is "auto" or "system") mode = "system";
        if (mode is not ("system" or "light" or "dark")) return false;

        try
        {
            var config = AppConfig.Load();
            if (!string.Equals(config.General.ThemeMode, mode, StringComparison.OrdinalIgnoreCase))
            {
                config.General.ThemeMode = mode;
                AppConfig.Save(config);
            }
            App.ApplyTheme(mode);
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("美化包脚本切换主题失败", ex);
            return false;
        }
    }

    private static bool ApplyMotion(string raw)
    {
        var text = (raw ?? string.Empty).Trim();
        var enabled = !text.Equals("false", StringComparison.OrdinalIgnoreCase)
                      && !text.Equals("0", StringComparison.OrdinalIgnoreCase)
                      && !text.Equals("off", StringComparison.OrdinalIgnoreCase);

        try
        {
            var config = AppConfig.Load();
            if (config.General.ShowAnimations != enabled)
            {
                config.General.ShowAnimations = enabled;
                AppConfig.Save(config);
            }
            return true;
        }
        catch (Exception ex)
        {
            LogService.Error("美化包脚本切换动效失败", ex);
            return false;
        }
    }

    // ---------- 数据 ----------

    private static string ArgText(IReadOnlyList<SkinScriptValue> args, int index)
        => index < args.Count ? args[index].AsText().Trim() : string.Empty;

    private static bool ArgBool(IReadOnlyList<SkinScriptValue> args, int index, bool fallback)
        => index < args.Count && !args[index].IsNull ? args[index].AsBool() : fallback;

    /// <summary>读取清单里声明的默认值（未保存过设置时使用）。</summary>
    private static string SettingDefault(string key)
    {
        var settings = AndroidSkinStore.Current?.Settings;
        if (settings == null) return string.Empty;

        foreach (var definition in settings)
        {
            if (definition == null) continue;
            if (!(definition.Key ?? string.Empty).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            return definition.GetDefaultText();
        }
        return string.Empty;
    }

    private static SkinScriptValue ResolveSetting(string key)
    {
        if (key.Length == 0) return SkinScriptValue.Null;

        var isTheme = key.Equals("theme", StringComparison.OrdinalIgnoreCase);
        var folder = AndroidSkinStore.CurrentFolder;
        if (folder.Length > 0)
        {
            var state = AndroidSkinSettingsStore.Load(folder);
            if (state.Values.TryGetValue(key, out var stored)) return SkinScriptValue.From(stored);
            if (isTheme && state.Theme.Length > 0) return SkinScriptValue.From(state.Theme);
        }

        if (isTheme) return SkinScriptValue.From(AppConfig.Load().General.ThemeMode);

        var settings = AndroidSkinStore.Current?.Settings;
        if (settings != null)
        {
            foreach (var definition in settings)
            {
                if (definition == null) continue;
                if (!(definition.Key ?? string.Empty).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                return SkinScriptValue.From(definition.GetDefaultText());
            }
        }

        return SkinScriptValue.Null;
    }

    private static SkinScriptValue ResolveBuiltin(string path)
    {
        var main = MainPage.Instance;
        switch (path.ToLowerInvariant())
        {
            case "version.count": return SkinScriptValue.From(main?.SkinVersionCount ?? 0);
            case "version.name": return SkinScriptValue.From(main?.SkinVersionName ?? string.Empty);
            case "version.gameversion": return SkinScriptValue.From(main?.SkinGameVersion ?? string.Empty);
            case "version.running": return SkinScriptValue.From(main?.SkinVersionRunning ?? false);
            case "mods.total": return SkinScriptValue.From(main?.SkinModTotal ?? 0);
            case "mods.enabled": return SkinScriptValue.From(main?.SkinModEnabled ?? 0);
            case "theme.id": return SkinScriptValue.From(AppConfig.Load().General.ThemeMode);
            case "theme.name": return SkinScriptValue.From(AppConfig.Load().General.ThemeMode switch
            {
                "light" => "亮色",
                "dark" => "深色",
                _ => "跟随系统",
            });
            case "theme.count": return SkinScriptValue.From(3);
            case "motion": return SkinScriptValue.From(AppConfig.Load().General.ShowAnimations);
            case "particles":
            {
                var folder = AndroidSkinStore.CurrentFolder;
                var state = folder.Length == 0 ? null : AndroidSkinSettingsStore.Load(folder);
                if (state != null && state.Values.TryGetValue("particles", out var stored))
                    return SkinScriptValue.From(stored);

                var fallback = SettingDefault("particles");
                return SkinScriptValue.From(fallback.Length > 0 ? fallback : "0");
            }
            case "ornament":
            {
                var folder = AndroidSkinStore.CurrentFolder;
                var state = folder.Length == 0 ? null : AndroidSkinSettingsStore.Load(folder);
                var raw = state != null && state.Values.TryGetValue("ornament", out var stored)
                    ? stored
                    : SettingDefault("ornament");
                return SkinScriptValue.From(raw.Equals("true", StringComparison.OrdinalIgnoreCase));
            }
            case "skin.id": return SkinScriptValue.From(AndroidSkinStore.Current?.Id ?? string.Empty);
            case "skin.name": return SkinScriptValue.From(AndroidSkinStore.Current?.Name ?? string.Empty);
            case "app.version": return SkinScriptValue.From(BuildInfo.Tag);
            default: return SkinScriptValue.Null;
        }
    }

    // ---------- 动作 ----------

    private static bool InvokeCore(string name, IReadOnlyList<SkinScriptValue> args)
    {
        switch (name)
        {
            case "settheme":
            {
                var id = ArgText(args, 0);
                return id.Length > 0 && SetSetting("theme", id);
            }

            case "setmotion":
                return SetSetting("motion", ArgBool(args, 0, false) ? "true" : "false");

            case "setparticles":
            {
                // 安卓端没有粒子层：只保存数值，便于皮肤配置跨端共用。
                var number = (int)Math.Round(args.Count > 0 ? args[0].AsNumber() : 0);
                number = Math.Clamp(number, 0, SkinRules.MaxParticles);
                WarnOnce("particles", "安卓端暂不渲染粒子效果，setParticles 仅保存设置值。");
                return SetSetting("particles", number.ToString(CultureInfo.InvariantCulture));
            }

            case "setornament":
                WarnOnce("ornament", "安卓端暂不渲染装饰效果，setOrnament 仅保存设置值。");
                return SetSetting("ornament", ArgBool(args, 0, false) ? "true" : "false");

            case "openpage":
            {
                var page = ArgText(args, 0);
                return MainPage.Instance?.SkinOpenPage(page) == true;
            }

            case "opensettings":
            {
                var main = MainPage.Instance;
                if (main == null) return false;
                main.SkinOpenSettings();
                return true;
            }

            case "show":
            case "hide":
            case "toggle":
            {
                var node = MauiSkinComponentService.FindComponent(ArgText(args, 0));
                if (node == null) return false;
                node.IsVisible = name switch
                {
                    "show" => true,
                    "hide" => false,
                    _ => !node.IsVisible,
                };
                return true;
            }

            case "setvar":
            {
                var variable = ArgText(args, 0);
                if (variable.Length == 0) return false;
                Variables[variable] = args.Count > 1 ? args[1] : SkinScriptValue.Null;
                NotifyBindingsChanged();
                return true;
            }

            case "launch":
            {
                var main = MainPage.Instance;
                if (main == null) return false;
                main.SkinLaunch();
                return true;
            }

            case "opendir":
            {
                var kind = ArgText(args, 0).ToLowerInvariant();
                var versionDir = MainPage.Instance?.SkinVersionFolder ?? string.Empty;
                var path = kind switch
                {
                    "version" => versionDir,
                    "skin" => AndroidSkinStore.SkinsRoot,
                    "mods" => versionDir.Length > 0 ? ModService.ModsDir(versionDir) : string.Empty,
                    _ => string.Empty,
                };
                return path.Length > 0 && AndroidFolderOpener.TryOpen(path);
            }

            case "toast":
                LogService.Append("美化包：" + ArgText(args, 0));
                return true;

            default:
                return false;
        }
    }

    // ---------- 宿主实现 ----------

    private static SkinScriptValue OnUi(Func<SkinScriptValue> action)
    {
        try
        {
            if (MainThread.IsMainThread) return action();
            return MainThread.InvokeOnMainThreadAsync(action).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogService.Error("美化包脚本读取数据失败", ex);
            return SkinScriptValue.Null;
        }
    }

    private static bool OnUi(Func<bool> action)
    {
        try
        {
            if (MainThread.IsMainThread) return action();
            return MainThread.InvokeOnMainThreadAsync(action).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogService.Error("美化包脚本执行动作失败", ex);
            return false;
        }
    }

    private static void WarnOnce(string key, string message)
    {
        if (!Warned.Add(key)) return;
        LogService.Append("[美化包] " + message);
    }

    private sealed class ScriptHost : ISkinScriptHost
    {
        public SkinScriptValue ResolveData(string path)
        {
            try
            {
                var text = (path ?? string.Empty).Trim();
                if (text.Length == 0) return SkinScriptValue.Null;

                if (text.StartsWith("vars.", StringComparison.OrdinalIgnoreCase))
                {
                    var name = text[5..].Trim();
                    return Variables.TryGetValue(name, out var value) ? value : SkinScriptValue.Null;
                }

                if (text.StartsWith("settings.", StringComparison.OrdinalIgnoreCase))
                    return ResolveSetting(text[9..].Trim());

                return OnUi(() => ResolveBuiltin(text));
            }
            catch
            {
                return SkinScriptValue.Null;
            }
        }

        public bool InvokeAction(string action, IReadOnlyList<SkinScriptValue> args)
        {
            try
            {
                var name = (action ?? string.Empty).Trim().ToLowerInvariant();
                if (name.Length == 0) return false;

                var arguments = args ?? Array.Empty<SkinScriptValue>();
                return OnUi(() => InvokeCore(name, arguments));
            }
            catch
            {
                return false;
            }
        }
    }
}
