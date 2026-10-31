using NMTools.Core.Updates;

namespace NMTools.Core.Tests;

/// <summary>
/// 更新完成后的清理。关键约束：只有新版本真的启动过才能删备份，
/// 否则一旦新版本起不来就没有可回滚的版本了。
/// </summary>
public sealed class UpdateCleanupTests : IDisposable
{
    private readonly string _root;

    public UpdateCleanupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "NMTools_Cleanup_" + Guid.NewGuid().ToString("N"));
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
    public void RunIfUpdateCompleted_WithoutMarker_DoesNothing()
    {
        var (package, app) = CreatePackage();
        var backup = PortableLayout.ResolveBackupDirectory(app);
        Directory.CreateDirectory(backup);

        var outcome = UpdateCleanup.RunIfUpdateCompleted(app);

        Assert.Equal(UpdateCleanup.Outcome.NothingToDo, outcome);
        // 没有标记就不能动备份——可能是上次更新没跑完，备份是唯一的回滚手段。
        Assert.True(Directory.Exists(backup));
    }

    [Fact]
    public void RunIfUpdateCompleted_WithMarker_DeletesBackupAndStaging()
    {
        var (package, app) = CreatePackage();
        var backup = PortableLayout.ResolveBackupDirectory(app);
        var staging = PortableLayout.ResolveStagingDirectory(app);
        Directory.CreateDirectory(backup);
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(backup, "old.txt"), "old version");
        Assert.True(UpdateCleanup.WriteVerifyMarker(app, new Version(0, 8, 0, 0)));

        var outcome = UpdateCleanup.RunIfUpdateCompleted(app);

        Assert.Equal(UpdateCleanup.Outcome.Cleaned, outcome);
        Assert.False(Directory.Exists(backup), "旧版本备份应被删除。");
        Assert.False(Directory.Exists(staging), "暂存目录应被删除。");
        Assert.False(
            File.Exists(PortableLayout.ResolveVerifyUpdateMarker(app)),
            "确认标记应被删除。");
    }

    [Fact]
    public void RunIfUpdateCompleted_LeavesDataAndLogs()
    {
        var (package, app) = CreatePackage();
        File.WriteAllText(Path.Combine(app, "data", "input-bridge-settings.json"), "{\"k\":1}");
        File.WriteAllText(Path.Combine(app, "logs", "NMTools.log"), "log");
        UpdateCleanup.WriteVerifyMarker(app, new Version(0, 8, 0, 0));

        UpdateCleanup.RunIfUpdateCompleted(app);

        // 清理只碰备份和暂存，用户数据和日志必须原样保留。
        Assert.Equal(
            "{\"k\":1}",
            File.ReadAllText(Path.Combine(app, "data", "input-bridge-settings.json")));
        Assert.Equal("log", File.ReadAllText(Path.Combine(app, "logs", "NMTools.log")));
    }

    [Fact]
    public void RunIfUpdateCompleted_CorruptMarker_StillCleansUp()
    {
        var (package, app) = CreatePackage();
        var backup = PortableLayout.ResolveBackupDirectory(app);
        Directory.CreateDirectory(backup);
        File.WriteAllText(PortableLayout.ResolveVerifyUpdateMarker(app), "{ broken");

        var outcome = UpdateCleanup.RunIfUpdateCompleted(app);

        // 标记损坏不该让清理卡住：标记只表示"有一次更新刚完成"，没有需要校验的决策信息。
        Assert.Equal(UpdateCleanup.Outcome.InvalidMarker, outcome);
        Assert.False(Directory.Exists(backup));
    }

    [Fact]
    public void RunIfUpdateCompleted_IsIdempotent()
    {
        var (package, app) = CreatePackage();
        Directory.CreateDirectory(PortableLayout.ResolveBackupDirectory(app));
        UpdateCleanup.WriteVerifyMarker(app, new Version(0, 8, 0, 0));

        var first = UpdateCleanup.RunIfUpdateCompleted(app);
        var second = UpdateCleanup.RunIfUpdateCompleted(app);

        Assert.Equal(UpdateCleanup.Outcome.Cleaned, first);
        Assert.Equal(UpdateCleanup.Outcome.NothingToDo, second);
    }

    [Fact]
    public void WriteVerifyMarker_RecordsVersion()
    {
        var (package, app) = CreatePackage();

        Assert.True(UpdateCleanup.WriteVerifyMarker(app, new Version(0, 8, 0, 0)));

        var marker = PortableLayout.ResolveVerifyUpdateMarker(app);
        Assert.True(File.Exists(marker));
        Assert.Contains("0.8.0.0", File.ReadAllText(marker));
    }

    private (string Package, string App) CreatePackage()
    {
        var package = Path.Combine(_root, "pkg-" + Guid.NewGuid().ToString("N"));
        var app = PortableLayout.ResolveAppDirectory(package);
        Directory.CreateDirectory(Path.Combine(app, "data"));
        Directory.CreateDirectory(Path.Combine(app, "logs"));
        File.WriteAllText(Path.Combine(app, PortableLayout.AppExecutableName), "exe");
        return (package, app);
    }
}
