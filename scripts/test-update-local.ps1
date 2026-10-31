<#
.SYNOPSIS
    在隔离目录里端到端验证真实更新链路，不需要人工点界面、不需要上传到 GitHub。

.DESCRIPTION
    用真实的发布 zip 走一遍「解压 → 校验包结构 → 替换 → 保留设置 → 启动校验 → 清理」，
    每一步都断言，失败时打印日志。目的是把"只有用户手工测试才暴露"的问题提前到这里。

    测试全程在系统临时目录的独立副本里进行，不接触真实安装、真实快捷方式或用户配置。

.EXAMPLE
    .\scripts\test-update-local.ps1
#>
param(
    [string]$BaselineVersion = '0.6.1',
    [string]$TargetZip,
    [switch]$KeepTemp
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = 'D:\Software\Dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) { throw '找不到 dotnet。' }
    $dotnet = $dotnetCommand.Source
}

if (-not $TargetZip) {
    $TargetZip = Join-Path $projectRoot 'publish\NMTools-v0.7.0-win-x64.zip'
}
if (-not (Test-Path $TargetZip)) {
    throw "找不到待更新的发布包：$TargetZip"
}

$baselineDir = Join-Path $projectRoot "publish\test-local-$BaselineVersion\NMTools-win-x64"
if (-not (Test-Path $baselineDir)) {
    throw "找不到 $BaselineVersion 基线包：$baselineDir"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("NMTools.UpdateTest." + [Guid]::NewGuid().ToString('N'))
$installDir = Join-Path $tempRoot 'NMTools-win-x64'

$failures = [System.Collections.Generic.List[string]]::new()
function Step {
    param([string]$Name, [scriptblock]$Body)
    Write-Host "==> $Name" -ForegroundColor Cyan
    try {
        & $Body
        Write-Host '    通过' -ForegroundColor Green
    }
    catch {
        Write-Host "    失败：$($_.Exception.Message)" -ForegroundColor Red
        $failures.Add("$Name — $($_.Exception.Message)")
        throw
    }
}

try {
    Step '准备隔离安装' {
        Copy-Item -LiteralPath $baselineDir -Destination $installDir -Recurse -Force
        # 制造一份"用户改过的设置"，稍后验证它必须原样保留。
        $dataDir = Join-Path $installDir 'program\data'
        New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
        Set-Content -LiteralPath (Join-Path $dataDir 'input-bridge-settings.json') -Value '{"marker":"user-setup"}' -Encoding utf8
        $ver = (Get-Item (Join-Path $installDir 'program\NMTools.App.exe')).VersionInfo.FileVersion
        if ($ver -ne "$BaselineVersion.0") { throw "基线版本不符：$ver" }
    }

    Step '解压更新包（模拟客户端）' {
        $staging = Join-Path $installDir '.update-staging\content'
        New-Item -ItemType Directory -Force -Path $staging | Out-Null
        Expand-Archive -LiteralPath $TargetZip -DestinationPath $staging -Force
        $thisRoot = Join-Path $staging 'NMTools-v0.7.0-win-x64'
        if (-not (Test-Path (Join-Path $thisRoot 'program\NMTools.App.exe'))) {
            throw "解压后找不到 program/NMTools.App.exe（实际结构：$((Get-ChildItem $staging).Name -join ', ')）"
        }
    }

    Step '确认更新器不在 program\ 内' {
        $updaterInStaging = Join-Path $installDir '.update-staging\updater\NMTools.Updater.exe'
        if (Test-Path (Join-Path $installDir 'program\.update-staging')) {
            throw '暂存目录仍在 program\ 内，会导致重命名自锁。'
        }
        New-Item -ItemType Directory -Force -Path (Split-Path $updaterInStaging) | Out-Null
        Copy-Item (Join-Path $installDir 'program\NMTools.Updater.exe') $updaterInStaging -Force
    }

    Step '执行替换（真实更新器进程）' {
        $updater = Join-Path $installDir '.update-staging\updater\NMTools.Updater.exe'
        $content = Join-Path $installDir '.update-staging\content\NMTools-v0.7.0-win-x64'
        # 传一个已退出的 PID：模拟主程序已退出。
        $probe = Start-Process -FilePath 'cmd.exe' -ArgumentList '/c exit 0' -PassThru -WindowStyle Hidden
        $probe.WaitForExit()
        $exitedPid = $probe.Id

        # --no-restart：不让更新器拉起新版本。否则新版本一启动就会执行清理，
        # 把备份删掉，后面的断言就看不到替换结果了。
        # -WorkingDirectory 必须在发布包之外：Windows 不允许重命名进程的工作目录或其祖先。
        $p = Start-Process -FilePath $updater `
            -ArgumentList "--pid $exitedPid --app-dir `"$(Join-Path $installDir 'program')`" --staged-dir `"$content`" --no-restart" `
            -WorkingDirectory ([System.IO.Path]::GetTempPath()) `
            -PassThru -Wait -WindowStyle Hidden
        if ($p.ExitCode -ne 0) {
            $log = Join-Path $installDir 'program\logs\updater.log'
            if (Test-Path $log) { Get-Content $log -Tail 20 | ForEach-Object { Write-Host "    $_" } }
            throw "更新器退出码 $($p.ExitCode)"
        }
    }

    Step '验证新版本已就位' {
        $ver = (Get-Item (Join-Path $installDir 'program\NMTools.App.exe')).VersionInfo.FileVersion
        if ($ver -ne '0.7.0.0') { throw "替换后版本应为 0.7.0.0，实际 $ver" }
    }

    Step '验证用户设置被保留' {
        $settings = Join-Path $installDir 'program\data\input-bridge-settings.json'
        if (-not (Test-Path $settings)) { throw '设置文件丢失' }
        $content = Get-Content $settings -Raw
        if ($content -notmatch 'user-setup') { throw "设置内容被改动：$content" }
    }

    Step '验证备份存在且可回滚' {
        # 注意：更新器替换成功后会拉起新版本，新版本启动即执行清理、删掉备份。
        # 所以这一项必须在"启动新版本"之前检查，否则必然失败。
        $backup = Join-Path $installDir 'program.old'
        if (-not (Test-Path $backup)) {
            $log = Join-Path $installDir 'program\logs\updater.log'
            if (Test-Path $log) { Get-Content $log -Tail 10 | ForEach-Object { Write-Host "    $_" } }
            throw '未生成 program.old 备份'
        }
        $oldVer = (Get-Item (Join-Path $backup 'NMTools.App.exe')).VersionInfo.FileVersion
        if ($oldVer -ne "$BaselineVersion.0") { throw "备份版本应为 $BaselineVersion.0，实际 $oldVer" }
    }

    Step '验证启动能正常起来' {
        # 只验证主程序能被启动并写出一条 app.start —— 它启动后是否常驻取决于运行环境，
        # 用"进程还在不在"判断会误报（UI 自动化模式下程序会自行退出）。
        $exe = Join-Path $installDir 'program\NMTools.App.exe'
        $logDir = Join-Path $installDir 'program\logs'
        $before = @(Get-ChildItem $logDir -Filter 'NMTools-*.log' -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Length })

        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = $exe
        $startInfo.UseShellExecute = $false
        $startInfo.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = (Join-Path $installDir 'program\.runtime')
        $proc = [System.Diagnostics.Process]::Start($startInfo)
        Start-Sleep -Seconds 5
        if (-not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(5000) }

        $log = Get-ChildItem $logDir -Filter 'NMTools-*.log' -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (-not $log) { throw '新版本未写出任何诊断日志，可能启动失败' }
        $text = Get-Content $log.FullName -Raw
        if ($text -notmatch 'app\.start') { throw "日志中没有 app.start 记录：$($log.Name)" }
    }

    Step '验证清理删除备份与暂存' {
        # 写入"更新已完成"标记后启动程序，App 在启动早期执行清理。
        $marker = Join-Path $installDir '.update-verify'
        Set-Content -LiteralPath $marker -Value '{"Version":"0.7.0.0"}' -Encoding utf8

        $exe = Join-Path $installDir 'program\NMTools.App.exe'
        $startInfo = New-Object System.Diagnostics.ProcessStartInfo
        $startInfo.FileName = $exe
        $startInfo.UseShellExecute = $false
        $startInfo.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = (Join-Path $installDir 'program\.runtime')
        $proc = [System.Diagnostics.Process]::Start($startInfo)
        Start-Sleep -Seconds 5
        if (-not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(5000) }

        if (Test-Path (Join-Path $installDir 'program.old')) { throw 'program.old 未被清理' }
        if (Test-Path (Join-Path $installDir '.update-staging')) { throw '.update-staging 未被清理' }
        if (Test-Path $marker) { throw '.update-verify 标记未被清理' }
    }
}
finally {
    if ($KeepTemp) {
        Write-Host "临时目录保留：$tempRoot" -ForegroundColor Yellow
    }
    elseif (Test-Path $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ''
if ($failures.Count -eq 0) {
    Write-Host '==== 全部通过 ====' -ForegroundColor Green
}
else {
    Write-Host '==== 存在失败 ====' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}
