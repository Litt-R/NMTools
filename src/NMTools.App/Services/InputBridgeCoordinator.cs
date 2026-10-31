using System.Diagnostics;
using NMTools.App.Infrastructure;
using NMTools.Core;

namespace NMTools.App.Services;

public sealed class InputBridgeCoordinator
{
    private readonly ITargetWindowService _targetWindowService;
    private readonly IClipboardService _clipboardService;
    private readonly ITextInjectionService _textInjectionService;
    private readonly InputBridgeOptions _options;
    private readonly Func<string?> _requestText;
    private readonly IDiagnosticLog? _log;
    private const int ClipboardVerificationAttempts = 3;
    private const int ClipboardVerificationDelayMilliseconds = 100;
    private const string TargetUnavailableMessage = "目标 NoMachine 窗口已不可用，文字已保留在剪贴板中。";

    private readonly PasteMethod _pasteMethod;
    private readonly RemoteInput? _customInput;

    public InputBridgeCoordinator(
        ITargetWindowService targetWindowService,
        IClipboardService clipboardService,
        ITextInjectionService textInjectionService,
        InputBridgeOptions options,
        Func<string?> requestText,
        PasteShortcutAction? pasteAction = null,
        IDiagnosticLog? log = null)
    {
        _targetWindowService = targetWindowService ?? throw new ArgumentNullException(nameof(targetWindowService));
        _clipboardService = clipboardService ?? throw new ArgumentNullException(nameof(clipboardService));
        _textInjectionService = textInjectionService ?? throw new ArgumentNullException(nameof(textInjectionService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _requestText = requestText ?? throw new ArgumentNullException(nameof(requestText));
        _log = log;
        var binding = _options.PasteBindings.GetBinding(pasteAction ?? PasteShortcutAction.Enter);
        _pasteMethod = binding.Method;
        _customInput = binding.CustomInput;
    }

    public event EventHandler<string>? OperationFailed;

    private static bool RequiresTarget(PasteMethod method)
    {
        return method is not PasteMethod.None and not PasteMethod.ClipboardOnly;
    }

    public async Task RunAsync(TargetWindow target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var stopwatch = Stopwatch.StartNew();
        _log?.Info(
            "bridge.run.start",
            $"method={_pasteMethod} target={DescribeTarget(target)}");
        try
        {
            await RunCoreAsync(target, cancellationToken);
            _log?.Info("bridge.run.completed", $"method={_pasteMethod} elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _log?.Info("bridge.run.cancelled", $"elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (Exception exception)
        {
            // 失败必须连同当时的目标窗口和阶段信息一起留证，否则日志只有一句无用的异常消息。
            _log?.Error(
                "bridge.run.failed",
                $"method={_pasteMethod} elapsedMs={stopwatch.ElapsedMilliseconds} target={DescribeTarget(target)}",
                exception);
            OperationFailed?.Invoke(this, exception.Message);
        }
    }

    public async Task RetryPasteAsync(TargetWindow target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        var stopwatch = Stopwatch.StartNew();
        _log?.Info("bridge.retry.start", $"method={_pasteMethod} target={DescribeTarget(target)}");
        try
        {
            await RetryPasteCoreAsync(target, cancellationToken);
            _log?.Info("bridge.retry.completed", $"method={_pasteMethod} elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _log?.Info("bridge.retry.cancelled", $"elapsedMs={stopwatch.ElapsedMilliseconds}");
        }
        catch (Exception exception)
        {
            _log?.Error(
                "bridge.retry.failed",
                $"method={_pasteMethod} elapsedMs={stopwatch.ElapsedMilliseconds} target={DescribeTarget(target)}",
                exception);
            OperationFailed?.Invoke(this, exception.Message);
        }
    }

    /// <summary>
    /// 目标窗口的排障描述。只记录句柄、进程名和是否 NoMachine，不记录窗口标题（标题可能含用户信息）。
    /// </summary>
    private static string DescribeTarget(TargetWindow target)
    {
        return $"hwnd=0x{target.Handle:X} pid={target.ProcessId} process={target.ProcessName} "
            + $"isNoMachine={target.IsNoMachine} class={target.ClassName ?? "unknown"}";
    }

    private async Task RetryPasteCoreAsync(TargetWindow target, CancellationToken cancellationToken)
    {
        if (!RequiresTarget(_pasteMethod))
        {
            return;
        }

        if (!_targetWindowService.IsValidTarget(target, _options))
        {
            OperationFailed?.Invoke(this, TargetUnavailableMessage);
            return;
        }

        if (!await _targetWindowService.TryActivateAsync(target, cancellationToken))
        {
            OperationFailed?.Invoke(this, "无法重新激活原来的 NoMachine 窗口，文字已保留在剪贴板中。");
            return;
        }

        await Task.Delay(_options.PasteDelay, cancellationToken);
        if (!_targetWindowService.IsValidTarget(target, _options)
            || !_targetWindowService.IsForegroundTarget(target))
        {
            OperationFailed?.Invoke(this, "目标窗口已不再是前台窗口，文字已保留在剪贴板中。");
            return;
        }

        if (!_targetWindowService.TryGetCursorScreenPosition(target, out var screenPosition))
        {
            OperationFailed?.Invoke(this, "无法计算 NoMachine 当前窗口中的原始鼠标位置，文字已保留在剪贴板中。");
            return;
        }

        await _textInjectionService.PasteAsync(
            target,
            screenPosition,
            _pasteMethod,
            _customInput,
            cancellationToken);
    }

    private async Task RunCoreAsync(TargetWindow target, CancellationToken cancellationToken)
    {
        var text = _requestText();
        if (string.IsNullOrEmpty(text))
        {
            _log?.Info("bridge.run.emptyText", "submitted text was empty, nothing to do");
            return;
        }

        // 只记录长度和字符类别，绝不记录正文。
        _log?.Info(
            "bridge.run.text",
            $"textLength={text.Length} hasLineBreak={text.Contains('\n')} pasteMethod={_pasteMethod}");

        // 用户明确提交后写入新文本，不读取或保存提交前的剪贴板。

        if (!await WriteAndVerifyClipboardAsync(
                text,
                _options.PreActivationClipboardDelay,
                cancellationToken))
        {
            _log?.Warn("bridge.run.clipboardVerifyFailed", "text could not be confirmed on the clipboard, aborting");
            OperationFailed?.Invoke(this, "无法确认本次文字已经写入 Windows 剪贴板，已取消后续操作。");
            return;
        }

        if (_pasteMethod is PasteMethod.None or PasteMethod.ClipboardOnly)
        {
            _log?.Info("bridge.run.clipboardOnly", "paste method needs no target, done");
            return;
        }

        if (!_targetWindowService.IsValidTarget(target, _options))
        {
            _log?.Warn("bridge.run.targetInvalid", DescribeTarget(target));
            OperationFailed?.Invoke(this, TargetUnavailableMessage);
            return;
        }

        var activationStopwatch = Stopwatch.StartNew();
        if (!await _targetWindowService.TryActivateAsync(target, cancellationToken))
        {
            _log?.Warn(
                "bridge.run.activationFailed",
                $"target={DescribeTarget(target)} elapsedMs={activationStopwatch.ElapsedMilliseconds}");
            OperationFailed?.Invoke(this, "无法重新激活原来的 NoMachine 窗口，文字已保留在剪贴板中。");
            return;
        }

        _log?.Info("bridge.run.activated", $"elapsedMs={activationStopwatch.ElapsedMilliseconds}");

        await Task.Delay(_options.PasteDelay, cancellationToken);
        if (!_targetWindowService.IsForegroundTarget(target))
        {
            _log?.Warn("bridge.run.lostForeground", $"after activation, target={DescribeTarget(target)}");
            OperationFailed?.Invoke(this, "目标窗口已不再是前台窗口，文字已保留在剪贴板中。");
            return;
        }

        // 激活 NoMachine 可能触发旧剪贴板回写，焦点稳定后分两次重写新内容，再留出同步时间。
        await _clipboardService.SetTextAsync(text, cancellationToken);
        await Task.Delay(_options.PostActivationClipboardRewriteDelay, cancellationToken);
        await _clipboardService.SetTextAsync(text, cancellationToken);
        await Task.Delay(_options.PostActivationClipboardSettleDelay, cancellationToken);
        _log?.Info(
            "bridge.run.clipboardRewritten",
            $"rewriteDelayMs={_options.PostActivationClipboardRewriteDelay.TotalMilliseconds} "
            + $"settleDelayMs={_options.PostActivationClipboardSettleDelay.TotalMilliseconds}");

        if (!_targetWindowService.IsValidTarget(target, _options)
            || !_targetWindowService.IsForegroundTarget(target))
        {
            _log?.Warn("bridge.run.targetLostBeforePaste", $"target={DescribeTarget(target)}");
            OperationFailed?.Invoke(this, TargetUnavailableMessage);
            return;
        }

        if (!_targetWindowService.TryGetCursorScreenPosition(target, out var screenPosition))
        {
            _log?.Warn("bridge.run.cursorPositionFailed", $"target={DescribeTarget(target)}");
            OperationFailed?.Invoke(this, "无法计算 NoMachine 当前窗口中的原始鼠标位置，文字已保留在剪贴板中。");
            return;
        }

        var pasteStopwatch = Stopwatch.StartNew();
        await _textInjectionService.PasteAsync(
            target,
            screenPosition,
            _pasteMethod,
            _customInput,
            cancellationToken);
        _log?.Info(
            "bridge.run.pasteSent",
            $"method={_pasteMethod} elapsedMs={pasteStopwatch.ElapsedMilliseconds}");
    }

    private async Task<bool> WriteAndVerifyClipboardAsync(
        string text,
        TimeSpan verificationDelay,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= ClipboardVerificationAttempts; attempt++)
        {
            await _clipboardService.SetTextAsync(text, cancellationToken);
            await Task.Delay(verificationDelay, cancellationToken);
            if (await _clipboardService.MatchesTextAsync(text, cancellationToken))
            {
                if (attempt > 1)
                {
                    _log?.Info("bridge.clipboardVerify.succeeded", $"attempt={attempt}");
                }

                return true;
            }

            _log?.Warn(
                "bridge.clipboardVerify.retry",
                $"attempt={attempt}/{ClipboardVerificationAttempts} verificationDelayMs={verificationDelay.TotalMilliseconds}");

            if (attempt < ClipboardVerificationAttempts)
            {
                await Task.Delay(ClipboardVerificationDelayMilliseconds, cancellationToken);
            }
        }

        return false;
    }
}
