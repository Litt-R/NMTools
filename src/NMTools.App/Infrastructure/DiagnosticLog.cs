using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace NMTools.App.Infrastructure;

/// <summary>
/// 轻量诊断日志：按天写入 program/logs，后台线程落盘，写失败静默降级。
///
/// 设计约束（见 docs/ARCHITECTURE.md 9.1）：
/// - 只记录环境快照和事件，绝不记录用户输入正文、草稿、剪贴板内容或凭据；
/// - 写入失败不得影响主流程，队列溢出时丢弃最旧记录而不是阻塞调用方；
/// - 每次写入前检查按天滚动，避免长时间运行后日志无限增长。
/// </summary>
public sealed class DiagnosticLog : IDiagnosticLog, IDisposable
{
    private const string LogFilePrefix = "NMTools-";
    private const string LogFileExtension = ".log";
    private const int MaxQueuedEntries = 2048;
    private const long DefaultMaxTotalBytes = 8L * 1024 * 1024;
    private const int DefaultMaxRetainedDays = 14;
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(3);

    private readonly string _directory;
    private readonly bool _ownsDirectory;
    private readonly long _maxTotalBytes;
    private readonly int _maxRetainedDays;
    private readonly BlockingCollection<string> _queue =
        new(new ConcurrentQueue<string>(), MaxQueuedEntries);
    private readonly Thread _writerThread;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _lifecycleLock = new();
    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private volatile bool _disposed;
    private long _droppedEntries;

    public DiagnosticLog(
        string directory,
        bool enabled = true,
        long maxTotalBytes = DefaultMaxTotalBytes,
        int maxRetainedDays = DefaultMaxRetainedDays,
        bool? ownsDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        // 只有本程序自己的日志目录才做自动清理，避免误删用户或其他程序的文件。
        _ownsDirectory = ownsDirectory ?? IsOwnedByApp(directory);
        _maxTotalBytes = maxTotalBytes;
        _maxRetainedDays = maxRetainedDays;
        IsEnabled = enabled;

        _writerThread = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "NMTools.DiagnosticLog",
        };
        _writerThread.Start();
    }

    public bool IsEnabled { get; }

    public static bool IsOwnedByApp(string directory)
    {
        // 只有本程序目录下的 data/logs 才归 NMTools 所有；其他位置的日志不做自动清理，
        // 避免误删用户或其他程序的文件。
        var full = Path.GetFullPath(directory);
        return full.StartsWith(
            Path.GetFullPath(AppContext.BaseDirectory),
            StringComparison.OrdinalIgnoreCase);
    }

    public string? CurrentLogFilePath { get; private set; }

    public long DroppedEntries => Interlocked.Read(ref _droppedEntries);

    public static string ResolveLogDirectory()
    {
        return Path.Combine(AppContext.BaseDirectory, "logs");
    }

    public static DiagnosticLog CreateForAppDirectory()
    {
        return new DiagnosticLog(ResolveLogDirectory());
    }

    public void Info(string eventName, string message)
    {
        Enqueue("INFO", eventName, message, exception: null);
    }

    public void Warn(string eventName, string message)
    {
        Enqueue("WARN", eventName, message, exception: null);
    }

    public void Error(string eventName, string message, Exception? exception = null)
    {
        Enqueue("ERROR", eventName, message, exception);
    }

    public void Timing(string eventName, long elapsedMilliseconds)
    {
        Enqueue("INFO", eventName, $"elapsedMs={elapsedMilliseconds}", exception: null);
    }

    public void Flush()
    {
        if (_disposed || !IsEnabled)
        {
            return;
        }

        try
        {
            // 尽力等待后台队列排空；超时即返回，绝不因为日志阻塞调用方。
            var deadline = DateTime.UtcNow + FlushTimeout;
            while (_queue.Count > 0 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }
        }
        catch
        {
            // Flush 只是尽力而为，失败不影响正确性。
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _queue.CompleteAdding();
            try
            {
                _writerThread.Join(DisposeTimeout);
            }
            catch
            {
                // 后台线程退出失败不应阻止进程退出。
            }

            _writer?.Dispose();
            _writer = null;
            _queue.Dispose();
            _shutdown.Dispose();
        }
    }

    private void Enqueue(string level, string eventName, string message, Exception? exception)
    {
        if (!IsEnabled || _disposed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(eventName))
        {
            eventName = "unknown";
        }

        var builder = new StringBuilder(160);
        builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        builder.Append(" [").Append(level).Append("] ");
        builder.Append(Sanitize(eventName));
        if (!string.IsNullOrEmpty(message))
        {
            builder.Append(" | ").Append(Sanitize(message));
        }

        if (exception is not null)
        {
            // 只记录异常类型、HResult 和消息；异常消息可能含路径但不会含用户输入正文。
            builder.Append(" | ex=").Append(Sanitize(exception.GetType().Name));
            builder.Append(" hr=0x").Append(exception.HResult.ToString("X8"));
            if (!string.IsNullOrWhiteSpace(exception.Message))
            {
                builder.Append(" msg=").Append(Sanitize(exception.Message));
            }

            var inner = exception.InnerException;
            var depth = 0;
            while (inner is not null && depth < 3)
            {
                builder.Append(" <- ").Append(Sanitize(inner.GetType().Name));
                builder.Append(" hr=0x").Append(inner.HResult.ToString("X8"));
                inner = inner.InnerException;
                depth++;
            }
        }

        var line = builder.ToString();

        // 队列满时丢弃并计数：宁可少记日志，也不能阻塞热路径或拖慢输入。
        if (!_queue.TryAdd(line))
        {
            Interlocked.Increment(ref _droppedEntries);
        }
    }

    /// <summary>
    /// 单行化并移除换行控制字符，防止多行内容破坏日志格式。
    /// </summary>
    private static string Sanitize(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        return value
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }

    private void WriteLoop()
    {
        try
        {
            foreach (var line in _queue.GetConsumingEnumerable(_shutdown.Token))
            {
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                try
                {
                    EnsureWriter();
                    if (_writer is null)
                    {
                        continue;
                    }

                    _writer.WriteLine(line);
                }
                catch
                {
                    // 单条写入失败不影响后续记录；下一次循环会尝试重新打开文件。
                    CloseWriter();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            CloseWriter();
        }
    }

    private void EnsureWriter()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_writer is not null && _writerDate == today)
        {
            return;
        }

        CloseWriter();

        Directory.CreateDirectory(_directory);
        var path = Path.Combine(
            _directory,
            $"{LogFilePrefix}{today:yyyyMMdd}{LogFileExtension}");
        // FileShare.ReadWrite | FileShare.Delete：用户排查问题时可以用记事本或 tail 类工具
        // 直接打开正在写入的日志，也可以在程序运行时把日志文件复制走，不会被句柄占用挡住。
        var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        _writerDate = today;
        CurrentLogFilePath = path;

        if (_ownsDirectory)
        {
            TryCleanupOldLogs(today);
        }
    }

    private void CloseWriter()
    {
        try
        {
            _writer?.Dispose();
        }
        catch
        {
            // 关闭失败交给下一次重新打开。
        }
        finally
        {
            _writer = null;
        }
    }

    /// <summary>
    /// 只删除本程序目录下、符合 NMTools-yyyyMMdd.log 命名规则的文件。
    /// 按天数上限和总大小上限双重清理，绝不递归删除目录或处理其他文件。
    /// </summary>
    private void TryCleanupOldLogs(DateOnly today)
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            var files = new List<(FileInfo Info, DateOnly Date)>();
            foreach (var path in Directory.EnumerateFiles(_directory, "NMTools-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (!name.StartsWith(LogFilePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var stamp = name[LogFilePrefix.Length..];
                if (stamp.Length != 8
                    || !DateOnly.TryParseExact(
                        stamp,
                        "yyyyMMdd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var fileDate))
                {
                    // 不符合命名规则的文件不属于 NMTools，一律不碰。
                    continue;
                }

                files.Add((new FileInfo(path), fileDate));
            }

            var cutoff = today.AddDays(-_maxRetainedDays);
            foreach (var entry in files.Where(file => file.Date < cutoff).ToList())
            {
                TryDeleteLog(entry.Info);
                files.Remove(entry);
            }

            var totalBytes = files.Sum(file => SafeLength(file.Info));
            if (totalBytes <= _maxTotalBytes)
            {
                return;
            }

            // 从最旧的开始删，直到总量回到上限以内；当天文件最后才考虑。
            foreach (var entry in files.OrderBy(file => file.Date))
            {
                if (totalBytes <= _maxTotalBytes)
                {
                    break;
                }

                if (entry.Date == today)
                {
                    continue;
                }

                var length = SafeLength(entry.Info);
                if (TryDeleteLog(entry.Info))
                {
                    totalBytes -= length;
                }
            }
        }
        catch
        {
            // 清理失败不是错误：日志清理属于非核心维护，不能让主流程感知。
        }
    }

    private static long SafeLength(FileInfo info)
    {
        try
        {
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryDeleteLog(FileInfo info)
    {
        try
        {
            info.Delete();
            return true;
        }
        catch
        {
            // 文件可能正被其他实例或查看器占用，下一轮清理再试。
            return false;
        }
    }
}
