# NMTools 桌面 UI 自动化测试

本项目使用 FlaUI（Windows UI Automation）启动隔离的 NMTools 副本，通过真实窗口和控件操作验证设置窗口行为。测试会显示设置窗口并模拟用户点击，请勿在正在演示或操作桌面时运行。

## 运行单项桌面测试

在 PowerShell 中执行：

```powershell
$env:NMTOOLS_APP_PATH = (Resolve-Path .\src\NMTools.App\bin\Release\net8.0-windows\win-x64\NMTools.App.exe).Path
dotnet test .\tests\NMTools.UIAutomation.Tests\NMTools.UIAutomation.Tests.csproj --configuration Release
```

运行前先构建应用。测试会将应用副本和设置放在系统临时目录，通过子进程环境变量启用专用 UI 测试模式（不创建托盘图标、不注册全局热键、不使用单实例互斥体），结束后清理临时目录。不会更改正常发布目录的设置，也不会操作 NoMachine 或发送远程输入。

`NMTOOLS_APP_PATH` 必须指向 `win-x64` 目录下的自包含 `NMTools.App.exe`（约 9 MB）；指向上一层约 148 KB 的框架依赖版本时，设置窗口不会出现，测试会超时。

## 覆盖范围

- 默认快捷键与默认粘贴方式、下拉列表顺序、下拉弹出层与下拉框对齐、选择后保存、第一项可被点中；
- 自定义行：只设置一侧时保存会被拦下并提示、✕ 清空两侧、键盘组合键录入、Shift + 鼠标中键录入；
- 关闭设置窗口：无改动直接关闭，有改动时弹出确认并可放弃；
- 开机自启动：默认不勾选、找不到 `Start-NMTools.vbs` 时置灰、勾选后创建指向无黑框启动命令的启动文件夹快捷方式、取消后移除、放弃保存时不写入。

键盘和鼠标录入类用例会向桌面发送真实输入，测试开始前会把设置窗口置于前台并确认；运行期间请不要操作键盘鼠标。开机自启动的快捷方式写在测试临时目录下的 `startup` 文件夹，不会触碰真实的启动文件夹和 `Run` 启动项，结束后随临时目录自动删除。

若未设置 `NMTOOLS_APP_PATH`，桌面 UI 测试会标记为跳过；普通 `dotnet test NMTools.sln --configuration Release` 不会擅自启动应用或操作桌面。
