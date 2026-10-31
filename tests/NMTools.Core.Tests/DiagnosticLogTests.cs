using NMTools.App.Infrastructure;

namespace NMTools.Core.Tests;

public sealed class DiagnosticLogTests : IDisposable
{
    private readonly string _directory;

    public DiagnosticLogTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "NMTools_LogTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // 测试目录清理失败不影响结论。
        }
    }

    /// <summary>
    /// 写入线程持有文件句柄期间，读取必须显式声明共享写入权限，
    /// 否则 File.ReadAllText 会因共享冲突抛 IOException。这也正是外部查看日志时的真实情形。
    /// </summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void WaitForContent(string path, Func<string, bool> predicate, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && predicate(ReadShared(path)))
            {
                return;
            }

            Thread.Sleep(20);
        }

        Assert.Fail($"日志文件在超时前未出现期望内容：{path}");
    }

    private string SingleLogPath => Path.Combine(
        _directory,
        $"NMTools-{DateTime.Now:yyyyMMdd}.log");

    [Fact]
    public void Write_AppendsTimestampedLine()
    {
        using var log = new DiagnosticLog(_directory, ownsDirectory: false);

        log.Info("test.event", "hello");

        WaitForContent(SingleLogPath, text => text.Contains("test.event") && text.Contains("hello"));
        var content = ReadShared(SingleLogPath);
        Assert.Contains("[INFO]", content);
    }

    [Fact]
    public void Error_IncludesHResultAndExceptionType()
    {
        using var log = new DiagnosticLog(_directory, ownsDirectory: false);
        var exception = new InvalidOperationException("boom")
        {
            HResult = unchecked((int)0x800401D0),
        };

        log.Error("test.error", "failed", exception);

        WaitForContent(SingleLogPath, text => text.Contains("0x800401D0"));
        var content = ReadShared(SingleLogPath);
        Assert.Contains("InvalidOperationException", content);
        Assert.Contains("[ERROR]", content);
    }

    [Fact]
    public void Info_FlattensLineBreaksToKeepOneRecordPerLine()
    {
        using var log = new DiagnosticLog(_directory, ownsDirectory: false);

        log.Info("test.multiline", "first\r\nsecond\nthird");

        WaitForContent(SingleLogPath, text => text.Contains("first second third"));
        var lines = ReadShared(SingleLogPath)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Length > 0)
            .ToArray();
        Assert.Single(lines);
    }

    [Fact]
    public void DisabledLog_WritesNothing()
    {
        using var log = new DiagnosticLog(_directory, enabled: false, ownsDirectory: false);

        log.Info("test.event", "should not appear");
        log.Flush();

        Assert.False(log.IsEnabled);
        Assert.False(File.Exists(SingleLogPath));
    }

    [Fact]
    public async Task ConcurrentWrites_AllRecordsAreWritten()
    {
        const int count = 200;
        using var log = new DiagnosticLog(_directory, ownsDirectory: false);

        await Task.WhenAll(Enumerable.Range(0, count).Select(index =>
            Task.Run(() => log.Info("test.concurrent", $"index={index}"))));

        WaitForContent(
            SingleLogPath,
            text => Enumerable.Range(0, count).All(index => text.Contains($"index={index}")));
        var matched = ReadShared(SingleLogPath)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Count(line => line.Contains("test.concurrent"));
        Assert.Equal(count, matched);
    }

    [Fact]
    public void Cleanup_DoesNotDeleteUnrelatedFiles()
    {
        var unrelated = Path.Combine(_directory, "user-notes.txt");
        File.WriteAllText(unrelated, "keep me");
        var foreignLog = Path.Combine(_directory, "OtherApp-20200101.log");
        File.WriteAllText(foreignLog, "not ours");

        using (var log = new DiagnosticLog(_directory, ownsDirectory: true))
        {
            log.Info("test.event", "trigger cleanup");
            log.Flush();
        }

        Assert.True(File.Exists(unrelated), "不属于 NMTools 的文件不应被删除。");
        Assert.True(File.Exists(foreignLog), "命名不符合 NMTools-yyyyMMdd.log 的日志不应被删除。");
    }

    [Fact]
    public void Cleanup_RemovesLogsOlderThanRetentionWindow()
    {
        var expired = Path.Combine(_directory, $"NMTools-{DateTime.Now.AddDays(-30):yyyyMMdd}.log");
        File.WriteAllText(expired, "old");

        using (var log = new DiagnosticLog(
            _directory,
            maxTotalBytes: 8L * 1024 * 1024,
            maxRetainedDays: 7,
            ownsDirectory: true))
        {
            log.Info("test.event", "trigger cleanup");
            log.Flush();
        }

        Assert.False(File.Exists(expired), "超过保留天数的 NMTools 日志应被清理。");
        Assert.True(File.Exists(SingleLogPath), "当天日志必须保留。");
    }

    [Fact]
    public void Cleanup_RespectsTotalSizeLimit()
    {
        var oversized = Path.Combine(_directory, $"NMTools-{DateTime.Now.AddDays(-3):yyyyMMdd}.log");
        File.WriteAllBytes(oversized, new byte[64 * 1024]);

        using (var log = new DiagnosticLog(
            _directory,
            maxTotalBytes: 16 * 1024,
            maxRetainedDays: 365,
            ownsDirectory: true))
        {
            log.Info("test.event", "trigger cleanup");
            log.Flush();
        }

        Assert.False(File.Exists(oversized), "超出总大小上限时应从最旧日志开始清理。");
        Assert.True(File.Exists(SingleLogPath), "当天日志不能被大小清理删除。");
    }

    [Fact]
    public void IsOwnedByApp_DetectsProgramDirectory()
    {
        Assert.True(DiagnosticLog.IsOwnedByApp(Path.Combine(AppContext.BaseDirectory, "logs")));
        Assert.False(DiagnosticLog.IsOwnedByApp(Path.Combine(Path.GetTempPath(), "somewhere")));
    }
}
