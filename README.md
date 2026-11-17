# komorebi-config

Windows 11 上的 [komorebi](https://github.com/LGUG2Z/komorebi) 平铺窗口管理器配置：双屏、9 工作区/屏、Dracula 主题、纯键盘操作，外加上一个把工作区/窗口标题/布局显示在 Windows 任务栏里的信息条。

## 目录结构

```
komorebi-config/
├── komorebi/
│   ├── komorebi.json          # 主配置（9 个 BSP 工作区、Dracula、边框、padding）
│   └── applications.json      # 按 exe/class/title 的应用规则
├── whkd/
│   └── whkdrc                 # 快捷键（awesomewm 风格）
├── scripts/
│   ├── komorebi-start.ps1     # 登录启动：komorebi + whkd + 任务栏信息条
│   └── launch-focused.ps1     # 在当前聚焦的屏幕/工作区启动终端/Firefox
├── WorkspaceTray/
│   ├── WorkspaceTray.cs       # 任务栏信息条（C# / WinForms）
│   ├── app.manifest           # PerMonitorV2 DPI 声明
│   └── build.ps1              # 用 .NET Framework csc 编译
├── legacy/                    # 已弃用的双屏 komorebi-bar 配置（仅供参考）
└── install.ps1
```

## 依赖

- Windows 11（任务栏信息条按 Win11 的任务栏结构编写）
- [komorebi](https://github.com/LGUG2Z/komorebi) v0.1.41+，安装在 `C:\Program Files\komorebi`
- [whkd](https://github.com/LGUG2Z/whkd)，安装在 `C:\Program Files\whkd`
- PowerShell 7（`whkdrc` 里的 `.shell pwsh`）
- .NET Framework 4.8（编译 `WorkspaceTray.exe`，Windows 自带）
- 可选：JetBrainsMono NF 字体、Windows Terminal

## 安装

```powershell
git clone git@github.com:cnjhb/komorebi-config.git
cd komorebi-config
.\install.ps1
```

`install.ps1` 会：

1. 把配置复制到 `%USERPROFILE%`（`komorebi.json`、`applications.json`）和 `%USERPROFILE%\.config\whkdrc`
2. 把脚本复制到 `%LOCALAPPDATA%\Programs\`
3. 把 `WorkspaceTray` 源码复制到 `%LOCALAPPDATA%\Programs\WorkspaceTray\` 并编译
4. 重建启动项 `%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup\komorebi.lnk`

然后重启当前会话：

```powershell
komorebic stop --whkd --bar
# 双击 Startup 里的 komorebi.lnk，或注销/重新登录
```

## 快捷键

前缀统一是 `win`：

| 操作 | 快捷键 |
|---|---|
| 启动终端 / Firefox（当前屏） | `win + Enter` / `win + '` |
| 切换窗口 | `win + j` / `win + k` |
| 移动窗口 | `win + shift + j` / `win + shift + k` |
| 切换显示器 | `win + ctrl + j` / `win + ctrl + k` |
| 切换工作区 1-9 | `win + 1..9` |
| 移动窗口到工作区 | `win + shift + 1..9` |
| 浮动 / monocle / 最大化 | `win + t` / `win + f` / `win + m` |
| 关闭窗口 | `win + shift + c` |
| 重新加载配置 | `win + ctrl + r` |
| 重新平铺 | `win + shift + r` |

完整列表见 `whkd/whkdrc`。

## 设计说明

### 任务栏信息条（WorkspaceTray）

Windows 11 任务栏没有第三方文字接口，所以用**置顶窗口**覆盖在任务栏左侧空白区：

- 每块屏一个胶囊（`Shell_TrayWnd` / `Shell_SecondaryTrayWnd` 各一个）
- 通过 `komorebic subscribe-pipe` 订阅状态，切换工作区/窗口时即时刷新
- 显示：工作区编号（聚焦 = 紫色加粗，有窗口 = 亮色，空 = 暗色）│ 当前窗口标题 │ 布局
- 外观：Dracula 深色胶囊（`#1E1F29` + 细边框），JetBrainsMono NF
- `WM_NCHITTEST -> HTTRANSPARENT`，点击穿透，不影响任务栏
- 全屏应用时自动隐藏

同时在 `http://127.0.0.1:17321/` 提供一行纯文本（聚焦屏的工作区、布局、窗口标题），方便 Windhawk 等外部工具读取，例如：

```
[1] 2· 3 4 5 6 7 8 9  ·  BSP  ·  Firefox — 标题…
```

实测这个 Windows 版本会把**分层透明窗口**合成到任务栏下面，不能用 per-pixel alpha。窗口保持矩形，背景实时采样任务栏的同色渐变，胶囊圆角用 GDI+ 抗锯齿绘制——窗口 Region 是 1-bit 的，直接裁剪会出锯齿。

### 可选：Windhawk 原生显示

`windhawk/` 里有一个基于 [Taskbar Clock Customization](https://github.com/ramensoftware/windhawk-mods) 的 mod（GPL-3.0），把工作区行直接画在**主屏任务栏时钟区**，1 秒刷新。默认不安装，构建和安装方法见 `windhawk/README.md`。

### 应用启动器（launch-focused.ps1）

新窗口的初始位置由应用自己决定（终端/Firefox 会记住上次位置），komorebi 的 `FocusChange` 处理会跟随窗口切换显示器。所以启动器：

1. 读取当前聚焦的显示器和其工作区
2. 终端用 `wt --pos <工作区左上角>` 直接开在正确屏幕
3. 用 `komorebic move-to-monitor-workspace` 把跑到别处的窗口（如 Firefox）挪回来

**注意**：脚本必须用「重定向到文件 + UTF-8 读取」的方式解析 `komorebic state`。whkd 调起的 pwsh 使用系统代码页（GBK），直接管道读取会把中文窗口标题解码坏，导致 JSON 解析失败、快捷键"没反应"。

### 启动脚本（komorebi-start.ps1）

- 直接启动 `komorebi.exe`，把 stderr 重定向到 `komorebi-stderr.log`（komorebi 是 `panic=abort` 构建，崩溃时日志里没有信息，stderr 才有）
- 轮询 `komorebic state` 等待 socket 就绪
- 按需启动 whkd 和任务栏信息条
- 由 Startup 快捷方式（资源管理器）启动，避免进程被其他进程树清理

## 日志

| 文件 | 内容 |
|---|---|
| `%USERPROFILE%\komorebi-start.log` | 启动流程 |
| `%USERPROFILE%\launch-focused.log` | 应用启动/搬移结果 |
| `%USERPROFILE%\taskbar-info.log` | 任务栏信息条连接/渲染 |
| `%USERPROFILE%\komorebi-stderr.log` | komorebi 的 panic 输出（正常为空） |

## 已知限制

- 任务栏胶囊是覆盖窗口，不参与任务栏布局；任务栏左对齐或关闭 Widgets 时位置会调整（脚本会读 `TaskbarDa` 自动左移）
- 只有被 komorebi 管理的窗口才会显示标题；聚焦未托管窗口时保留上一个标题
- 工作区数字不可点击（点击穿透），切换仍用 `win + 1..9`
- 不同 DPI 的多屏下，窗口搬移依赖 komorebi 的坐标，极端情况可能出现 1px 偏差
- `legacy/` 里的双屏 bar 配置已不再使用，仅保留作参考

## License

个人配置，未附带许可证。使用前请自行确认符合你的 komorebi / whkd 许可。
