# DoL 多版本启动器（DoLLauncher）

面向 Degrees of Lewdity 的多版本管理工具：把不同版本的游戏、各自的模组、存档与设置收在一个界面里，互不干扰。界面布局参考 PCL，桌面端采用 WPF，Android 端采用 .NET MAUI；发布版无需预装 Python，桌面版是单文件 exe，**也不需要预装 .NET 运行时**。

> 本仓库只包含启动器源码。游戏本体、模组、图片包与汉化资源不随仓库分发，版权归各自作者所有。

## 它解决什么问题

直接用 `file://` 打开不同版本的游戏 HTML 时，浏览器会把它们视为同一个 origin，存档、模组开关、游戏设置全部共享，换版本就容易串数据。启动器给每个版本分配一个固定的本地端口，用内置静态服务器在 `127.0.0.1:<端口>` 打开游戏 —— **不同端口即不同 origin**，IndexedDB 从此按版本隔离。

## 主要功能

| 模块 | 能力 |
| --- | --- |
| 版本管理 | 扫描、添加、启动 / 停止、导出与导入 `.dolpack`；卡片式列表与运行状态 |
| 模组管理 | 导入或拖拽安装、启停、排序、详情与 README、同名 / 缺前置 / 版本不满足提示 |
| 下载中心 | 游戏本体支持汉化整包 / ModLoader Neo 两种来源，镜像测速与分块并行下载；模组市场按索引搜索、筛选、选资产版本并一键装到选中版本 |
| 存档助手 | 同源页面导出 / 导入 / 删除单个存档槽，备份到 `backups/`；支持 DoL 原生 `.save` 存档的导入 / 导出，Android 端导出走系统保存对话框 |
| 剧情 | 选择版本与存档作为起点，配合「剧情分支模拟」模组展开分支树，查看节点详情并导出图片 / JSON |
| 备份与诊断 | 整版快照、模组配置导入导出、自动备份、兼容性检查、诊断包 |
| 问题检查 | 模组冲突、前置缺失、版本不匹配、重复模组，分页展示并给出处理操作 |
| 界面 | 深 / 亮 / 跟随系统主题、过渡动画、像素级平滑滚动、自定义主题弹窗与悬停提示 |
| 版本与更新 | 设置页显示版本 tag / 提交 / 构建时间，一键比对 GitHub Release |
| 网络 | GitHub API 统一收敛到 `GetGithubApiAsync`，镜像自动回退，所有请求写入日志 |

## 技术栈

- **.NET 10**，桌面端 WPF，Android 端 .NET MAUI
- 业务逻辑集中在跨平台的 `DoLLauncher.Core`，两端只做界面与平台适配
- 内置 `TcpListener` 静态服务器，无第三方 Web 依赖
- `System.Text.Json`（解析模组 `boot.json` 时容忍尾随逗号与注释）
- Win32 / DWM API：标题栏主题、边框禁用、DPI 像素对齐
- PowerShell 构建脚本：`build.ps1`（桌面端）、`build-android.ps1`（Android）

## 目录结构

```
.
├─ src/DoLLauncher/         ← WPF 启动器（Assets / Controls / Models / Services）
├─ src/DoLLauncher.Core/    ← 跨平台业务逻辑与平台抽象
├─ src/DoLLauncher.Maui/    ← Android 启动器（MAUI，含 Platforms/Android）
├─ tools/                   ← icon/ 图标生成脚本与源图；scripts/ 分发用 DoL.bat、launcher.ps1
├─ .github/workflows/       ← CI：WPF 构建（build.yml）、Android 构建（android.yml）
├─ build.ps1                ← 本地构建 / 打包（桌面端）
├─ build-android.ps1        ← 本地构建 Android APK（自动探测 SDK / JDK）
├─ README.md
└─ LICENSE
```

## 构建

需要 .NET 10 SDK：

```powershell
cd dev
.\build.ps1                  # 生成 exe + 只有启动器的分发包
.\build.ps1 -IncludeVersions # 连本机 versions 一起打包（体积很大）
.\build.ps1 -NoZip           # 只生成 exe
```

产物写入 `_publish/`；本地的 `build.ps1` 会把 exe 复制到同级运行时目录 `DoLLauncher/`（该目录不属于仓库），并从 `tools/scripts/` 取脚本打包分发 zip。CI 只构建单文件 exe，连带 README、LICENSE、`DoL.bat` 与 `launcher.ps1` 打成 `DoL-Launcher-win-x64.zip`；推送 `v*` 标签会自动创建 Release（见 `.github/workflows/build.yml`）。开发者可用 `DoLLauncher.exe --selftest` 跑一次自检，结果写入 `self-test.log`。

Android 端需要 Android SDK、JDK 17 与 MAUI 工作负载（脚本会自动探测 `ANDROID_HOME` / `ANDROID_SDK_ROOT` 及常见安装路径，找不到时按提示设置环境变量）：

```powershell
cd dev
dotnet workload install maui-android
.\build-android.ps1                 # Release APK，复制到上一级的 DoL-Launcher-android.apk
.\build-android.ps1 -Debug          # Debug 包，便于排查
.\build-android.ps1 -Install        # 构建后直接 adb 安装（默认设备 127.0.0.1:7555）
.\build-android.ps1 -NoCopy         # 只构建，不复制产物
```

未提供签名密码时，本地产物是 debug 签名的测试包（可安装，不能用于发版）；正式签名包由 CI 在 main / `v*` 标签上生成。本地需要正式签名时，把 keystore 放到 `keys/dol-release.keystore`，再传 `-KeyAlias` / `-KeyPass` / `-StorePass`（或设置 `DOL_KEYSTORE`、`DOL_KEY_ALIAS`、`DOL_KEY_PASS`、`DOL_STORE_PASS` 环境变量）。

`android.yml` 在 main 与 `v*` 标签上构建签名 Release APK，`android/**`、`feat/android*` 分支只构建 Debug 包；签名所需 keystore 通过仓库 Secrets（`ANDROID_KEYSTORE_BASE64`、`ANDROID_KEY_ALIAS`、`ANDROID_KEY_PASSWORD`、`ANDROID_STORE_PASSWORD`）注入，未配置时自动退回调试签名。

## 运行

1. 从 Release 下载 `DoL-Launcher-win-x64.zip`，解压到**没有中文和空格的路径**；
2. 双击 `DoLLauncher.exe`，左侧选中版本后点「启动游戏」；
3. 启动器在 `127.0.0.1:<端口>` 起内置服务器并用默认浏览器打开，关闭启动器时会询问是否一并停止服务器；服务器仍在运行时再次点击「打开游戏页面」会先聚焦上次打开的游戏窗口，页面确实关掉了才重新打开。

Android 端从 Release 下载 `DoL-Launcher-android.apk` 安装（与调试包签名不同，覆盖安装前需先卸载旧包）。进入版本详情后「启动游戏」会在应用内 WebView 打开，返回启动器时游戏保留在后台，需要退出时在版本页点「停止」。数据目录位于 `/storage/emulated/0/Android/media/com.dol.launcher/DoL`，版本详情与关于页的「打开」按钮可直接用文件管理器定位。

自行构建的用户可直接使用仓库里的 `tools/scripts/DoL.bat` 与 `launcher.ps1`（需 Python 3 在 PATH 中）；打包时会被放到分发包根目录。

## 说明与边界

- 启动器只管理本地文件与本地服务器，不抓取、不分发游戏本体或模组资源。
- 浏览器存档只能通过同源页面读写，因此存档相关操作会短暂打开一个页面。
- 启动器无法实时感知外部对 `mods/` 的修改；换端口也不会自动迁移旧的 `file://` 存档，需要用存档助手。
- 仅支持 Windows 与 Android（WPF + DWM / MAUI Android），未适配 macOS / Linux。
- Android 端固定使用应用内 WebView（系统 WebView），不使用外部浏览器；旧版 WebView 的 JS 堆上限较低，模组较大时可能渲染失败，建议保持系统 WebView 为较新版本。

## 第三方资源与致谢

- 本项目为非官方工具，与 Vrelnir 及 DoL 中文维基无隶属关系。
- 启动器图标（`Assets/dol.ico`、`Assets/dol-favicon.png`）由 Degrees of Lewdity（作者 Vrelnir）游戏美术资源中的网页小图标，与 DoL 中文维基使用的站点图标去背景合成，仅用于标识本项目。相关图像版权归原权利方所有，**不包含在本仓库的 MIT 许可范围内**。
- 游戏本体、模组、图片包与汉化资源均由各自作者发布，本仓库不再分发。
- 下载中心引用的仓库与市场索引归各自维护者所有。

## 许可证

[MIT](LICENSE)，仅覆盖本仓库源码。游戏本体、模组、图标等第三方资源不在许可范围内，详见上一节。
