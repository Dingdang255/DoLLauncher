# DoLLauncher 验证速查

改完代码、构建通过后，按这份清单自己走一遍。不要派 subagent 从 PowerShell 起步，太绕。

## 1. 构建与自检（须用户先许可构建）
```powershell
# cwd = E:\h\DoL
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
Remove-Item .\self-test.log -ErrorAction SilentlyContinue; Start-Process .\DoLLauncher.exe -ArgumentList '--selftest' -Wait; Get-Content .\self-test.log -Tail 3
# 期望末行 SELFTEST-PASS；产物 E:\h\DoL\DoLLauncher.exe + E:\h\DoL-Launcher.zip
```

## 2. GUI 验证（computer-use，经 Exec）
```js
async function cu(tool_name, args) { return await tools.run_mcp({ server_name: "ide_mcp.config.ext.computer-use", tool_name, args }); }
await cu("launch_app", { app: "E:\\h\\DoL\\DoLLauncher.exe" });
const apps = await cu("list_apps");                 // 取 pid
const st = await cu("get_app_state", { pid });      // 返回 UI 树 + 截图
// 点击：cu("click", { pid, element_id })；双击加 clickCount: 2；关详情窗 cu("press_key", { pid, key: "escape", element_id: "0" })
```
- 坐标是窗口 0–1000 分数制（0,0 左上，500,500 中心），**不是像素**。
- 每次点击后重新取 `get_app_state`，别复用旧 element_id；需要完整树传 `disableDiff: true`。
- 下拉展开会生成独立弹窗，先确认候选项文本再 Esc 关闭。
- 像素级对齐别靠肉眼：`Graphics.CopyFromScreen` 抓窗口 → PIL 扫描“首个非背景像素列”，空态与输入态两次比对。

## 3. 常走路径
- 下载中心 → 模组市场：搜 `枯木`（应出 8 个版本）、搜 `RAY`（点第二项出现「包型」6 项）、搜 `神殿`（应经简介命中 2 条）。
- 双击列表项打开详情窗：应有「打开主页」「包型」「下载并安装」「安装到版本 / 版本」；Esc 关闭。
- 版本管理：模组区标题行不应有 Ctrl/Shift 文字，按钮 52px 等宽。
- 设置：左面板应有「启动时预取索引」「模组市场索引源」「模组下载源」「GitHub 接口源」；改完读 `E:\h\DoL\launcher-versions.json` 的 `general.marketSource` / `general.prefetch` / `general.modDownloadSource` / `general.githubApiSource` 确认落盘。
- 图标：`python tools\icon_tool.py extract` 导帧到 `tools\icon-frames\`；`build` 重建 ico（≤32 用 16px 原图最近邻，>32 用 `Vrelicon.png`）。

## 4. 静态核对习惯
- 只读源码，不凭印象：路径 `e:\h\DoL\src\DoLLauncher\`，重点 `Services\DownloadService.cs`、`Services\ModService.cs`、`MainWindow.xaml(.cs)`、`Theme.xaml`。
- 图标：`Assets\dol.ico` 7 帧（16/24/32 用 16px 原图，48+ 由 `Vrelicon.png` 缩放），`Assets\dol-favicon.png` 64×64，UI 用 `<Image RenderOptions.BitmapScalingMode="HighQuality">`。
- 分发包抽查：解压到 `%TEMP%`，根目录不应有 `plugins`。

## 5. 汇报格式（简短）
```
结论：PASS / FAIL / PARTIAL / BLOCKED
逐项：| 项 | 结果 | 证据（界面文本或 文件:行） |
未覆盖 / 风险：一句话
```
只说当轮改动相关的项，不贴大段源码，控制在 300 字内。
