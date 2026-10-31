using System.Diagnostics;
using NMTools.Core.Updates;
using NMTools.Updater;

namespace NMTools.Core.Tests;

/// <summary>
/// 替换流程的集成测试：在临时目录里造一个真实的便携包结构，验证
/// "用户设置一定被保留""失败一定回滚"这两条最重要的规则。
/// </summary>
public sealed class UpdateApplierTests : IDisposable
{
    private readonly string _root;

    public UpdateApplierTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "NMTools_Updater_" + Guid.NewGuid().ToString("N"));
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
    public void Apply_PreservesUserSettingsAndLogs()
    {
        var package = CreatePackageRoot("v1");
        var app = CreateAppDirectory(package, "v1");
        File.WriteAllText(Path.Combine(app, "data", "input-bridge-settings.json"), "{\"secret\":\"user-config\"}");
        File.WriteAllText(Path.Combine(app, "logs", "NMTools-20260101.log"), "log line");

        var staged = CreateStagedVersion("v2");

        var result = new UpdateApplier(_ => { }).Apply(ExitedProcessId(), app, staged);

        Assert.True(result.IsSuccess, $"更新失败：{result.FailureReason}");
        // 新版本的程序文件已就位。
        Assert.Equal("v2", File.ReadAllText(Path.Combine(app, "version.txt")));
        // 用户设置原样保留，这是整个功能最重要的保证。
        Assert.Equal(
            "{\"secret\":\"user-config\"}",
            File.ReadAllText(Path.Combine(app, "data", "input-bridge-settings.json")));
        Assert.Equal("log line", File.ReadAllText(Path.Combine(app, "logs", "NMTools-20260101.log")));
    }

    [Fact]
    public void Apply_KeepsBackupDirectoryForLaterCleanup()
    {
        var package = CreatePackageRoot("v1");
        var app = CreateAppDirectory(package, "v1");

        var result = new UpdateApplier(_ => { }).Apply(ExitedProcessId(), app, CreateStagedVersion("v2"));

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.BackupDirectory);
        Assert.True(Directory.Exists(result.BackupDirectory));
        // 备份里仍是旧版本，回滚时能直接用。
        Assert.Equal("v1", File.ReadAllText(Path.Combine(result.BackupDirectory!, "version.txt")));
    }

    [Fact]
    public void Apply_FailsWhenBackupDirectoryAlreadyExists()
    {
        // 上次更新没清理干净时，那个目录可能是唯一一份可运行的旧版本，不能覆盖。
        var package = CreatePackageRoot("v1");
        var app = CreateAppDirectory(package, "v1");
        Directory.CreateDirectory(PortableLayout.ResolveBackupDirectory(app));

        var result = new UpdateApplier(_ => { }).Apply(ExitedProcessId(), app, CreateStagedVersion("v2"));

        Assert.False(result.IsSuccess);
        Assert.Equal(ApplyFailureReason.StaleBackup, result.FailureReason);
        // 原目录必须完好无损。
        Assert.Equal("v1", File.ReadAllText(Path.Combine(app, "version.txt")));
    }

    [Fact]
    public void Apply_FailsWhenStagedVersionHasNoExecutable()
    {
        var package = CreatePackageRoot("v1");
        var app = CreateAppDirectory(package, "v1");
        var emptyStaged = Path.Combine(_root, "empty-staged");
        Directory.CreateDirectory(emptyStaged);

        var result = new UpdateApplier(_ => { }).Apply(ExitedProcessId(), app, emptyStaged);

        Assert.False(result.IsSuccess);
        Assert.Equal(ApplyFailureReason.MissingNewProgram, result.FailureReason);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(app, "version.txt")));
    }

    [Fact]
    public async Task Apply_FailsWhenProcessStillRunning()
    {
        // 用一个不会退出的进程模拟"主程序还没退出"。
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c pause",
            CreateNoWindow = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        })!;

        try
        {
            var package = CreatePackageRoot("v1");
            var app = CreateAppDirectory(package, "v1");
            // 真实实现会等 30 秒；这里只验证等待期间绝不动文件。
            var applier = new UpdateApplier(_ => { });
            var task = Task.Run(() => applier.Apply(process.Id, app, CreateStagedVersion("v2")));

            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(1)));
            Assert.NotSame(task, completed);
            Assert.Equal("v1", File.ReadAllText(Path.Combine(app, "version.txt")));
        }
        finally
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void Apply_DeletesRuntimeCacheBeforeReplacing()
    {
        // .runtime 是单文件 exe 解压出的原生 DLL 缓存，杀毒软件扫描时会锁住整个目录。
        // 更新前必须删掉它，否则重命名 program/ 会失败——这是实际遇到过的故障。
        var package = CreatePackageRoot("v1");
        var app = CreateAppDirectory(package, "v1");
        var runtime = Path.Combine(app, PortableLayout.RuntimeDirectoryName, "NMTools.App", "abc");
        Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(runtime, "PresentationNative_cor3.dll"), "native");

        var result = new UpdateApplier(_ => { }).Apply(ExitedProcessId(), app, CreateStagedVersion("v2"));

        Assert.True(result.IsSuccess, $"更新失败：{result.FailureReason}");
        // 旧缓存不能跟到新版本里，新版本首次启动会自己解压。
        Assert.False(
            Directory.Exists(Path.Combine(app, PortableLayout.RuntimeDirectoryName)),
            ".runtime 缓存应被删除。");
        // 用户数据照旧保留。
        Assert.True(Directory.Exists(Path.Combine(app, "data")));
    }

    [Fact]
    public void Apply_FindsProgramDirectoryOneLevelDown()
    {
        // 真实链路：解压器返回的是「包根」（内含 program/），但如果调用方多包了一层，
        // 也要能找下去，否则会误判成"更新包不完整"。
        var package = CreatePackageRoot("v1");
        var app = CreateAppDirectory(package, "v1");
        var outer = Path.Combine(_root, "outer-" + Guid.NewGuid().ToString("N"));
        var versionDir = Path.Combine(outer, "NMTools-v0.7.0-win-x64");
        var innerProgram = Path.Combine(versionDir, "program");
        Directory.CreateDirectory(innerProgram);
        File.WriteAllText(Path.Combine(innerProgram, "NMTools.App.exe"), "v2");
        File.WriteAllText(Path.Combine(innerProgram, "version.txt"), "v2");

        var result = new UpdateApplier(_ => { }).Apply(ExitedProcessId(), app, outer);

        Assert.True(result.IsSuccess, $"更新失败：{result.FailureReason}");
        Assert.Equal("v2", File.ReadAllText(Path.Combine(app, "version.txt")));
    }

    [Fact]
    public void Apply_AcceptsVersionDirectoryAsExtractedRoot()
    {
        // 这正是修好后的真实调用形状：content/NMTools-v0.7.0-win-x64
        var package = CreatePackageRoot("v1");
        var app = CreateAppDirectory(package, "v1");
        var versionDir = Path.Combine(_root, "content-" + Guid.NewGuid().ToString("N"), "NMTools-v0.7.0-win-x64");
        var innerProgram = Path.Combine(versionDir, "program");
        Directory.CreateDirectory(innerProgram);
        File.WriteAllText(Path.Combine(innerProgram, "NMTools.App.exe"), "v2");
        File.WriteAllText(Path.Combine(innerProgram, "version.txt"), "v2");
        File.WriteAllText(Path.Combine(app, "data", "input-bridge-settings.json"), "{\"k\":1}");

        var result = new UpdateApplier(_ => { }).Apply(ExitedProcessId(), app, versionDir);

        Assert.True(result.IsSuccess, $"更新失败：{result.FailureReason}");
        Assert.Equal("v2", File.ReadAllText(Path.Combine(app, "version.txt")));
        // 用户设置仍然保留。
        Assert.Equal("{\"k\":1}", File.ReadAllText(Path.Combine(app, "data", "input-bridge-settings.json")));
    }

    [Fact]
    public void Apply_AcceptsStagedDirectoryThatIsProgramItself()
    {
        // 解压结果可能是「包根目录/program」或「program 内容本身」两种形状。
        var package = CreatePackageRoot("v1");
        var app = CreateAppDirectory(package, "v1");
        var stagedProgramOnly = Path.Combine(_root, "staged-program");
        Directory.CreateDirectory(stagedProgramOnly);
        File.WriteAllText(Path.Combine(stagedProgramOnly, "NMTools.App.exe"), "v2");
        File.WriteAllText(Path.Combine(stagedProgramOnly, "version.txt"), "v2");

        var result = new UpdateApplier(_ => { }).Apply(ExitedProcessId(), app, stagedProgramOnly);

        Assert.True(result.IsSuccess);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(app, "version.txt")));
    }

    [Fact]
    public void Rollback_RestoresPreviousVersion()
    {
        var package = CreatePackageRoot("v1");
        var app = CreateAppDirectory(package, "v1");
        var backup = PortableLayout.ResolveBackupDirectory(app);
        Directory.Move(app, backup);
        // 半成品新版本。
        Directory.CreateDirectory(app);
        File.WriteAllText(Path.Combine(app, "version.txt"), "broken");

        var rolledBack = new UpdateApplier(_ => { }).Rollback(app, backup);

        Assert.True(rolledBack);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(app, "version.txt")));
    }

    [Fact]
    public void Apply_DoesNotTouchFilesOutsideProgramDirectory()
    {
        // 同级目录（如 Start-NMTools.vbs）不能被更新过程动到。
        var package = CreatePackageRoot("v1");
        var app = CreateAppDirectory(package, "v1");
        var launcher = Path.Combine(package, "Start-NMTools.vbs");
        File.WriteAllText(launcher, "launcher content");

        new UpdateApplier(_ => { }).Apply(ExitedProcessId(), app, CreateStagedVersion("v2"));

        Assert.True(File.Exists(launcher));
        Assert.Equal("launcher content", File.ReadAllText(launcher));
    }

    /// <summary>
    /// 一个已经退出的进程 ID，用于让 <see cref="UpdateApplier"/> 认为"主程序已结束"。
    ///
    /// 不能用 Environment.ProcessId：那是测试进程自己，永远不退出，会让每次等待都撑满 30 秒超时。
    /// </summary>
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

    private string CreatePackageRoot(string version)
    {
        var package = Path.Combine(_root, $"pkg-{version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(package, "program"));
        return package;
    }

    private string CreateAppDirectory(string package, string version)
    {
        var app = Path.Combine(package, "program");
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(Path.Combine(app, "data"));
        Directory.CreateDirectory(Path.Combine(app, "logs"));
        File.WriteAllText(Path.Combine(app, "NMTools.App.exe"), version);
        File.WriteAllText(Path.Combine(app, "version.txt"), version);
        return app;
    }

    private string CreateStagedVersion(string version)
    {
        var staged = Path.Combine(_root, $"staged-{version}-{Guid.NewGuid():N}");
        var program = Path.Combine(staged, "program");
        Directory.CreateDirectory(program);
        File.WriteAllText(Path.Combine(program, "NMTools.App.exe"), version);
        File.WriteAllText(Path.Combine(program, "version.txt"), version);
        return staged;
    }
}
