$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = 'D:\Software\Dotnet\dotnet.exe'
$publishRoot = Join-Path $projectRoot 'publish\NMTools-win-x64'
$programDirectory = Join-Path $publishRoot 'program'
$dataDirectory = Join-Path $programDirectory 'data'
$dataBackupDirectory = Join-Path $projectRoot 'publish\.data-backup'
$stagingDirectory = Join-Path $projectRoot 'publish\.staging-NMTools-win-x64'
$updaterStagingDirectory = Join-Path $projectRoot 'publish\.staging-NMTools-updater'
$projectFile = Join-Path $projectRoot 'src\NMTools.App\NMTools.App.csproj'
$updaterProjectFile = Join-Path $projectRoot 'src\NMTools.Updater\NMTools.Updater.csproj'

if (Test-Path $dataBackupDirectory) {
    # 上一次发布没有走完还原，里面可能是唯一一份用户设置，不能直接覆盖。放在最前面检查，避免白白编译一遍。
    throw "发现上次发布遗留的设置备份：$dataBackupDirectory。请确认其中内容（若是需要保留的设置，把它放回 program\data），再删除该目录后重新发布。"
}

$runningProcesses = Get-Process -Name 'NMTools.App' -ErrorAction SilentlyContinue
if ($null -ne $runningProcesses) {
    Write-Host "检测到正在运行的 NMTools 实例 (PID: $(($runningProcesses.Id) -join ', '))，正在停止以进行发布更新..." -ForegroundColor Yellow
    $runningProcesses | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

if (-not (Test-Path $dotnet)) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw 'dotnet was not found. Install the .NET 8 SDK or update the path in this script.'
    }

    $dotnet = $dotnetCommand.Source
}

if (Test-Path $stagingDirectory) {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
}

if (Test-Path $updaterStagingDirectory) {
    Remove-Item -LiteralPath $updaterStagingDirectory -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $stagingDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $updaterStagingDirectory | Out-Null

& $dotnet publish $projectFile `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $stagingDirectory `
    --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

# 更新器是独立进程：主程序无法替换正在运行的自身，必须由它来完成文件替换。
# 单独发布到自己的暂存目录，稍后一并放进 program\。
# 更新器启用 trimming：它不用 WPF/WinForms，裁剪后从 ~69 MB 降到 ~10 MB，
# 发布包因此只比原来大十几 MB，而不是翻倍。
& $dotnet publish $updaterProjectFile `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $updaterStagingDirectory `
    --no-restore `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:PublishTrimmed=true `
    -p:TrimMode=partial

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish (updater) failed with exit code $LASTEXITCODE"
}

if (Test-Path $dataDirectory) {
    # 发布会整个重建发布目录，program\data 里的快捷键设置和窗口位置会一起被删。先暂存，发布完再放回。
    # 暂存失败就中止发布：宁可不发布，也不能丢用户设置。
    Copy-Item -LiteralPath $dataDirectory -Destination $dataBackupDirectory -Recurse -Force
    Write-Host "已暂存用户设置：$dataDirectory" -ForegroundColor Yellow
}

if (Test-Path $publishRoot) {
    Remove-Item -LiteralPath $publishRoot -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $programDirectory | Out-Null

Get-ChildItem -LiteralPath $stagingDirectory -Force |
    Where-Object { $_.Extension -notin @('.pdb', '.xml') } |
    Move-Item -Destination $programDirectory

# 更新器与主程序并列放在 program\，文件名固定为 NMTools.Updater.exe：
# 主程序按这个固定名字启动它，不做任何运行时查找。
$updaterExe = Join-Path $updaterStagingDirectory 'NMTools.Updater.exe'
if (-not (Test-Path $updaterExe)) {
    throw "更新器未生成：$updaterExe"
}

Copy-Item -LiteralPath $updaterExe -Destination (Join-Path $programDirectory 'NMTools.Updater.exe') -Force

Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
Remove-Item -LiteralPath $updaterStagingDirectory -Recurse -Force

if (Test-Path $dataBackupDirectory) {
    Copy-Item -LiteralPath $dataBackupDirectory -Destination $dataDirectory -Recurse -Force
    # 确认还原后的文件数与暂存一致才删除暂存，避免还原不完整时丢掉唯一副本。
    $backupCount = @(Get-ChildItem -LiteralPath $dataBackupDirectory -Recurse -File -Force).Count
    $restoredCount = @(Get-ChildItem -LiteralPath $dataDirectory -Recurse -File -Force).Count
    if ($restoredCount -ne $backupCount) {
        throw "用户设置还原不完整（暂存 $backupCount 个文件，还原 $restoredCount 个）。暂存目录已保留：$dataBackupDirectory"
    }

    Remove-Item -LiteralPath $dataBackupDirectory -Recurse -Force
    Write-Host "已还原用户设置：$dataDirectory" -ForegroundColor Yellow
}

$hiddenLauncher = @'
Set shell = CreateObject("WScript.Shell")
Set fileSystem = CreateObject("Scripting.FileSystemObject")
programDirectory = fileSystem.BuildPath(fileSystem.GetParentFolderName(WScript.ScriptFullName), "program")
runtimeDirectory = fileSystem.BuildPath(programDirectory, ".runtime")
If Not fileSystem.FolderExists(runtimeDirectory) Then
    fileSystem.CreateFolder(runtimeDirectory)
End If
shell.Environment("Process")("DOTNET_BUNDLE_EXTRACT_BASE_DIR") = runtimeDirectory
shell.CurrentDirectory = programDirectory
arguments = ""
For Each argument In WScript.Arguments
    arguments = arguments & " """ & argument & """"
Next
shell.Run """" & fileSystem.BuildPath(programDirectory, "NMTools.App.exe") & """" & arguments, 0, False
'@
Set-Content -LiteralPath (Join-Path $publishRoot 'Start-NMTools.vbs') -Value $hiddenLauncher -Encoding ascii

$readme = @'
NMTools portable build

Usage:
1. Double-click Start-NMTools.vbs for a windowless launch.
2. Focus the target position in the NoMachine remote window.
3. Press Ctrl + Alt + Space to open the input bubble.
4. Press Enter for Claude (Ctrl + V), Ctrl + Enter for generic terminal (Ctrl + Shift + V), Ctrl + O for OpenCode (Shift + middle mouse), Ctrl + Y for Emacs yank, Ctrl + G for gVim "+p, Ctrl + T for Terminal (middle mouse), or Esc to cancel. The mapping can be changed in Settings.

Program files are in the program directory. Runtime extraction is kept in program\.runtime and settings are kept in program\data.
Diagnostic logs are written to program\logs. If you hit a problem, use "Open diagnostic logs" in the tray menu and send the latest log file.
Logs never contain your typed text, drafts, clipboard contents or remote credentials; only lengths, error codes and process/window identifiers.
Use Start-NMTools.vbs so all generated files stay inside this folder. Keep the folder writable.
Close NMTools before deleting this entire folder.
This build does not require a separate .NET Runtime installation.
'@
Set-Content -LiteralPath (Join-Path $publishRoot 'README.txt') -Value $readme -Encoding ascii

# ---------------------------------------------------------------------------
# 生成分发用压缩包。
#
# 打包前剔除不属于程序本身的内容：
# - program\data\  本机的窗口位置与快捷键设置，绝不能分发给别人
# - program\logs\  本机的诊断日志，可能含本机进程信息
# - program\.runtime\  首次启动时自动重建的解压缓存
# 打包使用临时目录，不修改已发布的目录本身。
# ---------------------------------------------------------------------------
$version = (Get-Item (Join-Path $programDirectory 'NMTools.App.exe')).VersionInfo.FileVersion
if ($version -match '^(\d+\.\d+\.\d+)') {
    $version = $Matches[1]
}

$archiveName = "NMTools-v$version-win-x64.zip"
$stagingForArchive = Join-Path $projectRoot 'publish\.archive-staging'

if (Test-Path $stagingForArchive) {
    Remove-Item -LiteralPath $stagingForArchive -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $stagingForArchive | Out-Null

$archiveRoot = Join-Path $stagingForArchive "NMTools-v$version-win-x64"
New-Item -ItemType Directory -Force -Path $archiveRoot | Out-Null

Copy-Item -LiteralPath (Join-Path $publishRoot 'README.txt') -Destination $archiveRoot
Copy-Item -LiteralPath (Join-Path $publishRoot 'Start-NMTools.vbs') -Destination $archiveRoot

$archiveProgram = Join-Path $archiveRoot 'program'
New-Item -ItemType Directory -Force -Path $archiveProgram | Out-Null
Get-ChildItem -LiteralPath $programDirectory -File | Copy-Item -Destination $archiveProgram

# 只保留程序文件，不含本机设置、日志与运行时缓存。
if (-not (Test-Path (Join-Path $archiveProgram 'NMTools.App.exe'))) {
    throw '压缩包中缺少 NMTools.App.exe，已中止打包。'
}

# 更新器必须随包分发：没有它，新版本既无法自动更新，也无法被更新。
# 缺了就不发布，否则会产出"能检查更新但更新不了"的包。
if (-not (Test-Path (Join-Path $archiveProgram 'NMTools.Updater.exe'))) {
    throw '压缩包中缺少 NMTools.Updater.exe，已中止打包。'
}

# 压缩包与历史版本保持一致，直接放在 publish\ 根目录。
# 注意 $publishRoot 是 publish\NMTools-win-x64（程序目录本身），
# 因此这里必须从它的父目录取 publish\，否则压缩包会落进程序目录内部。
$publishDirectory = Split-Path -Parent $publishRoot
$finalArchive = Join-Path $publishDirectory $archiveName
if (Test-Path $finalArchive) {
    Remove-Item -LiteralPath $finalArchive -Force
}

Compress-Archive -LiteralPath $archiveRoot -DestinationPath $finalArchive -CompressionLevel Optimal
Remove-Item -LiteralPath $stagingForArchive -Recurse -Force

# ---------------------------------------------------------------------------
# 生成校验文件 SHA256SUMS.txt。
#
# 客户端下载 zip 后用它比对内容哈希。它和 zip 一起作为 Release 的两个资产，
# 客户端分别下载再比对，因此能发现"下载内容被替换"，而不只是"下载损坏"。
# 这个文件不入包，只作为独立资产上传。
# ---------------------------------------------------------------------------
$archiveHash = (Get-FileHash -LiteralPath $finalArchive -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumFile = Join-Path $publishDirectory 'SHA256SUMS.txt'
Set-Content -LiteralPath $checksumFile -Value "$archiveHash  $archiveName" -Encoding ascii
Write-Host "已生成校验文件：$checksumFile" -ForegroundColor Yellow

$archiveSize = [math]::Round((Get-Item $finalArchive).Length / 1MB, 1)

Write-Output "Published: $publishRoot"
Write-Output "Archive:   $finalArchive ($archiveSize MB)"
