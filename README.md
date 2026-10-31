# NMTools

Windows 本地输入桥接工具：在 NoMachine 远程 Linux 会话里，用 Windows 输入法输入中文。

## 它解决什么问题

通过 NoMachine 连远程 Linux 时，远程主机通常没有好用的中文输入法。NMTools 让你在 Windows 一侧用熟悉的输入法打字，再把结果粘贴回远程窗口的输入框。

工作方式：远程输入框获得焦点后按全局快捷键，本地弹出轻量输入气泡 → 用 Windows 输入法输入 → 回车提交 → 程序自动切回原远程窗口并把文本粘贴进去。

不需要在远程 Linux 上安装任何输入法。

## 功能

- **全局快捷键触发**，默认 `Ctrl + Alt + Space`，可在设置中修改
- **Windows 输入法输入**，支持长文本，气泡自动增高
- **按目标软件选择粘贴方式**：`Ctrl + V`、`Ctrl + Shift + V`、鼠标中键、`Shift + 鼠标中键`、Emacs `Ctrl + Y`、gVim `"+p`，或自定义
- **粘贴失败可重试**：默认 `Ctrl + Alt + V`，剪贴板同步延迟导致没粘上时可再发一次
- **可调剪贴板同步延迟**：默认 400 ms，网络慢时可以调大
- **单实例运行**、托盘驻留、可选开机自启动（静默启动，无黑框）
- **便携免安装**：自包含单文件，不需要 .NET Runtime

## 获取

当前只提供源码构建，尚未发布正式版本。

自行构建需要 Windows 10/11、.NET 8 SDK，以及用于实际使用的 NoMachine Windows 客户端。

```powershell
git clone https://github.com/Litt-R/NMTools.git
cd NMTools
.\scripts\publish-portable.ps1
```

产物在 `publish\NMTools-win-x64\`，用 `Start-NMTools.vbs` 启动——这个入口不会弹出控制台黑框。

> 发布目录需要可写。程序设置和诊断日志保存在 `program\data\`，移动整个目录后重新运行一次即可。

## 开发

```powershell
.\scripts\verify.ps1        # 还原 + Release 构建 + 单元测试
.\scripts\verify.ps1 -Ui    # 额外跑桌面 UI 自动化测试
```

`-Ui` 会弹出真实窗口并发送键鼠输入，**运行期间不要操作桌面**。

等价的手动命令：

```powershell
dotnet restore NMTools.sln
dotnet build NMTools.sln --configuration Release
dotnet test NMTools.sln --configuration Release
```

UI 自动化测试依赖 FlaUI，需要通过 `NMTOOLS_APP_PATH` 显式指定应用程序路径后才运行。

## 项目结构

| 路径 | 内容 |
|---|---|
| `src/NMTools.Core/` | 平台无关的领域模型、接口和用例边界 |
| `src/NMTools.App/` | Windows/WPF 宿主、Win32 互操作、窗口与服务 |
| `tests/NMTools.Core.Tests/` | 单元测试 |
| `tests/NMTools.UIAutomation.Tests/` | 桌面 UI 自动化测试（FlaUI） |
| `scripts/` | 验证与发布脚本 |

## 文档

- [`AGENTS.md`](AGENTS.md)：代码贡献与 agent 协作约定

架构说明、开发路线、技术决策记录（ADR）和版本变更不随源码分发，在本地开发副本的 `docs/` 目录中维护。

## 已知限制

- 需要 NoMachine 允许 Windows 到远程会话的剪贴板同步
- 只响应被识别为 NoMachine 的前台窗口
- 粘贴方式中 `Shift + 鼠标中键` 已在目标环境验证，纯鼠标中键与自定义输入尚未逐类实测
- 剪贴板同步没有完成信号，等待时间只能靠经验值，慢网络下可能需要调大延迟或手动重试

## 隐私

不记录输入文本、剪贴板内容或远程主机凭据。诊断日志只写 `program\data\`，不包含你输入的内容。
