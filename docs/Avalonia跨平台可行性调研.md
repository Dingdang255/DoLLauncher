# Avalonia 跨平台可行性调研（需求 7）

> 范围：`dev\src\DoLLauncher`（WPF / net10.0-windows）。本报告只做调研，不改现有 WPF 实现。

## 1. 结论

**可行，但不建议立刻整体迁移。** 项目已经具备三层基线与平台接口，业务代码与 WPF 的耦合集中在：

- 页面与控件（`MainWindow.*.xaml(.cs)`、三个自绘对话框、`PaneSplitter`、`TextToolTip`、`UiAnimations`）；
- 平台实现（DWM 标题栏/边框、DPI 物理像素对齐、窗口句柄操作、注册表读取系统主题）；
- 系统集成（`OpenFileDialog`、`Process.Start` 打开浏览器/目录、WPF 图片解码与资源 `pack://`）。

这些都可以在 Avalonia 中找到对应能力，但需要逐项替换。应用服务层（`Core\`）已经与 WPF 解耦，可作为迁移后复用的核心资产。

## 2. 现状盘点

| 模块 | 现状 | 迁移判断 |
| --- | --- | --- |
| `Core\`（Theme / Search / Data / Diagnostics / Browser / Versions / Ui / Story / Skin） | 纯 .NET，无 WPF 引用 | 直接复用 |
| `Platform\Abstractions\` | `IBrowserHost`、`IPathProvider`、`IThemePlatform`、`IDialogHost`、`IStorage`、`ILogSink` | 直接复用 |
| `Platform\Windows\` | DWM、注册表、`Process.Start`、WPF 图片/画刷 | 需要新增 Avalonia / 跨平台实现 |
| `Services\`（服务器、日志、备份、下载等） | 以文件与 HTTP 为主；少量 WPF 类型（画刷、窗口） | 大部分可复用，画刷/窗口相关抽出到 UI 层 |
| `UI`（`MainWindow` partial、对话框、控件、`Theme.xaml`） | WPF 强绑定 | 需要重写 |
| 资源 | `pack://application:,,,/Assets/...`、嵌入 `credits.json`、`Assets\credits\*.png` | 改为 Avalonia `avares://` |

当前 `MainWindow` 已拆为 `MainWindow.xaml.cs`（版本/模组/启动）与 `MainWindow.Settings/Market/Backup/Credits/Watchers/Avatar/Skin.cs`，页面边界清晰，重写时可以一页一页对照移植。

## 3. 能力对照与改造点

| WPF | Avalonia 对应 | 备注 |
| --- | --- | --- |
| `Window` / `Grid` / `DockPanel` / `Border` | 同名控件 | XAML 结构可大部分保留 |
| `DynamicResource` + `ResourceDictionary` | 同名机制 | 主题色画刷改写方式需改为 `IResourceHost` / 样式类 |
| 样式 `Style` / `ControlTemplate` | Avalonia `Style` / `ControlTheme` | `Theme.xaml` 需要重写，不能直接复用 |
| `DispatcherTimer` | `DispatcherTimer` | 颜色过渡可直接复用 `ColorTransitionService` |
| DWM 标题栏/边框（`DwmSetWindowAttribute`） | 各平台原生标题栏 | Windows 实现可保留；Linux/macOS 用 Avalonia 原生装饰 |
| DPI 物理像素对齐（`AppDialog.SnapWindowSize`） | 布局系统按 DIP，缩放由后端处理 | 不再需要手工半像素修正 |
| `OpenFileDialog`（`Microsoft.Win32`） | `StorageProvider.OpenFilePickerAsync` | 需要抽象成接口，避免 UI 直接引用 |
| `Process.Start(UseShellExecute)` | `Process.Start` / 平台 Launcher | 通过 `IExternalLinks` 之类的接口注入 |
| `pack://` 图片、`BitmapImage` | `avares://` + `Bitmap` | 头像、致谢头像、美化包背景需要重写加载逻辑 |
| `PaneSplitter` 自定义控件 | `GridSplitter` 或自绘 | 现有比例/像素双模式逻辑可保留在 Core |
| `TextToolTip` / `UiAnimations` | 自绘动画 | 需要按 Avalonia 动画 API 重写 |

## 4. 建议的迁移路线

1. **继续解耦（不引入 Avalonia）**
   - 把 `OpenFileDialog`、`Process.Start`、窗口查找等 UI 依赖抽成 `IFilePicker`、`IExternalLinks`、`IShellWindow` 等接口；
   - `Services\` 中的 WPF 画刷/窗口逻辑下沉到 UI 层，业务规则留在 `Core\`；
   - 补一层 UI 无关的“页面 ViewModel”，把 `MainWindow.*.cs` 里的状态与调用顺序整理出来。
2. **建立 Avalonia 前端骨架**
   - 新建 `src\DoLLauncher.Avalonia`，多目标或独立工程；
   - 先移植版本管理页与设置页，验证主题、字体、DPI、启动/停止链路。
3. **替换平台实现**
   - `IPathProvider` / `IStorage` / `ILogSink` 可直接复用；
   - 新增 `AvaloniaBrowserHost`（系统默认浏览器 / 自定义路径优先，独立 Profile 仅在支持的平台启用）；
   - `IThemePlatform` 拆分为“主题色应用”和“窗口装饰”，后者按平台实现。
4. **逐步迁移剩余页面**
   - 备份、诊断、下载中心、模组市场；
   - 最后迁移自绘对话框与动画，统一视觉。
5. **发布策略**
   - WPF 版继续作为 Windows 主发布；
   - Avalonia 版先在 Windows 验证行为一致，再开 Linux/macOS 预览。

## 5. 主要风险

| 风险 | 影响 | 缓解 |
| --- | --- | --- |
| `Theme.xaml` 样式体系重写 | 视觉回归面大 | 先做主题/控件样式对照表，逐控件验收 |
| 自绘弹窗的 DWM/DPI 逻辑不可移植 | 高 DPI 黑线问题可能以另一种形式出现 | Avalonia 使用原生装饰，删除物理像素对齐逻辑，重新做多 DPI 验收 |
| 浏览器承载的平台差异 | 静默清缓存、关页检测、IDB 落盘依赖 Chromium Profile | 能力声明保留；非 Windows 平台降级为“系统默认浏览器”，功能入口按能力禁用 |
| 美化包/头像的图片解码差异 | 格式支持范围变化 | 在 `AvatarRules` / `SkinRules` 中集中维护允许格式 |
| 资源路径 `pack://` | 图片、图标加载失败 | 统一封装资源 URI 解析 |
| 现有 WPF 细节（动画、工具提示、拖拽） | 行为不一致 | 优先保证功能链路，动画后置 |

## 6. 工作量与优先级判断（按改动面，不按时间）

- **改动面小**：`Core\`、`Platform\Abstractions\`、日志、备份、下载、搜索、颜色计算。
- **改动面中**：`Services\` 中的窗口/画刷依赖、资源加载、浏览器承载。
- **改动面大**：`MainWindow` 全部 XAML、`Theme.xaml`、三个对话框、`PaneSplitter`、`UiAnimations`、`TextToolTip`。

**建议**：先把 UI 依赖继续抽接口（本轮已新增 `SkinService`、`AvatarRules`、`IStorySource` 等平台无关代码），等 Windows 功能稳定后再启动 Avalonia 骨架，避免双前端同时返工。
