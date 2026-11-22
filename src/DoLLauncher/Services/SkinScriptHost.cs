using System.Globalization;
using System.Windows;
using DoLLauncher.Controls;
using DoLLauncher.Core.Skin;

namespace DoLLauncher.Services;

/// <summary>
/// 美化包轻量脚本的数据 / 动作宿主：把 <see cref="ISkinScriptHost"/> 的白名单能力接到启动器上。
/// 脚本本身不能访问文件、网络或反射，所有副作用都由这里显式转成启动器的内部调用。
/// </summary>
public static class SkinScriptHost
{
    /// <summary>内存变量表（<c>vars.&lt;name&gt;</c>）；美化包启用 / 卸载时清空。</summary>
    private static readonly Dictionary<string, SkinScriptValue> Variables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>供引擎与数据绑定使用的宿主实例。</summary>
    public static ISkinScriptHost Instance { get; } = new ScriptHost();

    /// <summary>清空脚本变量（切换或卸载美化包时调用）。</summary>
    public static void Clear() => Variables.Clear();

    /// <summary>写入一个脚本变量（交互组件回写 <c>vars.*</c> 时使用）。</summary>
    public static void SetVariable(string name, string value)
    {
        var key = (name ?? string.Empty).Trim();
        if (key.Length == 0) return;
        Variables[key] = SkinScriptValue.From(value);
    }

    /// <summary>解析并执行一段事件脚本；失败只写日志，不打断界面。</summary>
    public static void Run(string source)
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

    /// <summary>把动作放到 UI 线程执行；没有界面时返回兜底值。</summary>
    private static T OnUi<T>(Func<T> action, T fallback)
    {
        var app = Application.Current;
        if (app == null) return fallback;

        try
        {
            return app.Dispatcher.CheckAccess() ? action() : app.Dispatcher.Invoke(action);
        }
        catch (Exception ex)
        {
            LogService.Error("美化包脚本执行失败", ex);
            return fallback;
        }
    }

    private static MainWindow? FindWindow()
    {
        var app = Application.Current;
        if (app == null) return null;
        if (app.MainWindow is MainWindow main) return main;

        foreach (Window window in app.Windows)
            if (window is MainWindow found) return found;
        return null;
    }

    private static string ArgText(IReadOnlyList<SkinScriptValue> args, int index)
        => index < args.Count ? args[index].AsText().Trim() : string.Empty;

    private static bool ArgBool(IReadOnlyList<SkinScriptValue> args, int index, bool fallback)
        => index < args.Count && !args[index].IsNull ? args[index].AsBool() : fallback;

    /// <summary>读取内置数据；未知路径统一返回 Null。</summary>
    private static SkinScriptValue ResolveBuiltin(string path)
    {
        var key = path.ToLowerInvariant();
        var main = FindWindow();

        switch (key)
        {
            case "version.count": return SkinScriptValue.From(main?.SkinVersionCount ?? 0);
            case "version.name": return SkinScriptValue.From(main?.SkinVersionName ?? string.Empty);
            case "version.gameversion": return SkinScriptValue.From(main?.SkinGameVersion ?? string.Empty);
            case "version.running": return SkinScriptValue.From(main?.SkinVersionRunning ?? false);
            case "mods.total": return SkinScriptValue.From(main?.SkinModTotal ?? 0);
            case "mods.enabled": return SkinScriptValue.From(main?.SkinModEnabled ?? 0);
            case "theme.id": return SkinScriptValue.From(SkinService.CurrentThemeId);
            case "theme.name": return SkinScriptValue.From(SkinService.CurrentTheme?.Name ?? string.Empty);
            case "theme.count": return SkinScriptValue.From(SkinService.Themes.Count);
            case "motion": return SkinScriptValue.From(SkinService.MotionEnabled);
            case "particles": return SkinScriptValue.From(SkinService.Effects.Ambient?.Particles ?? 0);
            case "ornament": return SkinScriptValue.From(SkinService.OrnamentEnabled);
            case "skin.id": return SkinScriptValue.From(SkinService.Current?.Id ?? string.Empty);
            case "skin.name": return SkinScriptValue.From(SkinService.Current?.Name ?? string.Empty);
            case "app.version": return SkinScriptValue.From(BuildInfo.Tag);
            default: return SkinScriptValue.Null;
        }
    }

    /// <summary>读取设置项：已保存值优先，缺失时回退清单声明的默认值。</summary>
    private static SkinScriptValue ResolveSetting(string key)
    {
        if (key.Length == 0) return SkinScriptValue.Null;

        var isTheme = key.Equals("theme", StringComparison.OrdinalIgnoreCase);
        if (isTheme && SkinService.CurrentThemeId.Length > 0) return SkinScriptValue.From(SkinService.CurrentThemeId);

        var dir = SkinService.SkinDirectory;
        if (dir.Length > 0)
        {
            var state = SkinSettingsStore.Load(dir);
            if (state.Values.TryGetValue(key, out var stored)) return SkinScriptValue.From(stored);
            if (isTheme && state.Theme.Length > 0) return SkinScriptValue.From(state.Theme);
        }

        var settings = SkinService.Current?.Settings;
        if (settings != null)
        {
            foreach (var definition in settings)
            {
                if (definition == null) continue;
                if (!(definition.Key ?? string.Empty).Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
                return SkinScriptValue.From(definition.GetDefaultText());
            }
        }

        if (isTheme)
        {
            var fallback = (SkinService.Current?.DefaultTheme ?? string.Empty).Trim();
            if (fallback.Length > 0 && !fallback.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return SkinScriptValue.From(fallback);
        }

        return SkinScriptValue.Null;
    }

    /// <summary>动作白名单的真实实现；名字大小写不敏感，参数缺失时按默认值兜底。</summary>
    private static bool InvokeCore(string name, IReadOnlyList<SkinScriptValue> args)
    {
        switch (name)
        {
            case "settheme":
            {
                var id = ArgText(args, 0);
                return id.Length > 0 && SkinService.SetSettingValue("theme", id);
            }

            case "setmotion":
                return SkinService.SetSettingValue("motion", ArgBool(args, 0, false) ? "true" : "false");

            case "setparticles":
            {
                var number = (int)Math.Round(args.Count > 0 ? args[0].AsNumber() : 0);
                number = Math.Clamp(number, 0, SkinRules.MaxParticles);
                return SkinService.SetSettingValue("particles", number.ToString(CultureInfo.InvariantCulture));
            }

            case "setornament":
                return SkinService.SetSettingValue("ornament", ArgBool(args, 0, false) ? "true" : "false");

            case "openpage":
            {
                var page = ArgText(args, 0);
                var main = FindWindow();
                return main != null && main.SkinOpenPage(page);
            }

            case "opensettings":
            {
                var main = FindWindow();
                if (main == null || SkinService.Current == null) return false;
                new SkinSettingsWindow { Owner = main }.ShowDialog();
                return true;
            }

            case "show":
            case "hide":
            case "toggle":
            {
                var node = SkinComponentService.FindComponent(ArgText(args, 0));
                if (node == null) return false;
                node.Visibility = name switch
                {
                    "show" => Visibility.Visible,
                    "hide" => Visibility.Collapsed,
                    _ => node.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible,
                };
                return true;
            }

            case "setvar":
            {
                var variable = ArgText(args, 0);
                if (variable.Length == 0) return false;
                Variables[variable] = args.Count > 1 ? args[1] : SkinScriptValue.Null;
                SkinService.NotifyBindingsChanged();
                return true;
            }

            case "launch":
            {
                var main = FindWindow();
                if (main == null) return false;
                main.SkinLaunch();
                return true;
            }

            case "opendir":
            {
                var kind = ArgText(args, 0).ToLowerInvariant();
                var main = FindWindow();
                var versionDir = main?.SkinVersionFolder ?? string.Empty;

                var path = kind switch
                {
                    "version" => versionDir,
                    "skin" => SkinService.SkinsRoot,
                    "mods" => versionDir.Length > 0 ? ModService.ModsDir(versionDir) : string.Empty,
                    _ => string.Empty,
                };
                if (path.Length == 0) return false;

                ServerManager.OpenFolder(path);
                return true;
            }

            case "toast":
                LogService.Append("美化包：" + ArgText(args, 0));
                return true;

            default:
                return false;
        }
    }

    /// <summary>引擎侧宿主：数据读取与动作执行都集中在这里。</summary>
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

                return OnUi(() => ResolveBuiltin(text), SkinScriptValue.Null);
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
                return OnUi(() => InvokeCore(name, arguments), false);
            }
            catch
            {
                return false;
            }
        }
    }
}
