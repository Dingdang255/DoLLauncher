# UI 美化包格式（dolskin/1）

美化包是一个 **zip** 文件，根目录必须包含 `skin.json`，可附带 `skin.css` 与图片资源。启动器只读取清单、样式表与图片，不执行任何脚本。

## 1. 目录示例

```text
my-skin.zip
├─ skin.json
├─ skin.css          ← 可选；不写时自动查找 skin.css / style.css / styles.css
├─ window-bg.png
├─ dialog-bg.png
└─ images/
   └─ card-bg.png
```

加载后会解压到启动器根目录：`skins\<id>\`。

## 2. skin.json

```json
{
  "format": "dolskin/1",
  "id": "my-skin",
  "name": "示例美化包",
  "author": "你的名字",
  "version": "1.0.0",
  "description": "可选说明",
  "accentColor": "#FF6C8CFF",
  "windowBackgroundImage": "window-bg.png",
  "windowBackgroundColor": "#FF1C1C1E",
  "dialogBackgroundImage": "dialog-bg.png",
  "dialogBackgroundColor": "#FF242426",
  "styleSheet": "skin.css",
  "variables": {
    "accent": "#FF6C8CFF",
    "bg": "#FF1E2A44"
  }
}
```

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `format` | 是 | `dolskin/1`（兼容 `dolskin/2`） |
| `id` | 否 | 安装目录名；留空时依次使用 `name`、zip 文件名 |
| `name` | 是 | 美化包显示名称 |
| `author` / `version` / `description` | 否 | 设置页展示用 |
| `accentColor` | 否 | 覆盖主题主色；为空表示沿用「自定义主题色 / 默认主题」 |
| `windowBackgroundImage` | 否 | 主窗口背景图（包内相对路径） |
| `windowBackgroundColor` | 否 | 主窗口背景底色；没有背景图时使用 |
| `dialogBackgroundImage` | 否 | 弹窗背景图（包内相对路径） |
| `dialogBackgroundColor` | 否 | 弹窗背景底色；没有背景图时使用 |
| `styleSheet` | 否 | CSS 样式表路径；留空时自动查找包根目录的 `skin.css` / `style.css` / `styles.css` |
| `variables` | 否 | 额外变量，等价于 CSS 里的 `:root { --变量: 值 }` |

颜色支持 `#RRGGBB`、`#AARRGGBB`（注意 Alpha 在前，与 WPF 一致），CSS 里还额外支持 `#RGB`、`#ARGB`、`rgb()`、`rgba()` 与命名颜色。

## 3. skin.css：类似 CSS 的控件样式

样式表会在加载美化包后立即作用于启动器的所有窗口、弹窗、动态列表项与控件模板内的元素；卸载时逐项还原。

### 3.1 选择器

| 写法 | 含义 |
| --- | --- |
| `Button` | 所有 `Button` 及其子类（也匹配基类型名，如 `Control`） |
| `.FlatButton` | 使用该样式键的控件（等于 XAML 里的 `x:Key`） |
| `#LaunchButton` | `x:Name` 为 `LaunchButton` 的控件 |
| `TextBlock.MutedText` | 既是 `TextBlock` 又使用 `.MutedText` 样式键 |
| `StackPanel > Button` | 后代组合（`>` 与空格等价） |
| `Button, ComboBox` | 逗号分组 |
| `*` | 所有元素（慎用，会作用到模板内部） |

常用样式键：`FlatButton`、`PrimaryButton`、`IconButton`、`TinyButton`、`DangerButton`、`SettingsItemCard`、`MutedText`、`SectionTitle`、`DarkTextBox`、`NavItem`、`NavSubItem`、`FlatCombo`、`ThinProgress`、`LinkButton`、`TextCheck`、`ModCheck`、`VersionItem`、`ModItem`、`DownloadItem`、`CreditLinkButton`。

### 3.2 变量与调色板

```css
:root {
  --accent: #ff6c8cff;   /* 主题主色，映射到 AccentBrush */
  --bg: #ff1e2a44;       /* 页面底色，映射到 BgBrush */
  --card: #ff223354;     /* 卡片底色，映射到 CardBrush */
  --radius: 10px;        /* 仅作为普通变量，供下面的规则引用 */
}
```

变量名会忽略大小写与 `-`/`_`，并自动去掉 `Brush` 后缀。例如 `--accent-hover`、`--AccentHoverBrush` 都指向 `AccentHoverBrush`。
常见别名：`background`/`window` → `bg`，`foreground`/`font` → `text`，`secondary`/`subtext` → `muted`，`error` → `danger`，`warning` → `warn`，`primary`/`link` → `accent`，`surface` → `card`，`hover` → `cardhover`，`selected` → `cardselected`，`progress` → `progresstrack`。

在声明里用 `var(--accent)` 时会直接挂到调色板资源上，切换深浅主题时颜色仍会平滑过渡；写成 `var(--accent, #4C9AFF)` 可以带兜底值。

### 3.3 支持的属性

- 颜色：`background` / `bg` / `background-color`、`color` / `foreground`、`border-color`、`fill`、`stroke`
- 边框：`border`（宽度 + 颜色简写）、`border-width`、`border-thickness`、`border-left/right/top/bottom`（含 `-width`、`-color`）、`corner-radius` / `border-radius`（按钮等控件会作用到模板内的圆角边框）
- 字体：`font-family`、`font-size`、`font-weight`、`font-style`、`font-stretch`、`line-height`、`text-align`、`text-decoration`
- 尺寸与间距：`width`、`height`、`min-width`、`max-width`、`min-height`、`max-height`、`padding`、`margin`
- 面板布局（仅 `StackPanel` / `WrapPanel`，近似 flex）：`flex-direction: row|column`、`gap` / `row-gap` / `column-gap`、`align-items: start|center|end|stretch`
- 文本流：`white-space: nowrap|normal|pre-wrap`、`overflow-wrap: anywhere`、`word-break: break-word`、`text-overflow: ellipsis`
- 外观：`opacity`、`visibility`、`display`（`none` 隐藏，`flex`/`block` 仅作可见）、`cursor`、`horizontal-align`、`vertical-align`、`overflow: hidden|visible`、`z-index`
- 背景图：`background-image: url("images/card-bg.png")`、`background-size: cover|contain|fill|none`、`background-repeat: no-repeat|repeat`
- 阴影：`box-shadow: 0 4px 12px rgba(0,0,0,0.35)`（会转成 WPF 投影效果）

长度支持无单位数字、`px`、`pt`、`em`/`rem`（按 13px 基准）。

布局属性的工作方式：
- `gap` 通过给 `StackPanel` 直接子元素补 `Margin` 实现，只在相邻项之间生效（`WrapPanel` 会在右侧和底部都补）；虚拟化列表的项在生成后会自动补上。
- `flex-direction` 修改 `StackPanel` / `WrapPanel` 的 `Orientation`，`row-reverse` / `column-reverse` 不能反向。
- `align-items` 修改子元素在交叉轴上的 `VerticalAlignment` / `HorizontalAlignment`。
- 单边边框：WPF 的 `Border` 只有一种边框颜色，`border-left-color` 会同时改变四边颜色；宽度可以分别设置。

### 3.4 深浅主题

```css
@media (prefers-color-scheme: dark) {
  :root { --bg: #ff14161c; }
  .MutedText { color: #ff9aa4bd; }
}

@media (prefers-color-scheme: light) {
  :root { --bg: #fff7f8fb; }
}
```

启动器切换主题时会重新匹配这些规则。未写媒体查询的规则始终生效。

### 3.5 示例

```css
Window { font-family: "Microsoft YaHei UI"; }

Button { corner-radius: 9px; }
.PrimaryButton {
  background: var(--accent);
  box-shadow: 0 3px 10px rgba(76, 154, 255, 0.35);
}
.IconButton { corner-radius: 999px; }

.SettingsItemCard {
  background: #cc223354;
  border-color: #553f5c8a;
  corner-radius: 10px;
}

#VersionList { background: transparent; }
.MutedText { color: var(--muted); }
```

`corner-radius: 50%` 这类百分比圆角不受支持，请写具体数值（如 `999px`）。

### 3.6 做不到 / 不建议照搬网页写法

WPF 不是浏览器，下面这些属于技术瓶颈或对启动器无意义的写法，已明确不支持：

- **不能把任意面板变成 flex**：`display: flex` 不会改变控件结构；只有 `StackPanel` / `WrapPanel` 能通过 `flex-direction`、`gap`、`align-items` 近似。
- `flex-wrap` 不会把 `StackPanel` 换成 `WrapPanel`；需要换行请直接给 `WrapPanel` 写样式（启动器里列表本身多用 `StackPanel`）。
- `flex`（伸缩比例）、`justify-content`（主轴分布）、`order`、`align-content`：WPF 面板没有等价布局，忽略。
- `resize`（拖拽调整大小）需要自定义拖拽柄，不支持；`box-sizing` 对 WPF 布局无意义。
- `letter-spacing`、`@keyframes`、`transition`、`transform`、`filter`、`animation` 不支持。
- `:hover` / `:active` 等状态伪类不支持（本地值会覆盖控件的悬停触发器，写了反而可能让悬停失效）。
- `input[type="text"]` 这类 HTML 属性选择器不支持；改用 `TextBox`、`ComboBox` 等 WPF 类型选择器。
- `overflow-wrap: anywhere` / `word-break: break-all` 只能近似：WPF 仍按正常断点换行，超长无空格英文可能顶宽。
- `corner-radius: 50%` 这类百分比圆角不支持，请写具体数值（如 `999px`）。
- 直接使用 `Border`、`*` 这类宽泛选择器会作用到控件模板内部，可能让悬停效果失效，建议优先用类型 + 样式键/名称。
- 样式会改动真实控件属性；`width`/`height`/`gap` 等可能挤压原有排版，建议先在测试皮肤里验证。

## 4. 优先级与生效方式

- 主题色：**美化包 CSS 变量 > 美化包 `accentColor` > 设置里的自定义主题色 > 默认主题**。
- 背景：主窗口使用 `windowBackgroundImage` / `windowBackgroundColor`；自绘提示窗与业务对话框使用 `dialogBackgroundImage` / `dialogBackgroundColor`。
- 控件样式：`skin.css` 在加载后立即生效；新打开的窗口与动态创建的控件也会自动套用。
- 加载后点「卸载美化包」会还原所有被改写的属性；下次启动会记住上次启用的包。
- 深浅配色：只要 `windowBackgroundColor`、`dialogBackgroundColor` 或 CSS 的 `--bg` 偏暗，启动器会自动切到深色配色（浅色文字）。

## 5. 校验与安全

- 根目录缺少 `skin.json`、`format` 不匹配、缺少 `name`、颜色非法、样式表/图片路径越界（绝对路径或包含 `..`）都会被拒绝并提示原因。
- 解压时会再次校验目标是否位于包目录内。
- 美化包不会修改游戏文件、不会注入脚本，也不会联网下载资源。
