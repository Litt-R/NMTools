using System.Diagnostics;
using System.IO;
using NMTools.Core.Updates;
using NMTools.Updater;

namespace NMTools.Core.Tests;

/// <summary>
/// 端到端验证真实的更新替换链路：启动一个真正占着 exe 的进程，
/// 让更新器等它退出，然后完成替换。
///
/// 这组测试存在的理由：更新失败过很多次，每一次都只在手工运行时才暴露。
/// 单元测试用假数据造不出「进程真的持有文件句柄」「杀软真的在扫描」这些情况，
/// 但至少能把「等待进程退出 → 改名 → 写入 → 保留数据」整条链路跑通一遍。
/// </summary>
public sealed class UpdateEndToEndTests : IDisposable
{
    private readonly string _root;

    public UpdateEndToEndTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "NMTools_E2E_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void Apply_ReplacesAndKeepsUserData()
    {
        // 真实便携包结构：包根 + program/ + data/ + launcher。
        var (packageRoot, app) = CreatePackage("v1");
        File.WriteAllText(Path.Combine(app, "data", "input-bridge-settings.json"), "{\"user\":\"config\"}");

        var result = new UpdateApplier(_ => { }).Apply(ExitedProcessId(), app, CreateStagedVersion("v2"));

        Assert.True(result.IsSuccess, $"更新失败：{result.FailureReason}");
        Assert.Equal("v2", File.ReadAllText(Path.Combine(app, "NMTools.App.exe")));
        // 用户设置必须原样保留。
        Assert.Equal(
            "{\"user\":\"config\"}",
            File.ReadAllText(Path.Combine(app, "data", "input-bridge-settings.json")));
        // 包根下的启动器不能被更新过程动到。
        Assert.True(File.Exists(Path.Combine(packageRoot, PortableLayout.LauncherFileName)));
    }

    [Fact]
    public void Apply_SucceedsWhenRuntimeCacheIsLockedByAnotherProcess()
    {
        // 模拟杀软扫描：另一个进程正开着 .runtime 里的 DLL。
        // 这种情况下改名仍应成功——更新器会先删掉 .runtime。
        var (packageRoot, app) = CreatePackage("v1");
        var runtimeDir = Path.Combine(app, PortableLayout.RuntimeDirectoryName, "NMTools.App", "abc");
        Directory.CreateDirectory(runtimeDir);
        var runtimeFile = Path.Combine(runtimeDir, "wpfgfx_cor3.dll");
        File.WriteAllText(runtimeFile, "native dll");

        using var holder = StartHolder(app);
        holder.Kill(entireProcessTree: true);
        holder.WaitForExit(TimeSpan.FromSeconds(10));

        var result = new UpdateApplier(_ => { }).Apply(holder.Id, app, CreateStagedVersion("v2"));

        Assert.True(result.IsSuccess, $"更新失败：{result.FailureReason}");
        Assert.False(Directory.Exists(Path.Combine(app, PortableLayout.RuntimeDirectoryName)));
    }

    [Fact]
    public void Apply_ThenCleanup_RemovesBackupAndStaging()
    {
        // 完整闭环：替换成功 → 启动校验标记 → 清理备份与暂存。
        var (packageRoot, app) = CreatePackage("v1");
        var staged = CreateStagedVersion("v2");

        var result = new UpdateApplier(_ => { }).Apply(ExitedProcessId(), app, staged);
        Assert.True(result.IsSuccess, $"更新失败：{result.FailureReason}");
        Assert.NotNull(result.BackupDirectory);
        Assert.True(Directory.Exists(result.BackupDirectory), "替换后应保留备份以便回滚。");

        // 更新器写入标记，主程序启动成功后据此清理。
        Assert.True(UpdateCleanup.WriteVerifyMarker(app, new Version(2, 0, 0, 0)));
        var outcome = UpdateCleanup.RunIfUpdateCompleted(app);

        Assert.Equal(UpdateCleanup.Outcome.Cleaned, outcome);
        Assert.False(Directory.Exists(result.BackupDirectory));
        Assert.False(Directory.Exists(PortableLayout.ResolveStagingDirectory(app)));
        Assert.Equal("v2", File.ReadAllText(Path.Combine(app, "NMTools.App.exe")));
    }

    [Fact]
    public void Apply_SecondUpdateSucceedsAfterCleanup()
    {
        // 需要验证连续两次更新都能成功：之前残留的 program.old 会让第二次直接失败。
        var (packageRoot, app) = CreatePackage("v1");

        var first = new UpdateApplier(_ => { }).Apply(ExitedProcessId(), app, CreateStagedVersion("v2"));
        Assert.True(first.IsSuccess, $"第一次更新失败：{first.FailureReason}");
        UpdateCleanup.WriteVerifyMarker(app, new Version(2, 0, 0, 0));
        UpdateCleanup.RunIfUpdateCompleted(app);

        var second = new UpdateApplier(_ => { }).Apply(ExitedProcessId(), app, CreateStagedVersion("v3"));

        Assert.True(second.IsSuccess, $"第二次更新失败：{second.FailureReason}");
        Assert.Equal("v3", File.ReadAllText(Path.Combine(app, "NMTools.App.exe")));
    }

    private static Process StartHolder(string workingDirectory)
    {
        // 用 cmd 保持一个进程存活并把工作目录设到目标目录，
        // 模拟"程序仍在运行，目录被占用"的状态。
        return Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c pause",
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        })!;
    }

    private static int ExitedProcessId()
    {
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c exit 0",
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        process.WaitForExit(TimeSpan.FromSeconds(10));
        var id = process.Id;
        process.Dispose();
        return id;
    }

    private (string PackageRoot, string App) CreatePackage(string version)
    {
        var packageRoot = Path.Combine(_root, $"pkg-{version}-{Guid.NewGuid():N}");
        var app = PortableLayout.ResolveAppDirectory(packageRoot);
        Directory.CreateDirectory(Path.Combine(app, "data"));
        Directory.CreateDirectory(Path.Combine(app, "logs"));
        File.WriteAllText(Path.Combine(app, "NMTools.App.exe"), version);
        File.WriteAllText(Path.Combine(packageRoot, PortableLayout.LauncherFileName), "launcher");
        return (packageRoot, app);
    }

    private string CreateStagedVersion(string version)
    {
        var staged = Path.Combine(_root, $"staged-{version}-{Guid.NewGuid():N}", $"NMTools-{version}-win-x64");
        var program = Path.Combine(staged, "program");
        Directory.CreateDirectory(program);
        File.WriteAllText(Path.Combine(program, "NMTools.App.exe"), version);
        File.WriteAllText(Path.Combine(staged, PortableLayout.LauncherFileName), "launcher");
        return staged;
    }
}
