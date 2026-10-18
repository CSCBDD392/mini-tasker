# 极简任务管理器 · MiniTasker

当 Windows 自带的任务管理器卡住、或者根本打不开的时候，用这个。

**单一 exe，41 KB，不需要安装、不需要 .NET 之外的任何依赖。**

## 为什么它能用，而任务管理器偶尔不行

自带任务管理器打不开，通常不是"没装好"，而是它依赖的东西正好出问题：

- **WMI / Winmgmt 服务卡住**——任务管理器有些页签走 WMI，服务一卡整个界面就白屏
- 它自己进程被挂起或高 CPU 拖垮
- 启动时有大量进程/句柄，初始化慢到像死了

MiniTasker 的做法是**绕开所有可能卡住的环节**：

| 环节 | 自带任务管理器 | MiniTasker |
| --- | --- | --- |
| 进程枚举 | 混合 WMI / API | 只用 `CreateToolhelp32Snapshot` |
| 内存 | WMI | `GetProcessMemoryInfo` |
| CPU | 性能计数器 | `GetProcessTimes` 差值 + `GetSystemTimes` |
| 界面 | 复杂 UI 框架 | WinForms 自绘，无第三方组件 |
| 采样 | 多处并发 | 单线程定时采样，失败不崩 |

实测：一次完整快照（258 个进程 + 内存 + CPU + 窗口挂起检测）**约 130ms**，
同一时刻走 WMI 的 `Win32_Process` 要 **157ms**，而且 WMI 服务一卡就整个没了。

## 界面

```
● 极简任务管理器   不依赖 WMI · 纯 Win32 采样 · 关闭窗口收进托盘      — ×
 CPU 30.6%   内存 52%   4.2/8.0 GB   [仅显示部分进程 · 以管理员重启]
 进程  10 个                                    [搜索框]  [结束任务]
 名称              PID    CPU ↓    内存      线程   状态
 DeepSeek Harness  5412   2.8%    65.7 MB    10
 MiniTasker.exe    1536   2.8%    43.7 MB    11    管理员
 [System Process]     0    —        —         8     系统
```

- **CPU / 内存**两根进度条，超过 65% 变黄、85% 变红
- **卡死标黄**：主窗口没响应的进程，名称变黄、左边红条、"状态"列写「无响应」
- 点表头排序（名称 / PID / CPU / 内存 / 线程 / 状态），再点一次反向

## 操作

| 操作 | 效果 |
| --- | --- |
| 点行 | 选中（底部显示路径） |
| **双击** | 在资源管理器里定位这个 exe |
| `Del` | 结束选中进程（会二次确认） |
| `Ctrl+F` | 搜索进程名 / PID / 窗口标题 |
| `Esc` | 退出搜索 |
| `↑` `↓` | 上下选择 |
| `F5` | 立即刷新 |
| 拖顶部空白 | 移动窗口 |
| **关闭窗口 / 点 ×** | **收进托盘**（不是退出） |
| `Ctrl+Alt+F10` | 呼出窗口 |
| 托盘双击 | 呼出窗口 |
| 托盘右键 → 退出 | 真正退出 |

## 关于「只能看到部分进程」

**这是 Windows 的权限机制，不是 bug。**

非管理员权限（Medium 完整性）下，只能枚举**同一边界内**的进程。数量可能只有十几个，
系统进程、其它用户的服务都看不到。界面上那行「仅显示部分进程 · 以管理员重启」就是提示这个，
点它可以 UAC 提权重开。

要排查「某个服务卡死了」这类问题，请以管理员身份运行：

```powershell
Start-Process .\MiniTasker.exe -Verb RunAs
```

## 编译

只要 .NET Framework 自带的编译器，不需要 Visual Studio：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" `
  /target:winexe /platform:anycpu /optimize+ `
  /out:MiniTasker.exe `
  /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
  src\Program.cs src\ProcessApi.cs src\MainForm.cs
```

## 文件

| 文件 | 作用 |
| --- | --- |
| `MiniTasker.exe` | 成品，双击即可 |
| `src/ProcessApi.cs` | 进程枚举 / CPU / 内存 / 挂起检测 / 结束进程 |
| `src/MainForm.cs` | 界面绘制、排序筛选、托盘、热键 |
| `src/Program.cs` | 入口、单实例 |
| `src/SelfTest.cs` | 离线自检（不开界面直接打印采样结果，排查用） |

## 已经踩过的坑（都修了，记下来免得再犯）

**1. `ShowInTaskbar` 会把全局热键搞失效**

`HideToTray()` 里改 `ShowInTaskbar = false` 会**重建窗口句柄**，之前 `RegisterHotKey` 注册的热键
跟着失效——结果就是"收进托盘后热键再也唤不回来"。改成构造时设一次 `ShowInTaskbar = false`，
隐藏时只 `Hide()`。

**2. `Ctrl+Alt+M` 已被别的软件占用**

`RegisterHotKey` 返回 1409（`ERROR_HOTKEY_ALREADY_REGISTERED`）。现在会在
`Ctrl+Alt+F10` / `F8` / `F7` 之间自动降级，全被占用才退化成"只能点托盘"，状态栏会显示实际可用的组合。

**3. 进程枚举必须分两步**

一开始把「枚举」和「逐个 OpenProcess 补内存」写在一个 `do...while` 里，
采集过程中调用的其它 API 会干扰枚举，导致枚举到十几个进程就提前结束（错误码还是"正常结束"，
非常难查）。现在先一次性读完所有 `PROCESSENTRY32W`、关掉快照，再逐个补细节。

**4. PowerShell 5.1 读 UTF-8 无 BOM 脚本会按 GBK 解析**

中文注释被错误解码会把后面的语句带歪。任何带中文的 `.ps1` 都要存成 **UTF-8 with BOM**。
（C# 源码不受影响，csc 默认按 UTF-8 读。）

## 已知限制

- 没有开机自启（需要的话丢进 `shell:startup`）
- 没做结束进程树（只结束单个进程；结束一个带子进程的程序可能残留子进程）
- 温度、磁盘、网络、启动项都没做——这是个「救火工具」，不是完整替代
- CPU 百分比按「累计 CPU 时间差值 / 采样间隔」算，第一次采样全是 `—`（和任务管理器一致）

## 许可

MIT
