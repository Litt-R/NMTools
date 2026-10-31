<#
.SYNOPSIS
    把 publish\NMTools-win-x64 打成 publish\NMTools-v<版本>-win-x64.zip。

.DESCRIPTION
    先运行 scripts\publish-portable.ps1 生成发布目录，再运行本脚本打包。

    发布目录被运行过之后，program\data（快捷键设置、窗口位置、诊断文件）和 program\.runtime
    （单文件运行时解压目录）会出现在里面，这些是本机数据，不能发给别人，也不应该因为打包而被删除。
    因此本脚本不在发布目录上直接打包，而是复制到系统临时目录下的一个新文件夹，
    在副本里排除这两个目录，再用副本生成 zip；发布目录本身不会被修改。

    为避免把错误的内容发出去，下列情况会直接中止，不会生成 zip：
      - 发布目录里的 exe 版本与 csproj 里的版本不一致（说明发布目录是旧的）；
      - 目标 zip 已存在（不覆盖已发出的版本）；
      - 生成后的 zip 里出现 data 或 .runtime，或缺少 README.txt、Start-NMTools.vbs、program/NMTools.App.exe。
    zip 根目录直接是 README.txt、Start-NMTools.vbs 和 program/，与历史版本的压缩包结构一致。
#>
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$publishRoot = Join-Path $projectRoot 'publish\NMTools-win-x64'
$exePath = Join-Path $publishRoot 'program\NMTools.App.exe'
$projectFile = Join-Path $projectRoot 'src\NMTools.App\NMTools.App.csproj'

# 打包时从副本中排除的目录（相对发布根目录）。这些目录只属于运行过该包的那台机器。
$excludedDirectories = @('program\data', 'program\.runtime')

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "找不到发布产物：$exePath。请先运行 scripts\publish-portable.ps1。"
}

[xml]$project = Get-Content -LiteralPath $projectFile -Raw
$version = ($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "无法从 $projectFile 读取版本号。"
}

$exeVersion = (Get-Item -LiteralPath $exePath).VersionInfo.FileVersion
if ($exeVersion -ne "$version.0") {
    throw "发布目录里的 exe 版本是 $exeVersion，而工程版本是 $version.0。发布目录已过期，请先重新运行 scripts\publish-portable.ps1。"
}

$zipPath = Join-Path $projectRoot "publish\NMTools-v$version-win-x64.zip"
if (Test-Path -LiteralPath $zipPath) {
    throw "目标压缩包已存在：$zipPath。不会覆盖；如确需重新打包，请先手动删除它。"
}

function Test-IsExcluded {
    param([string]$RelativePath)

    foreach ($excluded in $excludedDirectories) {
        if ($RelativePath.Equals($excluded, [StringComparison]::OrdinalIgnoreCase) -or
            $RelativePath.StartsWith($excluded + '\', [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }
    }

    return $false
}

# 临时副本放在系统临时目录下，名字带固定前缀和随机后缀，只由本次运行创建、只由本次运行删除。
$stagingPrefix = 'NMToolsPackage_'
$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) ($stagingPrefix + [guid]::NewGuid().ToString('N'))
$stagingContent = Join-Path $stagingRoot 'NMTools-win-x64'

try {
    New-Item -ItemType Directory -Path $stagingContent -Force | Out-Null

    $skipped = New-Object System.Collections.Generic.List[string]
    $publishFullPath = (Resolve-Path -LiteralPath $publishRoot).Path
    foreach ($item in Get-ChildItem -LiteralPath $publishFullPath -Recurse -Force) {
        $relative = $item.FullName.Substring($publishFullPath.Length).TrimStart('\')
        if (Test-IsExcluded $relative) {
            if (-not $item.PSIsContainer) {
                $skipped.Add($relative)
            }

            continue
        }

        $target = Join-Path $stagingContent $relative
        if ($item.PSIsContainer) {
            New-Item -ItemType Directory -Path $target -Force | Out-Null
        }
        else {
            Copy-Item -LiteralPath $item.FullName -Destination $target -Force
        }
    }

    if ($skipped.Count -gt 0) {
        Write-Host '以下本机运行产生的文件未放入压缩包（发布目录里的原文件保持不变）：' -ForegroundColor Yellow
        foreach ($name in $skipped) {
            Write-Host "  $name"
        }
    }

    # 不能用 ZipFile.CreateFromDirectory：Windows PowerShell 5.1 所用的 .NET Framework 会把条目名里的
    # 路径分隔符原样写成反斜杠，而 zip 规范要求正斜杠；反斜杠在 Linux/macOS 的解压工具里会变成名字带 \ 的单个文件。
    # 这里逐个文件写入，并把条目名统一成正斜杠，与历史版本的压缩包一致。
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zipStream = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite)
    try {
        $writer = New-Object System.IO.Compression.ZipArchive($zipStream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            $stagingFull = (Resolve-Path -LiteralPath $stagingContent).Path
            foreach ($file in Get-ChildItem -LiteralPath $stagingFull -Recurse -Force -File) {
                $entryName = $file.FullName.Substring($stagingFull.Length).TrimStart('\').Replace('\', '/')
                [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $writer,
                    $file.FullName,
                    $entryName,
                    [System.IO.Compression.CompressionLevel]::Optimal)
            }
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        $zipStream.Dispose()
    }
}
finally {
    # 只删除本次运行创建的临时副本：必须位于系统临时目录下、名字带固定前缀、且确实存在。
    $tempRoot = [System.IO.Path]::GetTempPath().TrimEnd('\')
    $isOwnStaging = $stagingRoot.StartsWith($tempRoot + '\', [StringComparison]::OrdinalIgnoreCase) -and
        ([System.IO.Path]::GetFileName($stagingRoot)).StartsWith($stagingPrefix, [StringComparison]::Ordinal)
    if ($isOwnStaging -and (Test-Path -LiteralPath $stagingRoot)) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}

# 对生成的 zip 做最终检查：不能带本机数据，必须包含运行所需的三个入口。
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $names = @($archive.Entries | ForEach-Object { $_.FullName })

    $backslashed = @($names | Where-Object { $_.Contains('\') })
    if ($backslashed.Count -gt 0) {
        throw "压缩包条目名里含有反斜杠，在非 Windows 解压工具里会出错：$($backslashed -join ', ')。请删除 $zipPath 并检查脚本。"
    }

    $leaked = @($names | Where-Object { $_ -match '(^|/)(data|\.runtime)(/|$)' })
    if ($leaked.Count -gt 0) {
        throw "压缩包里出现了不应发布的本机数据：$($leaked -join ', ')。请删除 $zipPath 并检查脚本。"
    }

    foreach ($required in @('README.txt', 'Start-NMTools.vbs', 'program/NMTools.App.exe')) {
        if ($names -notcontains $required) {
            throw "压缩包缺少必需文件：$required。请删除 $zipPath 并检查脚本。"
        }
    }

    Write-Host '压缩包内容：' -ForegroundColor Cyan
    foreach ($entry in $archive.Entries) {
        Write-Host ('  {0}  ({1:N0} 字节)' -f $entry.FullName, $entry.Length)
    }
}
finally {
    $archive.Dispose()
}

$zip = Get-Item -LiteralPath $zipPath
Write-Host ('已生成：{0}（{1:N0} 字节）' -f $zip.FullName, $zip.Length) -ForegroundColor Green
Write-Host ('SHA256：{0}' -f (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash)
