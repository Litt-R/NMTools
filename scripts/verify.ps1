<#
.SYNOPSIS
    一键验证：还原、Release 构建、Core 单元测试，并可选运行桌面 UI 自动化测试。

.DESCRIPTION
    UI 自动化测试会弹出真实窗口并向桌面发送键盘/鼠标输入，运行期间不能操作桌面，
    因此默认不运行，需要加 -Ui 显式开启。未加 -Ui 时会在结尾明确提示"UI 测试未运行"，
    避免把"没跑"误读成"通过"。

.EXAMPLE
    .\scripts\verify.ps1          # 构建 + 单元测试
    .\scripts\verify.ps1 -Ui      # 再加上桌面 UI 自动化测试（会占用桌面）
#>
param(
    [switch]$Ui
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $projectRoot 'NMTools.sln'
$dotnet = 'D:\Software\Dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnetCommand) {
        throw 'dotnet was not found. Install the .NET 8 SDK or update the path in this script.'
    }

    $dotnet = $dotnetCommand.Source
}

function Invoke-Step {
    param([string]$Name, [scriptblock]$Action)

    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) {
        throw "$Name 失败（退出码 $LASTEXITCODE）。"
    }
}

$results = [ordered]@{}

Invoke-Step '还原依赖' { & $dotnet restore $solution }
$results['还原依赖'] = '通过'

Invoke-Step 'Release 构建' { & $dotnet build $solution --configuration Release --no-restore }
$results['Release 构建'] = '通过'

Invoke-Step 'Core 单元测试' {
    & $dotnet test (Join-Path $projectRoot 'tests\NMTools.Core.Tests\NMTools.Core.Tests.csproj') --configuration Release --no-build
}
$results['Core 单元测试'] = '通过'

if ($Ui) {
    $appPath = Join-Path $projectRoot 'src\NMTools.App\bin\Release\net8.0-windows\win-x64\NMTools.App.exe'
    if (-not (Test-Path $appPath)) {
        throw "找不到自包含的 NMTools.App.exe：$appPath"
    }

    Write-Host '即将运行桌面 UI 自动化测试：会弹出窗口并发送真实键鼠输入，请勿操作桌面。' -ForegroundColor Yellow
    $previous = $env:NMTOOLS_APP_PATH
    $env:NMTOOLS_APP_PATH = (Resolve-Path $appPath).Path
    try {
        Invoke-Step '桌面 UI 自动化测试' {
            & $dotnet test (Join-Path $projectRoot 'tests\NMTools.UIAutomation.Tests\NMTools.UIAutomation.Tests.csproj') --configuration Release --no-build
        }
    }
    finally {
        $env:NMTOOLS_APP_PATH = $previous
    }

    $results['桌面 UI 自动化测试'] = '通过'
}
else {
    $results['桌面 UI 自动化测试'] = '未运行（加 -Ui 开启）'
}

Write-Host ''
Write-Host '==== 验证汇总 ====' -ForegroundColor Green
foreach ($entry in $results.GetEnumerator()) {
    Write-Host ('{0,-20} {1}' -f $entry.Key, $entry.Value)
}
