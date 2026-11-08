# DoL 多版本启动器（DoLLauncher）

面向 Degrees of Lewdity 的多版本管理工具：把不同版本的游戏、各自的模组、存档与设置收在一个界面里，互不干扰。界面布局参考 PCL，桌面端采用 WPF；发布版是单文件 exe，**不依赖 Python，也不需要预装 .NET 运行时**。

> 本仓库只包含启动器源码。游戏本体、模组、图片包与汉化资源不随仓库分发，版权归各自作者所有。

## 它解决什么问题

直接用 `file://` 打开不同版本的游戏 HTML 时，浏览器会把它们视为同一个 origin，存档、模组开关、游戏设置全部共享，换版本就容易串数据。启动器给每个版本分配一个固定的本地端口，用内置静态服务器在 `127.0.0.1:<端口>` 打开游戏 —— **不同端口即不同 origin**，IndexedDB 从此按版本隔离。

## 主要功能

| 模块 | 能力 |
| --- | --- |
| 版本管理 | 扫描、添加、启动 / 停止、导出与导入 `.dolpack`；卡片式列表与运行状态 |
| 模组管理 | 导入或拖拽安装、启停、排序、详情与 README、同名 / 缺前置 / 版本不满足提示 |
| 下载中心 | 游戏本体从汉化仓库 Release 安装；模组市场按索引搜索、筛选、一键装到选中版本 |
| 存档助手 | 同源页面导出 / 导入 / 删除单个存档槽，备份到 `backups/` |
| 备份与诊断 | 整版快照、模组配置导入导出、自动备份、兼容性检查、诊断包 |
| 问题检查 | 模组冲突、前置缺失、版本不匹配、重复模组，分页展示并给出处理操作 |
| 界面 | 深 / 亮 / 跟随系统主题、过渡动画、像素级平滑滚动、自定义主题弹窗与悬停提示 |
| 网络 | GitHub API 统一收敛到 `GetGithubApiAsync`，镜像自动回退，所有请求写入日志 |

## 技术栈

- **.NET 10 / WPF（C#）**，单文件自包含发布（`win-x64`）
- 内置 `TcpListener` 静态服务器，无第三方 Web 依赖
- `System.Text.Json`（解析模组 `boot.json` 时容忍尾随逗号与注释）
- Win32 / DWM API：标题栏主题、边框禁用、DPI 像素对齐
- PowerShell 构建脚本 `build.ps1`

## 目录结构

```
DoL/
├─ dev/                     ← 本仓库（源码）
│  ├─ src/DoLLauncher/      ← WPF 启动器源码
│  ├─ tools/                ← 图标生成脚本、图标帧与 Vrelicon.png（大图标源图）
│  ├─ build.ps1             ← 构建 / 打包
│  └─ README.md
├─ DoLLauncher/             ← 运行时目录（exe、versions、backups、logs）
│  ├─ DoLLauncher.exe
│  ├─ versions/<游戏版本>-<modloader版本>/
│  ├─ backups/
│  └─ logs/
└─ _archives/               ← 历史发布包，不参与运行
```

## 构建

需要 .NET 10 SDK：

```powershell
cd dev
.\build.ps1                  # 生成 exe + 只有启动器的分发包
.\build.ps1 -IncludeVersions # 连本机 versions 一起打包（体积很大）
.\build.ps1 -NoZip           # 只生成 exe
```

产物写入 `dev/_publish/DoLLauncher.exe`，并自动复制到运行时目录 `DoLLauncher/`；分发包输出到 `DoL/DoL-Launcher.zip`。开发者可用 `DoLLauncher.exe --selftest` 跑一次自检，结果写入 `self-test.log`。

## 运行

1. 把 `DoLLauncher/` 整个目录放到**没有中文和空格的路径**；
2. 双击 `DoLLauncher.exe`，左侧选中版本后点「启动游戏」；
3. 启动器在 `127.0.0.1:<端口>` 起内置服务器并用默认浏览器打开，关闭启动器时会询问是否一并停止服务器。

命令行备用方案（需 Python 3 在 PATH 中）：`DoL.bat`、`DoL.bat -List`、`DoL.bat -Stop`。

## 说明与边界

- 启动器只管理本地文件与本地服务器，不抓取、不分发游戏本体或模组资源。
- 浏览器存档只能通过同源页面读写，因此存档相关操作会短暂打开一个页面。
- 启动器无法实时感知外部对 `mods/` 的修改；换端口也不会自动迁移旧的 `file://` 存档，需要用存档助手。
- 仅支持 Windows（WPF + DWM），未适配 macOS / Linux。

## 第三方资源与致谢

- 启动器图标（`Assets/dol.ico`、`Assets/dol-favicon.png`）由 Degrees of Lewdity（作者 Vrelnir）游戏美术资源中的网页小图标，与 DoL 中文维基使用的站点图标去背景合成，仅用于标识本项目。相关图像版权归原权利方所有，**不包含在本仓库的 MIT 许可范围内**。
- 游戏本体、模组、图片包与汉化资源均由各自作者发布，本仓库不再分发。
- 下载中心引用的仓库与市场索引归各自维护者所有。

## 许可证

[MIT](LICENSE)，仅覆盖本仓库源码。游戏本体、模组、图标等第三方资源不在许可范围内，详见上一节。
