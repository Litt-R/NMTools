namespace NMTools.Core;

public interface IHotkeyService : IDisposable
{
    event EventHandler? HotkeyPressed;

    event EventHandler? RetryHotkeyPressed;

    bool Register(InputBridgeOptions options);

    void Unregister();
}

public interface ITargetWindowService
{
    TargetWindow? CaptureForegroundWindow();

    bool IsValidTarget(TargetWindow target, InputBridgeOptions options);

    bool IsForegroundTarget(TargetWindow target);

    Task<bool> TryActivateAsync(TargetWindow target, CancellationToken cancellationToken = default);

    bool TryGetCursorScreenPosition(TargetWindow target, out ScreenPosition screenPosition);
}

public interface IClipboardService
{
    Task SetTextAsync(string text, CancellationToken cancellationToken = default);

    Task<bool> MatchesTextAsync(string expectedText, CancellationToken cancellationToken = default);
}

public interface ITextInjectionService
{
    Task PasteAsync(
        TargetWindow target,
        ScreenPosition screenPosition,
        PasteMethod method,
        RemoteInput? customInput = null,
        CancellationToken cancellationToken = default);
}
