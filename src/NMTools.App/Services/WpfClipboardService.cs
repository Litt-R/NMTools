using System.Runtime.InteropServices;
using System.Windows;
using NMTools.App.Infrastructure;
using NMTools.App.Interop;
using NMTools.Core;

namespace NMTools.App.Services;

public sealed class WpfClipboardService : IClipboardService
{
    private readonly IDiagnosticLog? _log;

    public WpfClipboardService(IDiagnosticLog? log = null)
    {
        _log = log;
    }

    public async Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();

        var maxAttempts = ClipboardWriteRetrySchedule.MaxAttempts;
        var waitedMilliseconds = 0;
        Exception? lastException = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var sequenceBefore = ClipboardDiagnostics.GetSequenceNumber();
            try
            {
                System.Windows.Clipboard.SetText(text, System.Windows.TextDataFormat.UnicodeText);
                var sequenceAfter = ClipboardDiagnostics.GetSequenceNumber();
                if (attempt > 1)
                {
                    // 记录"第几次重试才成功"和累计等待，这是判断竞争窗口有多长的直接证据。
                    _log?.Info(
                        "clipboard.write.retrySucceeded",
                        $"attempt={attempt}/{maxAttempts} waitedMs={waitedMilliseconds} "
                        + $"sequenceChanged={sequenceBefore != sequenceAfter}");
                }

                return;
            }
            catch (Exception exception) when (IsClipboardException(exception))
            {
                lastException = exception;
                var delay = ClipboardWriteRetrySchedule.GetDelayAfterAttempt(attempt);

                // 失败现场必须完整留证：错误码、剪贴板持有者、序列号是否变化。
                // 这是排查"写入剪贴板失败"唯一有价值的信息来源。
                _log?.Warn(
                    "clipboard.write.failed",
                    $"attempt={attempt}/{maxAttempts} waitedMs={waitedMilliseconds} nextDelayMs={delay} "
                    + $"textLength={text.Length} "
                    + ClipboardDiagnostics.DescribeWriteAttempt(sequenceBefore, ClipboardDiagnostics.GetSequenceNumber(), exception));

                if (delay > 0)
                {
                    await Task.Delay(delay, cancellationToken);
                    waitedMilliseconds += delay;
                }
            }
        }

        _log?.Error(
            "clipboard.write.exhausted",
            $"attempts={maxAttempts} waitedMs={waitedMilliseconds} {BuildExhaustedMessage(text, lastException)}",
            lastException);
        throw new InvalidOperationException(
            BuildExhaustedMessage(text, lastException),
            lastException);
    }

    public Task<bool> MatchesTextAsync(
        string expectedText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedText);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var actualText = System.Windows.Clipboard.ContainsText(System.Windows.TextDataFormat.UnicodeText)
                ? System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText)
                : null;
            var matches = string.Equals(actualText, expectedText, StringComparison.Ordinal);
            if (!matches)
            {
                // 只记录长度和差异类型，绝不记录内容。
                _log?.Warn(
                    "clipboard.verify.mismatch",
                    $"expectedLength={expectedText.Length} actualLength={actualText?.Length ?? -1} "
                    + $"actualNull={actualText is null}");
            }

            return Task.FromResult(matches);
        }
        catch (Exception exception) when (IsClipboardException(exception))
        {
            _log?.Warn("clipboard.verify.failed", $"hr=0x{exception.HResult:X8} type={exception.GetType().Name}");
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// 生成可读的失败说明。错误码和占用者是用户/维护者定位问题的关键，
    /// 同时绝不把用户输入正文写进异常消息（消息会显示在弹窗里，也可能被截图分享）。
    /// </summary>
    private static string BuildExhaustedMessage(string text, Exception? lastException)
    {
        var detail = lastException is null
            ? "未知原因"
            : $"HRESULT=0x{lastException.HResult:X8}";
        var owner = ClipboardDiagnostics.DescribeClipboardOwner();
        var ownerText = owner is null ? "未检测到窗口持有者" : owner;
        var attempts = ClipboardWriteRetrySchedule.MaxAttempts;
        var seconds = ClipboardWriteRetrySchedule.EstimatedWorstCaseMilliseconds / 1000.0;
        return $"无法写入 Windows 剪贴板（已重试 {attempts} 次，耗时约 {seconds:0.#} 秒）。\n"
            + $"错误码：{detail}\n剪贴板占用：{ownerText}\n"
            + $"本次文字长度 {text.Length} 字。详细日志见程序目录 logs 文件夹。";
    }

    private static bool IsClipboardException(Exception exception)
    {
        return exception is ExternalException or COMException or InvalidOperationException;
    }
}
