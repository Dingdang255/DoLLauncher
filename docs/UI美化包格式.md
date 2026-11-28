# UI 美化包格式（dolskin/1 · dolskin/2 · dolskin/3）

美化包是一个 **zip** 文件，根目录必须包含 `skin.json`，可附带 `skin.css` 与图片资源。启动器只读取清单、样式表与图片，**不执行任意代码、不联网下载资源**；`dolskin/3` 仅在声明式组件里提供受白名单约束的轻量脚本（见 12.5）。

> 相关文档：[常见问题 Q&A](DoLL-QA.md)（含美化包导入、兼容性警告与恢复默认主题）。

- `dolskin/1`、`dolskin/2`、`dolskin/3` 均可加载，按 `skin.json` 的 `format` 字段识别；
- `dolskin/3` 新增的主题、环境层、设置项**全部可选**，旧包行为不变。

## 1. 目录示例

```text
my-skin.zip
├─ skin.json
├─ skin.css              ← 可选；不写时自动查找 skin.css / style.css / styles.css
├─ window-bg.png
├─ dialog-bg.png
├─ images/
│  └─ card-bg.png
└─ ornaments/
   └─ neon.png           ← dolskin/3 九宫格边饰（仅 PNG）
```

加载后会解压到启动器根目录：`skins\<id>\`。

## 2. skin.json

### 2.1 完整示例（dolskin/3）

```json
{
  "format": "dolskin/3",
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
  },
  "defaultTheme": "night",
  "themes": [
    {
      "id": "night",
      "name": "夜色",
      "variables": { "accent": "#FF35E8D5", "bg": "#FF0B1220", "card": "#FF13203A" },
      "texture": "images/night.png",
      "ornament": "ornaments/neon.png",
      "slice": 24
    },
    {
      "id": "amber",
      "name": "琥珀",
      "variables": { "accent": "#FFFFB74A", "bg": "#FF1A1206", "card": "#FF2A1E0C" }
    }
  ],
  "effects": {
    "ambient": { "particles": 24, "veil": true, "sky": true, "opacity": 0.6 },
    "motion": { "panelIn": true, "breathe": true, "hairline": true, "ripple": true }
  },
  "settings": [
    { "key": "theme", "type": "select", "label": "主题", "default": "night", "optionsFromThemes": true },
    { "key": "motion", "type": "toggle", "label": "动效", "default": true },
    { "key": "particles", "type": "select", "label": "环境粒子", "default": "16", "options": ["0", "8", "16", "24"] },
    { "key": "ornament", "type": "toggle", "label": "主题边饰", "default": true }
  ]
}
```

### 2.2 顶层字段

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `format` | 是 | `dolskin/1`、`dolskin/2` 或 `dolskin/3` |
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
| `themes` | 否 | `dolskin/3` 可切换主题列表，见第 3 节 |
| `defaultTheme` | 否 | 默认主题 id；为空或 `auto` 时由平台决定 |
| `effects` | 否 | `dolskin/3` 环境层与动效开关，见第 4 节 |
| `settings` | 否 | `dolskin/3` 美化包设置页的控件声明，见第 5 节 |
| `components` | 否 | `dolskin/3` 声明式组件树（含事件脚本与数据绑定），见第 12 节 |

颜色支持 `#RRGGBB`、`#AARRGGBB`（注意 Alpha 在前，与 WPF 一致），CSS 里还额外支持 `#RGB`、`#ARGB`、`rgb()`、`rgba()` 与命名颜色。

## 3. 主题：`themes[]` 与 `defaultTheme`

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `id` | 是 | 主题标识，同一包内唯一（忽略大小写） |
| `name` | 否 | 设置页下拉框显示名；为空时显示 `id` |
| `variables` | 否 | 该主题的调色板变量，写在与 CSS `:root` 相同的位置 |
| `texture` | 否 | 背景纹理图，包内相对路径；仅在未写顶层 `windowBackgroundImage` 时使用 |
| `ornament` | 否 | 九宫格边饰 PNG，包内相对路径，见第 7 节 |
| `slice` | 否 | 九宫格切片尺寸（像素），默认 24 |

- 主题变量叠加在 `skin.json` 的 `variables` 与 `skin.css` 的 `:root` 之上，切换主题后调色板立即刷新；
- `defaultTheme` 为 `auto` 时跟随启动器深浅模式，**不会读取游戏内时间**；
- 主题最多 16 个，`id` 重复时只保留第一个并写告警。

## 4. 效果开关：`effects{}`

| 字段 | 默认 | 说明 |
| --- | --- | --- |
| `effects.ambient.particles` | 4 | 环境粒子数量，铺满窗口飘落，推荐 `0` / `8` / `16` / `24`，上限 24 |
| `effects.ambient.veil` | `false` | 漂移光带 |
| `effects.ambient.sky` | `false` | 渐变天空 |
| `effects.ambient.opacity` | 0.6 | 环境层整体不透明度，范围 0..1 |
| `effects.motion.panelIn` | `true` | 面板入场动画 |
| `effects.motion.breathe` | `true` | 呼吸灯类动画 |
| `effects.motion.hairline` | `true` | 发丝线动画 |
| `effects.motion.ripple` | `true` | 按压涟漪 |

环境层不拦截鼠标、不改变布局；动效总开关关闭后所有 Storyboard 停止。

## 5. 美化包设置页：`settings[]`

`settings[]` 里的每一项会在「美化包设置」窗口生成一个控件，改动后立即生效并保存。

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `key` | 是 | 设置项标识，同一包内唯一；部分 key 有约定含义（见下） |
| `type` | 否 | `toggle`（默认）/ `select` / `slider` / `color` |
| `label` | 否 | 控件左侧文字；为空时显示 `key` |
| `default` | 否 | 默认值，保留 JSON 原始类型（bool / number / string） |
| `min` / `max` | slider 必填 | 取值范围，`min` 必须小于 `max` |
| `step` | 否 | slider 步长，默认 1 |
| `options` | select 必填 | 选项字符串列表 |
| `optionsFromThemes` | 否 | select 的选项直接取 `themes[]` 的 `id` / `name` |
| `target` | 否 | 形如 `--变量名` 时，把值写成该 CSS 变量的覆盖 |

按 `key` 识别的内置项（包含关键字即可，忽略大小写）：

- `theme`：切换主题（配合 `optionsFromThemes`）；
- 含 `motion`：动效总开关；
- 含 `particle`：环境粒子数量（吸附到 0..24）；
- 含 `ornament`：主题边饰开关。

其余 `key` 只保存值；若写了 `target: "--xxx"`，会作为变量覆盖应用到调色板。

**持久化**：设置保存在 `skins/<id>/settings.json`，结构为 `{ "theme": "...", "values": { "key": "value" } }`，所有值统一按字符串保存。文件缺失或损坏时回退 `default`。

**重置**：
- 「重置本页」删除该皮肤的 `settings.json`，清除变量覆盖，重新应用皮肤默认值与默认主题；
- 「恢复出厂」删除 `settings.json` 并重新启用整个皮肤（主题、环境层、边饰全部回到声明初值）。

未声明 `settings[]` 时窗口显示「该美化包没有可调项」，不报错。

## 6. skin.css：类似 CSS 的控件样式

样式表会在加载美化包后立即作用于启动器的所有窗口、弹窗、动态列表项与控件模板内的元素；卸载时逐项还原。

### 6.1 选择器

| 写法 | 含义 |
| --- | --- |
| `Button` | 所有 `Button` 及其子类（也匹配基类型名，如 `Control`） |
| `.FlatButton` | 使用该样式键的控件（等于 XAML 里的 `x:Key`） |
| `#LaunchButton` | `x:Name` 为 `LaunchButton` 的控件 |
| `TextBlock.MutedText` | 既是 `TextBlock` 又使用 `.MutedText` 样式键 |
| `StackPanel > Button` | 后代组合（`>` 与空格等价） |
| `Button, ComboBox` | 逗号分组 |
| `*` | 所有元素（慎用，会作用到模板内部） |

**状态伪类**（写在选择器末尾，只在状态成立期间覆盖基础规则，退出后恢复）：

| 伪类 | 生效条件 |
| --- | --- |
| `:hover` | 鼠标进入元素 |
| `:pressed` / `:active` | 元素被按下（两者等价） |
| `:selected` | 选中项（列表项、选项卡等） |
| `:checked` | 复选/开关处于勾选态 |
| `:disabled` | `IsEnabled = false` |

```css
.PrimaryButton:hover { background: var(--accent-hover); }
.PrimaryButton:pressed { transform: scale(.96); }
.TextCheck:checked { border-color: var(--accent); }
```

`:focus` 等其它伪类暂不参与匹配（不会导致整条规则失败）。

常用样式键：`FlatButton`、`PrimaryButton`、`IconButton`、`TinyButton`、`DangerButton`、`SettingsItemCard`、`MutedText`、`SectionTitle`、`DarkTextBox`、`NavItem`、`NavSubItem`、`FlatCombo`、`ThinProgress`、`LinkButton`、`TextCheck`、`ModCheck`、`VersionItem`、`ModItem`、`DownloadItem`、`CreditLinkButton`。

### 6.2 变量与调色板

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

### 6.3 支持的属性

- 颜色：`background` / `bg` / `background-color`、`color` / `foreground`、`border-color`、`fill`、`stroke`
- 边框：`border`（宽度 + 颜色简写）、`border-width`、`border-thickness`、`border-left/right/top/bottom`（含 `-width`、`-color`）、`corner-radius` / `border-radius`（按钮等控件会作用到模板内的圆角边框）
- 字体：`font-family`、`font-size`、`font-weight`、`font-style`、`font-stretch`、`line-height`、`text-align`、`text-decoration`
- 尺寸与间距：`width`、`height`、`min-width`、`max-width`、`min-height`、`max-height`、`padding`、`margin`
- 面板布局（仅 `StackPanel` / `WrapPanel`，近似 flex）：`flex-direction: row|column`、`gap` / `row-gap` / `column-gap`、`align-items: start|center|end|stretch`、`order`（整数，仅重排直接子元素）
- 文本流：`white-space: nowrap|normal|pre-wrap`、`overflow-wrap: anywhere`、`word-break: break-word`、`text-overflow: ellipsis`
- 外观：`opacity`、`visibility`、`display`（`none` 隐藏，`flex`/`block` 仅作可见）、`cursor`、`horizontal-align`、`vertical-align`、`overflow: hidden|visible`、`z-index`
- 背景图：`background-image: url("images/card-bg.png")`、`background-size: cover|contain|fill|none`、`background-repeat: no-repeat|repeat`
- 阴影：`box-shadow: 0 4px 12px rgba(0,0,0,0.35)`（会转成 WPF 投影效果）
- 动效：`@keyframes`、`animation` 及长写、`transition` 及长写、`transform`，见 6.5

长度支持无单位数字、`px`、`pt`、`em`/`rem`（按 13px 基准）。

布局属性的工作方式：
- `gap` 通过给 `StackPanel` 直接子元素补 `Margin` 实现，只在相邻项之间生效（`WrapPanel` 会在右侧和底部都补）；虚拟化列表的项在生成后会自动补上。
- `flex-direction` 修改 `StackPanel` / `WrapPanel` 的 `Orientation`，`row-reverse` / `column-reverse` 不能反向。
- `align-items` 修改子元素在交叉轴上的 `VerticalAlignment` / `HorizontalAlignment`。
- 单边边框：WPF 的 `Border` 只有一种边框颜色，`border-left-color` 会同时改变四边颜色；宽度可以分别设置。
- `order` 只对 `StackPanel` / `WrapPanel` 的直接子元素生效；虚拟化列表（`ItemsControl` / `VirtualizingPanel`）内会跳过并写告警，卸载后恢复原始顺序。容器复用会让重排结果错位，因此不做尝试。

### 6.4 深浅主题

```css
@media (prefers-color-scheme: dark) {
  :root { --bg: #ff14161c; }
  .MutedText { color: #ff9aa4bd; }
}

@media (prefers-color-scheme: light) {
  :root { --bg: #fff7f8fb; }
}
```

只支持 `prefers-color-scheme: dark|light` 这一个媒体查询；启动器切换深浅模式时会重新匹配。未写媒体查询的规则始终生效。

### 6.5 动画与过渡

**能参与 `animation` / `transition` 的属性只有下面这些，其余一律不做插值：**

| 类别 | 可用属性 |
| --- | --- |
| 透明度 | `opacity` |
| 变换 | `transform` 及其组成：`translate` / `translateX` / `translateY`、`rotate`、`scale` / `scaleX` / `scaleY` |
| 画刷颜色 | `background` / `background-color` / `bg`、`color` / `foreground`、`border-color`、`fill`、`stroke` |
| 圆角 | `corner-radius` / `border-radius` |

布局与排版属性（`width` / `height` / `min-width` / `max-width` / `min-height` / `max-height` / `margin` / `padding` / `gap` / `font-size` / `order` / `flex-direction` / `align-items` 等）**只直接应用，不做动画**：进入/退出状态或切换主题时会立即跳变到新值，写了 `animation` / `transition` 也不会平滑过渡。

**`@keyframes`**：支持 `from` / `to` / 百分比帧，帧内可写上述渲染安全属性。

```css
@keyframes lantern-breathe {
  from { opacity: 0.65; transform: scale(0.98); }
  50%  { opacity: 1;    transform: scale(1.04); }
  to   { opacity: 0.65; transform: scale(0.98); }
}

.SectionTitle {
  animation: lantern-breathe 3.2s ease-in-out infinite;
}
```

**`animation` 简写**：`animation: <name> <duration> [timing] [delay] [iteration] [direction] [fill-mode]`。

- 时间单位 `ms` / `s`，单条动画时长上限 60s；
- 缓动：`ease`、`linear`、`ease-in`、`ease-out`、`ease-in-out`、`cubic-bezier(...)`、`steps(...)`；
- 循环：数字或 `infinite`；方向：`normal|reverse|alternate|alternate-reverse`；填充：`none|forwards|backwards|both`；
- 长写 `animation-name` / `-duration` / `-delay` / `-timing-function` / `-iteration-count` / `-direction` / `-fill-mode` 同样可用；
- 状态规则（`:hover` / `:pressed` / `:checked` 等）里的 `animation` 只在状态成立期间播放，规则见下方「状态动画」。

**状态动画**：基础规则里的 `animation` 常驻播放；写在状态规则里的 `animation` 按以下规则生效：

- 进入 `:hover` / `:pressed` / `:checked` 等状态时，立即播放该状态声明的 `animation`；
- 退出该状态时停止它，并恢复基础层声明的 `animation`（基础层没有动画时回到基础值）；
- 同一个元素同时命中多条状态 `animation` 时，以**最后声明**的一条为准；
- `animation: none` 表示该状态不播放动画，可用于让元素在退出态保持基础样式。

```css
@keyframes lift {
  from { opacity: .85; transform: translateY(1px) scale(.985); }
  to   { opacity: 1;   transform: translateY(-2px) scale(1.03); }
}

Button:hover { animation: lift .18s ease-out; }
```

关键帧里只写 `opacity` / `transform`；写成布局属性不会产生插值。

**`transition` 简写**：`transition: background 180ms ease-out, transform 120ms ease-out`。

- 属性名可写 `all` 兜底；时长/延迟单位同上；
- 状态切换时从当前值平滑过渡到目标值，未声明过渡的属性瞬变；
- 长写 `transition-property` / `-duration` / `-delay` / `-timing-function` 可用。

**`transform`**：`translate(x[, y])`、`translateX()`、`translateY()`、`rotate(deg|rad)`、`scale(x[, y])`，最多 8 个操作；落到 WPF 的 `RenderTransform`，不触发布局。

```css
.IconButton { transition: background 160ms ease-out, transform 120ms ease-out; }
.IconButton:hover { background: #3335e8d5; }
.IconButton:pressed { transform: scale(.96); }
```

动画运行期间不要再用同属性写另一条互相冲突的声明；卸载美化包或切换主题时会停止全部 Storyboard 并还原原始值。

### 6.6 示例

```css
Window { font-family: "Microsoft YaHei UI"; }

Button { corner-radius: 9px; }
.PrimaryButton {
  background: var(--accent);
  box-shadow: 0 3px 10px rgba(76, 154, 255, 0.35);
  transition: background 180ms ease-out;
}
.PrimaryButton:hover { background: var(--accent-hover); }
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

### 6.7 仍不支持的写法

WPF 不是浏览器，下面的写法属于技术瓶颈或对启动器无意义，继续不支持：

- **不能把任意面板变成 flex**：`display: flex` 不会改变控件结构；只有 `StackPanel` / `WrapPanel` 能通过 `flex-direction`、`gap`、`align-items` 近似。
- `flex-wrap` 不会把 `StackPanel` 换成 `WrapPanel`；需要换行请直接给 `WrapPanel` 写样式。
- `flex`（伸缩比例）、`justify-content`（主轴分布）、`align-content`：WPF 面板没有等价布局，忽略。
- `filter`（模糊/饱和度等）与 `backdrop-filter` 不支持；发光请用 `box-shadow` 近似。
- 伪元素 `::before` / `::after` 不支持；`@font-face`、`@import`、`@supports`、`@container` 等规则不支持。
- `resize`（拖拽调整大小）需要自定义拖拽柄，不支持；`box-sizing` 对 WPF 布局无意义。
- 相邻/兄弟组合（`+`、`~`）、HTML 属性选择器（`input[type="text"]`）不支持；改用 `TextBox`、`ComboBox` 等 WPF 类型选择器。
- 布局属性（`width`/`height`/`margin`/`padding`/`gap`/`font-size` 等）不能参与 `animation` / `transition` 的插值。
- `overflow-wrap: anywhere` / `word-break: break-all` 只能近似：WPF 仍按正常断点换行，超长无空格英文可能顶宽。
- 直接使用 `Border`、`*` 这类宽泛选择器会作用到控件模板内部，建议优先用类型 + 样式键/名称。
- 样式会改动真实控件属性；`width`/`height`/`gap` 等可能挤压原有排版，建议先在测试皮肤里验证。

## 7. 环境层与主题边饰

### 7.1 环境层

由 `effects.ambient` 控制，插入在主窗口内容下层：

- 粒子：铺满窗口的雪花 / 光点，`0` / `8` / `16` / `24` 四档推荐值（上限 24），0 表示不创建粒子并停止对应动画；
- `veil`：缓慢漂移的光带；
- `sky`：顶部到底部的渐变天空；
- `opacity`：整体不透明度。

环境层 `IsHitTestVisible = false`，不拦截鼠标、不改变布局；动效关闭时完全停止。

### 7.2 主题边饰（九宫格）

在主题里写 `ornament` 指向包内图片，`slice` 指定切片像素：

```json
{ "id": "night", "name": "夜色", "ornament": "ornaments/neon.png", "slice": 24 }
```

- 支持 PNG 与 SVG（SVG 见第 14 节），建议方形素材，四角为不透明图案、中心透明；
- 图片按 `slice` 切成九宫格：四角固定尺寸，四边平铺拉伸，绘制在窗口内容四周；
- `slice` 默认 24，超过图片短边一半时自动收窄；文件缺失或读取失败时静默跳过，不影响其它样式；
- 设置页的「主题边饰」开关（key 含 `ornament`）可整体隐藏。

## 8. 优先级与生效方式

- 主题色：**设置页变量覆盖 > 主题 `variables` > 美化包 CSS 变量 > 美化包 `accentColor` > 设置里的自定义主题色 > 默认主题**；
- 背景：`windowBackgroundImage` 优先，未写时使用当前主题的 `texture`，都没有时用 `windowBackgroundColor`；自绘提示窗与业务对话框使用 `dialogBackgroundImage` / `dialogBackgroundColor`；
- 控件样式：`skin.css` 在加载后立即生效；新打开的窗口与动态创建的控件也会自动套用；
- 状态样式：状态成立期间覆盖基础规则，退出后恢复；
- 加载后点「卸载美化包」会还原所有被改写的属性、停止动画、移除环境层与边饰；下次启动会记住上次启用的包；
- 深浅配色：只要 `windowBackgroundColor`、`dialogBackgroundColor` 或 CSS 的 `--bg` 偏暗，启动器会自动切到深色配色（浅色文字）。

## 9. 版本兼容与限制

- `dolskin/1` / `dolskin/2` 不含主题、环境层与设置项，行为与旧版一致；新增字段全部可选；
- `defaultTheme: "auto"` 表示跟随启动器深浅模式，**不读取游戏时间**，也不按时段自动换主题；
- 美化包在 WPF 启动器与安卓（MAUI）启动器内都生效，两端共用同一份 `skin.json` / `skin.css` / `settings.json`；差异见 9.1；
- 不执行任意脚本、不联网、不修改游戏文件；`components[].events` 只运行第 12.5 节的轻量脚本。

### 9.1 安卓（MAUI）平台差异

安卓端支持主色 / 页面背景、CSS 静态属性、`@keyframes` 动画、`transition`、`transform`、状态伪类、声明式组件、`events` 轻量脚本与 `bind` 数据绑定，语义与 WPF 对齐；以下为无法等价的降级：

| 能力 | 安卓端行为 |
| --- | --- |
| 组件宿主定位 | `#Name` 对应 `StyleId` 或 XAML `x:Name`；主界面侧边栏提供固定插槽 `#SkinHost`；类型名支持 WPF 习惯写法（`StackPanel` → 垂直/水平 StackLayout、`TextBlock` → Label、`TextBox` → Entry 等）；`Type.Class` 对应 `StyleClass`；`slot: start/end` 要求宿主是布局容器（Grid / StackLayout 等），`before/after` 要求父级是布局容器 |
| 虚拟化列表 | `CollectionView` / `ListView` 内的宿主**跳过组件插入**并写告警（与 WPF 一致，避免复用行错乱） |
| 状态伪类 | 无鼠标悬停，`:hover` 规则不激活；`:pressed` 仅 `Button` 有效；`:checked` 对应 Switch / CheckBox；`:disabled` 对应 `IsEnabled`；`:selected` 暂不激活 |
| `particles` / `ornament` | 只保存设置值，没有粒子层与边饰渲染；`setParticles` / `setOrnament` 动作会写一条告警 |
| 过渡与动画 | 用 16ms 定时器逐帧写入属性；`cubic-bezier(...)` / `steps(...)` 统一近似为缓出曲线；受设置项「界面过渡动画」总开关控制 |
| `transform` | `scale(x, y)` 只取 X 分量（MAUI 只有单一 `Scale`）；`translate` / `rotate` 正常 |
| `background-image` | 依赖当前 MAUI 版本的 `ImageBrush`，不支持时写告警并保留背景色 |
| `Slider` 的 `step` | MAUI 滑块没有吸附刻度，按连续值处理 |
| 动画关键帧属性 | 白名单：`opacity` / `transform` / 背景、前景、描边颜色 / `corner-radius` |


## 10. 校验与安全

- 根目录缺少 `skin.json`、`format` 不受支持、缺少 `name`、颜色非法、样式表/图片/纹理/边饰路径越界（绝对路径或包含 `..`）会被拒绝并提示原因。
- 解压时会再次校验目标是否位于包目录内。
- 可选字段超限只写中文告警并跳过该项，不整包拒绝：主题 ≤ 16、`@keyframes` 关键帧总数 ≤ 64、设置项 ≤ 32、粒子 ≤ 24、单条动画/过渡时长 ≤ 60s、`transform` 操作 ≤ 8、变量 ≤ 200 条、组件 ≤ 100、组件嵌套 ≤ 4、单组件 `props` ≤ 32、单组件 `events` ≤ 8、单组件 `bind` ≤ 8、单条绑定 ≤ 512 字符。
- 轻量脚本上限：源码 ≤ 16KB、语句 ≤ 200、表达式嵌套 ≤ 32、函数参数 ≤ 6；无法访问文件、网络、注册表或反射。

## 11. 示例皮肤

- `docs/samples/sample-skin/`：`dolskin/1` 基础示例（背景、主题色、静态控件样式）；
- `docs/samples/lantern-skin/`：`dolskin/3` 示例（两套主题、呼吸灯、`:hover` / `:pressed`、粒子、九宫格边饰、设置项、声明式组件）。新增组件演示：切换主题按钮（轻量脚本 + 数据绑定）、动效 Toggle（`bindTo` 回写设置）、粒子 Slider、版本/模组统计 Label。
- `docs/samples/lantern-skin-android/`：`dolskin/3` 安卓端示例，组件插入主界面侧边栏的 `#SkinHost` 插槽，演示数据绑定、按钮脚本、`bindTo` 回写、按压过渡与 `@keyframes` 呼吸动画。

## 12. 声明式组件 `components[]`

`components[]` 用纯 JSON 描述「把哪个控件插到哪个插槽」，启动器据此创建真实控件（WPF 与 MAUI 各自映射，差异见 9.1）。
**不联网、不读取包外资源**；`dolskin/3` 允许在组件上声明受白名单约束的 `events` 轻量脚本与 `bind` 数据绑定（见 12.5 / 12.6），卸载美化包时插入的节点会被整体移除。

```json
{
  "components": [
    {
      "id": "lantern-badge",
      "host": "#LaunchBtn",
      "slot": "after",
      "widget": "Badge",
      "class": "skin-badge",
      "props": { "text": "灯台" }
    },
    {
      "id": "title-card",
      "host": "#TitleText",
      "slot": "after",
      "widget": "Card",
      "class": "skin-card",
      "props": { "padding": "8 10" },
      "children": [
        { "id": "title-card-text", "widget": "Label", "class": "skin-card-text", "props": { "text": "灯台夜幕" } }
      ]
    }
  ]
}
```

### 12.1 字段

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `id` | 是 | 组件标识，同一包内唯一；用于去重（`(host, id)` 相同只插入一次） |
| `host` | 是 | 宿主写法，见 12.2 |
| `slot` | 否 | `before` / `after` / `start` / `end`，默认 `end` |
| `widget` | 否 | 控件类型，默认 `Label`，白名单见 12.3 |
| `class` | 否 | 皮肤样式类，等价于 CSS 的 `.类名`，多个用空格分隔 |
| `props` | 否 | 控件属性，见 12.4 |
| `children` | 否 | 子组件，仅 `Card` / `Row` / `Column` 使用 |
| `events` | 否 | 事件脚本，键为 `click` / `changed`，见 12.5 |
| `bind` | 否 | 数据绑定，键为属性名，见 12.6 |

### 12.2 host 与 slot

| host 写法 | 匹配规则 |
| --- | --- |
| `#LaunchBtn` | 元素的 `x:Name`（忽略大小写） |
| `Button` | 元素类型名（含基类，如 `Control`） |
| `TextBlock.MutedText` | 类型 + 样式键/皮肤类 |

`slot` 语义：

- `before` / `after`：插到宿主在**父面板**中的前 / 后一位，宿主父级必须是 `Panel`（如 `Grid`、`StackPanel`、`WrapPanel`、`DockPanel`）；
- `start` / `end`：宿主**自身必须是 `Panel`**，插到其子元素的最前 / 最后。

宿主位于虚拟化列表（`ListBox` / `ItemsControl` 开启虚拟化，即内部使用 `VirtualizingPanel`）内时**不会插入**，日志会写一条告警；`order` 重排在虚拟化面板内同样跳过。原因是虚拟化容器会被回收复用，插入的节点或重排结果在滚动后可能错位、重复甚至挂到别的数据项上。需要固定展示的内容请放到虚拟化列表之外的宿主上。

### 12.3 widget 白名单

| widget | 生成的控件 |
| --- | --- |
| `Label` | `TextBlock`（`text`） |
| `Badge` | `Border` + `TextBlock`（`text`） |
| `Divider` | `Border`（高 1，左右外边距 6） |
| `Spacer` | 空白 `FrameworkElement`（`width` / `height`） |
| `Image` | `Image`（`src` 为包内安全相对路径） |
| `Icon` | `TextBlock`（Segoe Fluent Icons / Segoe MDL2 Assets，`glyph`） |
| `ProgressBar` | `ProgressBar`（`Maximum=1`，高 6，`value` 0..1；`2..100` 自动除以 100） |
| `Card` | `Border` + 垂直 `StackPanel` |
| `Row` / `Column` | 水平 / 垂直 `StackPanel` |
| `Banner` | `Border` + `TextBlock`（默认内边距 10,6） |
| `Button` | `Button`（`text`；点击触发 `events.click`） |
| `Toggle` | 开关（`text` / `checked`；变化触发 `events.changed`） |
| `Slider` | 滑块（`min` / `max` / `step` / `value`；变化触发 `events.changed`） |
| `Input` | 单行输入框（`text`；变化触发 `events.changed`） |
| `Select` | 下拉框（`options` 英文逗号分隔、`value`；变化触发 `events.changed`） |

白名单外的 widget 会被忽略并写告警。

### 12.4 props

| 键 | 适用 | 说明 |
| --- | --- | --- |
| `text` | Label / Badge / Banner / Button / Toggle / Input / Select | 文本 |
| `glyph` | Icon | 图标字形 |
| `value` | ProgressBar / Slider / Input | 进度或当前值；ProgressBar 为 0..1，大于 1 且不超过 100 时按百分比处理 |
| `checked` | Toggle | 勾选态，`true` / `false` |
| `min` / `max` / `step` | Slider | 最小值 / 最大值 / 步长 |
| `options` | Select | 选项列表，英文逗号分隔，如 `0,8,16,24` |
| `bindTo` | 交互组件 | 回写位置：`settings.<key>` 或 `vars.<name>`，见 12.7 |
| `src` | Image | 包内相对路径，支持位图与 `svg`；不能是绝对路径或包含 `..` |
| `background` | 面板 / 卡片 / 等 | 背景色 |
| `foreground` | Label / Icon / Badge / Banner | 文字颜色 |
| `border-color` | Border 类 | 边框色 |
| `corner-radius` | Border 类 | 圆角 |
| `padding` | Border / Control | 内边距，支持 `8` / `8 12` / `8 12 8 12` |
| `margin` | 全部 | 外边距，写法同上 |
| `width` / `height` | 全部 | 尺寸 |
| `font-size` / `font-weight` | 文本类 | 字号（px）与字重（`normal` / `medium` / `semibold` / `bold` 或 400/600/700） |
| `opacity` | 全部 | 0..1 |

颜色与尺寸可写 `var(--accent)`、`var(--radius)`，按当前主题变量解析。
白名单外的 props 会被忽略并写一次告警。

### 12.5 轻量脚本 `events`

```json
"events": { "click": "setTheme('amber');" }
```

| 事件 | 触发时机 |
| --- | --- |
| `click` | 按钮点击，或带点击语义的组件被点击 |
| `changed` | Toggle / Slider / Input / Select 的值发生变化 |

`changed` 只在用户操作时触发；脚本里**没有**内置 `value` 变量，控件值请通过 `props.bindTo` 回写后再用 `{{ settings.xxx }}` / `{{ vars.xxx }}` / `getVar('xxx')` 读取。

**语句**

- 语句以 `;` 结尾，代码块用 `{ }`；
- `let 名称 = 表达式;` 定义局部变量；
- `名称 = 表达式;` 修改局部变量；名称不存在时等价于 `setVar(名称, 值)`；
- `if (条件) { ... } else if (条件) { ... } else { ... }`；
- 表达式语句：`toast('已切换');`；
- **没有**循环（`for` / `while`）、函数定义、`return`、`class`、注释与多行字符串。

**表达式**

- 字面量：数字（`1`、`0.5`）、字符串（单/双引号，支持 `\n` `\t` `\r`）、`true` / `false` / `null`；
- 运算符：`+ - * / %`、`== != < <= > >=`、`&& || !`、三元 `? :`、括号；`+` 只要有一侧是文本就按拼接处理；
- 数据路径：直接写内置路径（如 `version.count`）或局部变量名；
- 纯函数（无副作用）：

| 函数 | 说明 |
| --- | --- |
| `getVar(name)` | 读取运行期变量 `vars.<name>` |
| `str(v)` | 转文本 |
| `num(v[, fallback])` | 转数字；文本无法解析时返回 `fallback`（默认 0） |
| `bool(v)` | 转布尔 |
| `len(v)` | 文本长度 |
| `lower(v)` / `upper(v)` | 转小写 / 大写 |
| `round(v[, digits])` | 四舍五入，`digits` 取 0..6 |
| `clamp(v, min, max)` | 夹在区间内 |
| `min(...)` / `max(...)` | 取最小 / 最大 |
| `coalesce(...)` | 返回第一个非空值 |

**白名单动作**（其它函数名不会产生副作用）

| 动作 | 参数 | 说明 |
| --- | --- | --- |
| `setTheme(id)` | 主题 id | 切换到 `themes[]` 中的主题；不存在时忽略并写日志 |
| `setMotion(bool)` | 布尔 | 动效总开关 |
| `setParticles(n)` | 数字 | 环境粒子数量，吸附到 0..24 |
| `setOrnament(bool)` | 布尔 | 主题边饰开关 |
| `openPage(name)` | `version` / `download` / `backup` / `story` / `diagnostic` / `settings` | 切换主页面 |
| `openSettings()` | 无 | 打开美化包设置窗口 |
| `show(id)` / `hide(id)` | 组件 id | 显示 / 隐藏同包内的组件 |
| `toggle(id)` | 组件 id | 切换组件可见性 |
| `setVar(name, value)` | 名称、值 | 写运行期变量，供 `{{ vars.name }}` 与 `getVar()` 读取 |
| `launch()` | 无 | 启动当前选中的游戏版本 |
| `openDir(name)` | `version` / `skin` / `mods` | 打开当前版本目录 / 美化包目录 / 模组目录，不接受绝对路径 |
| `toast(text)` | 文本 | 写一条启动器日志（不弹窗，避免打断操作） |

**限制**：单段脚本 ≤ 16KB、语句 ≤ 200、表达式嵌套 ≤ 32、函数参数 ≤ 6；无法访问文件、网络、注册表或反射。脚本解析失败时写告警并跳过该事件，控件仍会插入；运行时异常只写日志。

下面是一个可直接放进 `components[]` 的切主题按钮：

```json
{
  "id": "theme-switch",
  "host": "#LaunchBtn",
  "slot": "after",
  "widget": "Button",
  "class": "skin-action",
  "props": { "text": "切换主题" },
  "bind": { "text": "主题：{{ theme.name }}" },
  "events": {
    "click": "if (theme.id == 'cyberpunk') { setTheme('amber'); toast('已切换到琥珀灯'); } else { setTheme('cyberpunk'); toast('已切换到赛博夜'); }"
  }
}
```

### 12.6 数据绑定 `bind`

```json
"bind": {
  "text": "版本 {{ version.count }} 个",
  "visible": "{{ version.count > 0 }}"
}
```

- 模板用 `{{ 表达式 }}`，一条绑定里可以出现多个占位符；
- **只读**：绑定里不允许动作，写了 `setTheme(...)` 也不会执行（按 null 处理）；
- 占位符外的文本原样输出，表达式结果为 null 时输出空串；
- 数据变化后自动重算：切换主题、修改设置、`setVar`、交互控件回写、版本与模组状态变化都会触发；
- 单条绑定 ≤ 512 字符；缺少右花括号 `}}` 时该条绑定被忽略并写告警。

**可绑定的属性**

| 属性 | 适用 | 说明 |
| --- | --- | --- |
| `text` | 文本类 / Button / Toggle / Input | 文本模板 |
| `visible` | 全部 | 布尔，`false` 时隐藏且不占位 |
| `enabled` | 交互组件 / 全部 | 布尔，`false` 时禁用 |
| `value` | Toggle / Slider / ProgressBar / Input | 数字或布尔：Toggle 表示勾选态，ProgressBar 为 0..1 |
| `background` | 面板 / 卡片 / 控件 | 颜色表达式，支持 `var(--x)` |
| `foreground` | 文本类 / 图标 | 文字或图标颜色 |

**内置数据路径**

| 路径 | 类型 | 说明 |
| --- | --- | --- |
| `version.count` | 数字 | 已安装版本数量 |
| `version.name` | 文本 | 当前选中版本名 |
| `version.gameVersion` | 文本 | 当前版本的游戏版本号 |
| `version.running` | 布尔 | 当前版本是否运行中 |
| `mods.total` | 数字 | 模组总数 |
| `mods.enabled` | 数字 | 已启用模组数 |
| `theme.id` / `theme.name` | 文本 | 当前主题 id / 显示名 |
| `theme.count` | 数字 | 主题数量 |
| `motion` | 布尔 | 动效总开关 |
| `particles` | 数字 | 当前粒子数量 |
| `ornament` | 布尔 | 主题边饰开关 |
| `skin.id` / `skin.name` | 文本 | 当前美化包 id / 名称 |
| `app.version` | 文本 | 启动器版本 |
| `vars.<name>` | 任意 | 运行期变量（`setVar` / 赋值产生） |
| `settings.<key>` | 任意 | 美化包设置项，见第 5 节 |

未知路径返回 null：文本属性输出空串，布尔属性按 `false` 处理。

### 12.7 交互组件

| widget | 可用 props | 说明 |
| --- | --- | --- |
| `Button` | `text` | 点击触发 `events.click` |
| `Toggle` | `text`、`checked` | 勾选态变化触发 `events.changed` |
| `Slider` | `min`、`max`、`step`、`value` | 值变化（拖动或键盘）触发 `events.changed` |
| `Input` | `text` | 文本每次变化触发 `events.changed` |
| `Select` | `options`、`value` | 选项变化触发 `events.changed` |

- `options` 用英文逗号分隔，如 `"options": "0,8,16,24"`；单个选项文本不能包含逗号；
- 所有交互组件都支持 `props.bindTo`，写入位置只有两种：
  - `settings.<key>`：写入美化包设置值。命中内置 key（`theme` / `motion` / `particle` / `ornament`）时立即生效，并持久化到 `skins/<id>/settings.json`（统一按字符串保存）；
  - `vars.<name>`：写入运行期变量，供 `{{ vars.name }}` 与 `getVar('name')` 读取，关闭启动器后丢失；
- 其它写法（绝对路径、未知前缀）会被忽略并写告警；
- 回写后会重新计算该组件及所有引用同一路径的 `bind`；
- `changed` 脚本里拿不到控件值，需要值时应读取 `bindTo` 回写后的 `vars.*` / `settings.*`。

```json
{
  "id": "motion-toggle",
  "host": "#LaunchBtn",
  "slot": "after",
  "widget": "Toggle",
  "props": { "text": "动效", "bindTo": "settings.motion" },
  "bind": { "value": "{{ motion }}" },
  "events": { "changed": "toast('动效设置已更新');" }
}
```

### 12.8 样式与限制

- 组件节点会带上 `class` 里的皮肤类，`skin.css` 中的 `.skin-badge { ... }` 会立即生效；
- 事件脚本与数据绑定都在启动器内解释执行，不加载外部程序集；脚本或绑定失败只影响对应项；
- 嵌套深度 ≤ 4，组件总数 ≤ 100，单个组件 `props` ≤ 32、`events` ≤ 8、`bind` ≤ 8；
- `id` 为空或重复、`host` 为空或写法非法、`slot` / `widget` 不支持时，只写中文告警并跳过该项，不会拒绝整个美化包；
- `before` / `after` 找不到父面板、`start` / `end` 宿主不是面板、图片读取失败时静默跳过；
- 重复应用（切换主题、窗口刷新）不会重复插入；卸载 / 切换皮肤时全部移除。

## 13. 底图导入（窗口 / 按钮 / 卡片）

美化包设置窗口提供三类底图的导入 / 移除，导入后**立即生效**并写入 `skins/<id>/settings.json`，下次启动自动恢复。

| 底图 | 作用范围 | 绘制方式 |
| --- | --- | --- |
| 窗口背景图 | 主窗口背景层（优先于主题 `texture` 与 `windowBackgroundImage`） | `UniformToFill` 铺满，保持比例裁切 |
| 按钮底图 | 所有 `Button`（含 `FlatButton` / `PrimaryButton` 等） | `UniformToFill`；悬停 / 按下时按模板临时覆盖，松开后回到底图 |
| 卡片底图 | 全局 `CardBrush` 资源（列表卡片、设置卡片等） | 平铺纹理：相对视口 `0 0 0.5 0.5`，按 50% 尺寸重复 |

**存放位置**：`skins/<id>/custom/`，文件名固定为 `window.*` / `button.*` / `card.*`，扩展名取导入图片的原扩展名。

**支持格式**：`png` / `jpg` / `jpeg` / `webp` / `bmp`（WPF 可解码的位图）。重复导入会先删除同类的旧文件。

**持久化字段**（`skins/<id>/settings.json`）：

```json
{
  "theme": "night",
  "values": { "...": "..." },
  "windowImage": "custom/window.png",
  "buttonImage": "custom/button.png",
  "cardImage": "custom/card.png"
}
```

- 路径必须是皮肤目录内的安全相对路径：拒绝绝对路径、URL（`http` / `data:`）与包含 `..` 的片段；
- 文件缺失或解码失败时只写一条日志告警并跳过该类底图，不影响其它样式与启动；
- 切换主题 / 重新启用皮肤后底图仍保留，并覆盖主题背景与调色板中的 `CardBrush`；
- 「移除」删除对应文件并清空字段；「重置本页」与「恢复出厂」会删除整个 `skins/<id>/custom/` 目录，窗口背景、按钮与卡片恢复主题原样（不会删除皮肤包自带的图片）。

## 14. SVG 支持

美化包内的 `.svg` 可直接用于：

- 主题边饰：`themes[].ornament` 指向 `.svg`；
- 声明式组件 `Image`：`props.src` 指向 `.svg`。

启动器内置受限 SVG 渲染器（WPF `Geometry` 绘制后栅格化为位图），不引入第三方依赖，也不执行任何脚本。

### 14.1 支持的元素与属性

| 类别 | 支持内容 |
| --- | --- |
| 元素 | `<svg>`、`<g>`、`<path>`、`<rect>`（含 `rx` / `ry`）、`<circle>`、`<ellipse>`、`<line>`、`<polyline>`、`<polygon>` |
| 画布 | `viewBox`、`width` / `height`、`preserveAspectRatio`（默认等比居中 meet；`none` 拉伸） |
| 样式 | `fill`（默认黑）、`fill="none"`、`stroke`（默认无）、`stroke-width`（默认 1）、`opacity` / `fill-opacity` / `stroke-opacity`、`fill-rule="evenodd"`、`stroke-linecap`、`stroke-linejoin`；`style="fill:...;stroke:..."` 优先于同名属性 |
| 颜色 | `#RGB`、`#RRGGBB`、`rgb(r,g,b)`、`rgba(...)`、WPF 命名色、`currentColor`（回退白色） |
| 变换 | `translate`、`scale`、`rotate(angle cx cy)`、`matrix(a,b,c,d,e,f)`，可多个组合；`<g>` 上的变换向下继承 |

`<path>` 的 `d` 使用 WPF 路径语法（覆盖 SVG 常用写法）。

### 14.2 不支持

脚本（`<script>`）、外部引用（`xlink:href` / `<image>`）、`<foreignObject>`、`<use>`、渐变、滤镜、文本（`<text>`）、蒙版与裁剪路径。

- 出现 `<script>` / `<foreignObject>` / 外部引用时**整份拒绝**；
- 其他不支持的元素或属性被跳过，并在 `launcher.log` 里对同类问题告警一次，不影响其它样式。

### 14.3 尺寸与安全上限

- 主题边饰以 **1024px** 栅格化后再按 `slice` 切九宫格；组件 `Image` 以 **512px** 栅格化；
- 位图的逻辑尺寸保持 SVG 自身的 `width` / `height`（未写时用 `viewBox`），因此 `slice` 与组件 `width` / `height` 按设计尺寸生效；
- 文件 ≤ 2MB、元素总数 ≤ 5000、单个 `path` 的 `d` ≤ 100000 字符，超限直接拒绝；
- 渲染失败只记一条「SVG 渲染失败」日志并回退为不显示，不影响其它样式与启动。


