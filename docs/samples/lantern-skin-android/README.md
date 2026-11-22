# 灯台夜幕 · 安卓示例（lantern-skin-android）

`dolskin/3` 的**安卓端**示例美化包：主色覆盖、随深浅色变化的 CSS 变量、按压状态过渡、`@keyframes` 呼吸动画、声明式组件、轻量事件脚本与数据绑定。

WPF 版示例见 `docs/samples/lantern-skin/`（组件宿主是 WPF 主窗口的 `#LaunchBtn` 等控件名，安卓端不适用）。

## 加载方式

- 把本目录**内容**打包成 zip（zip 根目录直接包含 `skin.json`、`skin.css`），在安卓启动器「设置 → 外观 → UI 美化包」里导入；
- 或直接放到启动器根目录 `skins/lantern-skin-android/`（含 `skin.json`）后重新加载美化包。

## 演示内容

组件全部插入主界面侧边栏的固定插槽 `#SkinHost`（`MainPage.xaml` 中的空 VerticalStackLayout）：

| 组件 id | 类型 | 演示点 |
| --- | --- | --- |
| `android-version-note` | Label | `bind.text` 读取 `version.count` / `mods.enabled` / `mods.total` 数据 |
| `android-theme-switch` | Button | `bind.text` 显示 `theme.name`；`events.click` 用轻量脚本 `setTheme(...)` 切换深浅色，并 `toast` 写日志 |
| `android-motion-toggle` | Toggle | `bind.value` 读 `motion`；`bindTo: settings.motion` 回写设置；`changed` 脚本调用 `setMotion(...)` |
| `android-particle-slider` | Slider | `bindTo: settings.particles` 回写；`changed` 调用 `setParticles(...)`（安卓只保存数值，不渲染粒子） |
| `android-breath-badge` | Badge | `animation: breathe 3.2s ease-in-out infinite` 关键帧动画 |

样式表另外演示了：

- `.skin-action:pressed`：按压时背景与透明度过渡（安卓端 `:pressed` 仅 Button 生效）；
- `@media (prefers-color-scheme: dark)` 内覆盖 `:root` 变量，切换深浅色后文字颜色自动跟随；
- `transition: background 160ms ease-out` 写在基础规则里，状态切换时平滑过渡。

## 安卓端限制（与文档 9.1 一致）

- 没有鼠标悬停，`:hover` 不生效；`:selected` 暂不激活；
- `particles` / `ornament` 只保存设置值，没有粒子层与边饰渲染；
- Slider 没有吸附刻度，`step` 被忽略；
- `scale(x, y)` 只取 X 分量；
- 虚拟化列表（`CollectionView`）内的宿主不会插入组件。

格式细节见 `docs/UI美化包格式.md`。
