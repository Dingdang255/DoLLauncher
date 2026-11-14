# UI 美化包格式（dolskin/1）

美化包是一个 **zip** 文件，根目录必须包含 `skin.json`，可附带若干图片资源。启动器只读取清单与图片，不执行任何脚本。

## 1. 目录示例

```text
my-skin.zip
├─ skin.json
├─ window-bg.png
└─ dialog-bg.png
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
  "dialogBackgroundColor": "#FF242426"
}
```

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `format` | 是 | 固定为 `dolskin/1` |
| `id` | 否 | 安装目录名；留空时依次使用 `name`、zip 文件名 |
| `name` | 是 | 美化包显示名称 |
| `author` / `version` / `description` | 否 | 设置页展示用 |
| `accentColor` | 否 | 覆盖主题主色；为空表示沿用「自定义主题色 / 默认主题」 |
| `windowBackgroundImage` | 否 | 主窗口背景图（包内相对路径） |
| `windowBackgroundColor` | 否 | 主窗口背景底色；没有背景图时使用 |
| `dialogBackgroundImage` | 否 | 弹窗背景图（包内相对路径） |
| `dialogBackgroundColor` | 否 | 弹窗背景底色；没有背景图时使用 |

颜色支持 `#RRGGBB` 或 `#AARRGGBB`（十六进制）。

## 3. 优先级与生效方式

- 主题色：**美化包 `accentColor` > 设置里的自定义主题色 > 默认主题色**。
- 背景：主窗口使用 `windowBackgroundImage` / `windowBackgroundColor`；自绘提示窗与三个业务对话框使用 `dialogBackgroundImage` / `dialogBackgroundColor`。
- 加载后立即生效，点「卸载美化包」恢复默认外观；下次启动会记住上次启用的包。
- 深浅配色：只要 `windowBackgroundColor` 或 `dialogBackgroundColor` 的感知亮度低于 0.5，启动器会自动切到深色配色（浅色文字），避免深底深字看不清；卸载后按设置里的主题模式恢复。

## 4. 校验与安全

- 根目录缺少 `skin.json`、`format` 不匹配、缺少 `name`、颜色非法、图片路径越界（绝对路径或包含 `..`）都会被拒绝并提示原因。
- 图片路径只允许包内相对路径，解压时会再次校验目标是否位于包目录内。
- 美化包不会修改游戏文件、不会注入脚本，也不会联网下载资源。
