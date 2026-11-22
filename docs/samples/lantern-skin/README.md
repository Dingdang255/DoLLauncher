# 灯台夜幕（lantern-skin）

`dolskin/3` 示例美化包：两套主题、呼吸灯、按压反馈、环境粒子与九宫格边饰（PNG / SVG 各一）。

## 加载方式

- 把本目录**内容**打包成 zip（zip 根目录直接包含 `skin.json`、`skin.css`、`ornaments/`），在启动器「UI 美化包」卡片里选择该 zip；
- 或直接放到启动器根目录 `skins/lantern-skin/`（含 `skin.json`）后重新加载美化包。

加载后会解压/读取到 `skins/<id>/`，样式与动效立即生效，卸载时逐项还原。

## 设置页能调什么

| 控件 | key | 说明 |
| --- | --- | --- |
| 主题 | `theme` | 选项来自 `themes[]`（赛博夜 / 琥珀灯），切换后调色板立即刷新 |
| 动效 | `motion` | 关闭后呼吸灯、面板入场、发丝线与按压涟漪停止 |
| 环境粒子 | `particles` | 0 / 4 / 8 档，0 表示不渲染粒子并停止对应动画 |
| 主题边饰 | `ornament` | 显示或隐藏当前主题的九宫格边饰（赛博夜为 `ornaments/lantern.svg`，琥珀灯为 `ornaments/lantern.png`） |

调整结果写入 `skins/lantern-skin/settings.json`；「重置本页」删除保存值并回到皮肤默认，「恢复出厂」重载整个皮肤。

## 文件说明

- `skin.json`：清单、主题、环境层与设置项声明；
- `skin.css`：`@keyframes lantern-breathe`、按钮 `:hover` / `:pressed` 过渡与按压缩放、浅色模式变量；
- `ornaments/lantern.png`：96×96、四周 24px 青色描边的九宫格边饰，`slice: 24`，中心透明；
- `ornaments/lantern.svg`：120×120 矢量边饰（青色描边 + 四角菱形角饰），同样按 `slice: 24` 切分；
- `skin.json` 的 `components[]` 还演示了 `Image` 组件直接引用 `ornaments/lantern.svg`。

格式细节见 `docs/UI美化包格式.md`。
