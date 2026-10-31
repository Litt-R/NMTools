using NMTools.Core;

namespace NMTools.Core.Tests;

public sealed class InputBridgeCoordinatorTests
{
    [Fact]
    public async Task InvalidTargetKeepsTextButDoesNotPaste()
    {
        var targetService = new FakeTargetWindowService { IsValid = false };
        var clipboard = new FakeClipboardService();
        var injector = new FakeTextInjectionService();
        var coordinator = CreateCoordinator(targetService, clipboard, injector);

        await coordinator.RunAsync(CreateTarget());

        Assert.Equal(1, clipboard.SetTextCount);
        Assert.Equal(0, injector.PasteCount);
    }

    [Fact]
    public async Task ActivationFailureKeepsNewTextOnClipboard()
    {
        var targetService = new FakeTargetWindowService { CanActivate = false };
        var clipboard = new FakeClipboardService();
        var injector = new FakeTextInjectionService();
        var coordinator = CreateCoordinator(targetService, clipboard, injector);

        await coordinator.RunAsync(CreateTarget());

        Assert.True(clipboard.SetTextCount >= 1);
        Assert.Equal("new text", clipboard.LastText);
        Assert.Equal(0, injector.PasteCount);
    }

    [Fact]
    public async Task PasteFailureKeepsNewTextOnClipboard()
    {
        var targetService = new FakeTargetWindowService();
        var clipboard = new FakeClipboardService();
        var injector = new FakeTextInjectionService { Failure = new InvalidOperationException("paste failed") };
        var coordinator = CreateCoordinator(targetService, clipboard, injector);

        await coordinator.RunAsync(CreateTarget());

        Assert.Equal(3, clipboard.SetTextCount);
        Assert.Equal("new text", clipboard.LastText);
        Assert.Equal(1, injector.PasteCount);
    }

    [Fact]
    public async Task ClipboardOnlyActionDoesNotActivateOrPaste()
    {
        var targetService = new FakeTargetWindowService();
        var clipboard = new FakeClipboardService();
        var injector = new FakeTextInjectionService();
        var coordinator = new InputBridgeCoordinator(
            targetService,
            clipboard,
            injector,
            new InputBridgeOptions
            {
                PreActivationClipboardDelay = TimeSpan.Zero,
                PostActivationClipboardRewriteDelay = TimeSpan.Zero,
                PostActivationClipboardSettleDelay = TimeSpan.Zero,
                PasteDelay = TimeSpan.Zero,
                PasteBindings = new PasteShortcutBindings
                {
                    Custom = new PasteShortcutBinding(null, PasteMethod.ClipboardOnly),
                },
            },
            () => "new text",
            PasteShortcutAction.Custom);

        await coordinator.RunAsync(CreateTarget());

        Assert.Equal(0, targetService.ActivateCount);
        Assert.Equal(1, clipboard.SetTextCount);
        Assert.Equal(0, injector.PasteCount);
    }

    [Fact]
    public async Task CustomSlotPassesConfiguredInputToInjector()
    {
        var targetService = new FakeTargetWindowService();
        var clipboard = new FakeClipboardService();
        var injector = new FakeTextInjectionService();
        var input = new RemoteInput(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x50, RemoteMouseButton.None);
        var bindings = new PasteShortcutBindings()
            .SetShortcut(PasteShortcutAction.Custom, new PasteShortcut(HotkeyModifiers.Control, 0x4B))
            .SetCustomInput(input);
        var coordinator = new InputBridgeCoordinator(
            targetService,
            clipboard,
            injector,
            new InputBridgeOptions
            {
                PreActivationClipboardDelay = TimeSpan.Zero,
                PostActivationClipboardRewriteDelay = TimeSpan.Zero,
                PostActivationClipboardSettleDelay = TimeSpan.Zero,
                PasteDelay = TimeSpan.Zero,
                PasteBindings = bindings,
            },
            () => "new text",
            PasteShortcutAction.Custom);

        await coordinator.RunAsync(CreateTarget());

        Assert.Equal(1, injector.PasteCount);
        Assert.Equal(PasteMethod.CustomInput, injector.LastMethod);
        Assert.Equal(input, injector.LastCustomInput);
    }

    [Fact]
    public async Task MiddleClickMethodIsPassedToInjector()
    {
        var targetService = new FakeTargetWindowService();
        var clipboard = new FakeClipboardService();
        var injector = new FakeTextInjectionService();
        var coordinator = new InputBridgeCoordinator(
            targetService,
            clipboard,
            injector,
            new InputBridgeOptions
            {
                PreActivationClipboardDelay = TimeSpan.Zero,
                PostActivationClipboardRewriteDelay = TimeSpan.Zero,
                PostActivationClipboardSettleDelay = TimeSpan.Zero,
                PasteDelay = TimeSpan.Zero,
                PasteBindings = new PasteShortcutBindings
                {
                    Enter = new PasteShortcutBinding(new PasteShortcut(HotkeyModifiers.None, 0x0D), PasteMethod.MiddleClick),
                },
            },
            () => "new text");

        await coordinator.RunAsync(CreateTarget());

        Assert.Equal(PasteMethod.MiddleClick, injector.LastMethod);
    }

    [Fact]
    public async Task ClipboardVerificationFailureDoesNotSendPaste()
    {
        var targetService = new FakeTargetWindowService();
        var clipboard = new FakeClipboardService();
        clipboard.MatchResults.Enqueue(false);
        clipboard.MatchResults.Enqueue(false);
        clipboard.MatchResults.Enqueue(false);
        clipboard.MatchResults.Enqueue(false);
        clipboard.MatchResults.Enqueue(false);
        clipboard.MatchResults.Enqueue(false);
        var injector = new FakeTextInjectionService();
        var coordinator = CreateCoordinator(targetService, clipboard, injector);

        await coordinator.RunAsync(CreateTarget());

        Assert.Equal(3, clipboard.SetTextCount);
        Assert.Equal(0, injector.PasteCount);
    }

    [Fact]
    public async Task InitialClipboardVerificationRetriesBeforeSendingPaste()
    {
        var targetService = new FakeTargetWindowService();
        var clipboard = new FakeClipboardService();
        clipboard.MatchResults.Enqueue(false);
        clipboard.MatchResults.Enqueue(true);
        var injector = new FakeTextInjectionService();
        var coordinator = CreateCoordinator(targetService, clipboard, injector);

        await coordinator.RunAsync(CreateTarget());

        Assert.Equal(4, clipboard.SetTextCount);
        Assert.Equal(2, clipboard.MatchTextCount);
        Assert.Equal(1, injector.PasteCount);
    }

    [Fact]
    public void DefaultOptionsUseConfiguredClipboardDelays()
    {
        var options = new InputBridgeOptions();

        Assert.Equal(TimeSpan.FromMilliseconds(50), options.PasteDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(50), options.PreActivationClipboardDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(200), options.PostActivationClipboardRewriteDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(400), options.PostActivationClipboardSettleDelay);
    }

    [Fact]
    public async Task DefaultConfigurationKeepsNewTextAfterSuccessfulPaste()
    {
        var targetService = new FakeTargetWindowService();
        var clipboard = new FakeClipboardService();
        var injector = new FakeTextInjectionService();
        var coordinator = CreateCoordinator(targetService, clipboard, injector);

        await coordinator.RunAsync(CreateTarget());

        Assert.Equal(3, clipboard.SetTextCount);
        Assert.Equal(1, clipboard.MatchTextCount);
        Assert.Equal(1, injector.PasteCount);
    }

    [Fact]
    public async Task RetryPasteAsync_SkipsClipboardWritesAndDirectlyPastesToTarget()
    {
        var targetService = new FakeTargetWindowService();
        var clipboard = new FakeClipboardService();
        var injector = new FakeTextInjectionService();
        var coordinator = new InputBridgeCoordinator(
            targetService,
            clipboard,
            injector,
            new InputBridgeOptions
            {
                PasteDelay = TimeSpan.Zero,
                PasteBindings = new PasteShortcutBindings
                {
                    OpenCode = new PasteShortcutBinding(
                        new PasteShortcut(HotkeyModifiers.Control, 0x4F),
                        PasteMethod.ShiftMiddleClick),
                },
            },
            () => null,
            PasteShortcutAction.OpenCode);

        await coordinator.RetryPasteAsync(CreateTarget());

        Assert.Equal(0, clipboard.SetTextCount);
        Assert.Equal(1, targetService.ActivateCount);
        Assert.Equal(1, injector.PasteCount);
        Assert.Equal(PasteMethod.ShiftMiddleClick, injector.LastMethod);
    }

    [Fact]
    public async Task RetryPasteAsync_ClipboardOnlyActionDoesNotActivateOrPaste()
    {
        var targetService = new FakeTargetWindowService();
        var clipboard = new FakeClipboardService();
        var injector = new FakeTextInjectionService();
        var coordinator = new InputBridgeCoordinator(
            targetService,
            clipboard,
            injector,
            new InputBridgeOptions
            {
                PasteDelay = TimeSpan.Zero,
                PasteBindings = new PasteShortcutBindings
                {
                    Enter = new PasteShortcutBinding(
                        new PasteShortcut(HotkeyModifiers.None, 0x0D),
                        PasteMethod.ClipboardOnly),
                },
            },
            () => null,
            PasteShortcutAction.Enter);

        await coordinator.RetryPasteAsync(CreateTarget());

        Assert.Equal(0, targetService.ActivateCount);
        Assert.Equal(0, clipboard.SetTextCount);
        Assert.Equal(0, injector.PasteCount);
    }

    private static InputBridgeCoordinator CreateCoordinator(
        FakeTargetWindowService targetService,
        FakeClipboardService clipboard,
        FakeTextInjectionService injector)
    {
        return new InputBridgeCoordinator(
            targetService,
            clipboard,
            injector,
            new InputBridgeOptions
            {
                PreActivationClipboardDelay = TimeSpan.Zero,
                PostActivationClipboardRewriteDelay = TimeSpan.Zero,
                PostActivationClipboardSettleDelay = TimeSpan.Zero,
                PasteDelay = TimeSpan.Zero,
            },
            () => "new text");
    }

    private static TargetWindow CreateTarget()
    {
        return new TargetWindow(
            new nint(1),
            100,
            200,
            "nxplayer",
            "NoMachine",
            true,
            new WindowRelativePosition(10, 20),
            new ScreenPosition(100, 200));
    }

    private sealed class FakeTargetWindowService : ITargetWindowService
    {
        public bool IsValid { get; init; } = true;

        public bool CanActivate { get; init; } = true;

        public int ActivateCount { get; private set; }

        public TargetWindow? CaptureForegroundWindow() => CreateTarget();

        public bool IsValidTarget(TargetWindow target, InputBridgeOptions options) => IsValid;

        public bool IsForegroundTarget(TargetWindow target) => IsValid && CanActivate;

        public Task<bool> TryActivateAsync(TargetWindow target, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActivateCount++;
            return Task.FromResult(IsValid && CanActivate);
        }

        public bool TryGetCursorScreenPosition(TargetWindow target, out ScreenPosition screenPosition)
        {
            screenPosition = new ScreenPosition(100, 200);
            return IsValid && CanActivate;
        }
    }

    private sealed class FakeClipboardService : IClipboardService
    {
        public int SetTextCount { get; private set; }

        public int MatchTextCount { get; private set; }

        public string? LastText { get; private set; }

        public TimeSpan SetTextDelay { get; init; }

        public Queue<bool> MatchResults { get; } = new();

        public async Task SetTextAsync(string text, CancellationToken cancellationToken = default)
        {
            await Task.Delay(SetTextDelay, cancellationToken);
            SetTextCount++;
            LastText = text;
        }

        public Task<bool> MatchesTextAsync(string expectedText, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MatchTextCount++;
            return Task.FromResult(MatchResults.Count == 0 || MatchResults.Dequeue());
        }

    }

    private sealed class FakeTextInjectionService : ITextInjectionService
    {
        public int PasteCount { get; private set; }

        public Exception? Failure { get; init; }

        public PasteMethod? LastMethod { get; private set; }

        public RemoteInput? LastCustomInput { get; private set; }

        public Task PasteAsync(
            TargetWindow target,
            ScreenPosition screenPosition,
            PasteMethod method,
            RemoteInput? customInput = null,
            CancellationToken cancellationToken = default)
        {
            PasteCount++;
            LastMethod = method;
            LastCustomInput = customInput;
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.CompletedTask;
        }
    }

}
