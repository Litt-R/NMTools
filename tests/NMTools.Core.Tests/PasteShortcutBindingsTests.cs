using NMTools.Core;

namespace NMTools.Core.Tests;

public sealed class PasteShortcutBindingsTests
{
    [Fact]
    public void DefaultBindingsMatchConfiguredLayout()
    {
        var bindings = new PasteShortcutBindings();

        Assert.Equal(new PasteShortcut(HotkeyModifiers.None, 0x0D), bindings.Enter.Shortcut);
        Assert.Equal(PasteMethod.ControlV, bindings.Enter.Method);

        Assert.Equal(new PasteShortcut(HotkeyModifiers.Control, 0x0D), bindings.ControlShiftV.Shortcut);
        Assert.Equal(PasteMethod.ControlShiftV, bindings.ControlShiftV.Method);

        Assert.Equal(new PasteShortcut(HotkeyModifiers.Control, 0x4F), bindings.OpenCode.Shortcut);
        Assert.Equal(PasteMethod.ShiftMiddleClick, bindings.OpenCode.Method);

        Assert.Equal(new PasteShortcut(HotkeyModifiers.Control, 0x59), bindings.Emacs.Shortcut);
        Assert.Equal(PasteMethod.EmacsYank, bindings.Emacs.Method);

        Assert.Equal(new PasteShortcut(HotkeyModifiers.Control, 0x47), bindings.Gvim.Shortcut);
        Assert.Equal(PasteMethod.GvimRegisterPaste, bindings.Gvim.Method);

        Assert.Equal(new PasteShortcut(HotkeyModifiers.Control, 0x54), bindings.GenericTerminal.Shortcut);
        Assert.Equal(PasteMethod.MiddleClick, bindings.GenericTerminal.Method);

        Assert.Null(bindings.Custom.Shortcut);
        Assert.Equal(PasteMethod.None, bindings.Custom.Method);
        Assert.True(bindings.IsValid());
    }

    [Theory]
    [InlineData("Enter", PasteShortcutAction.Enter)]
    [InlineData("ControlShiftV", PasteShortcutAction.ControlShiftV)]
    [InlineData("OpenCode", PasteShortcutAction.OpenCode)]
    [InlineData("Emacs", PasteShortcutAction.Emacs)]
    [InlineData("Gvim", PasteShortcutAction.Gvim)]
    [InlineData("GenericTerminal", PasteShortcutAction.GenericTerminal)]
    [InlineData("Custom", PasteShortcutAction.Custom)]
    public void SlotTagParsesToAction(string tag, PasteShortcutAction expected)
    {
        Assert.Equal(expected, PasteShortcutActionParser.TryParse(tag));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("CustomInput")]
    [InlineData("enter")]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("Enter,Emacs")]
    public void InvalidSlotTagParsesToNull(string? tag)
    {
        Assert.Null(PasteShortcutActionParser.TryParse(tag));
    }

    [Fact]
    public void EveryPresetSlotIsHandledByBindingsApi()
    {
        // 新增 PasteShortcutAction 成员时，如果忘了在 GetBinding / SetMethod / SetShortcut 里补分支，这里会失败。
        foreach (var action in Enum.GetValues<PasteShortcutAction>())
        {
            var bindings = new PasteShortcutBindings();
            var changed = bindings.SetMethod(action, PasteMethod.ClipboardOnly);

            if (action == PasteShortcutAction.Custom)
            {
                Assert.Equal(bindings, changed);
                continue;
            }

            Assert.Equal(PasteMethod.ClipboardOnly, changed.GetBinding(action).Method);
            Assert.NotEqual(bindings, changed);
        }
    }

    [Fact]
    public void SetMethodOnlyChangesTheTargetSlot()
    {
        var bindings = new PasteShortcutBindings();

        var changed = bindings.SetMethod(PasteShortcutAction.Emacs, PasteMethod.ControlV);

        Assert.Equal(PasteMethod.ControlV, changed.Emacs.Method);
        Assert.Equal(bindings.Emacs.Shortcut, changed.Emacs.Shortcut);
        Assert.Equal(bindings.Enter, changed.Enter);
        Assert.Equal(bindings.Gvim, changed.Gvim);
        Assert.Equal(bindings.Custom, changed.Custom);
    }

    [Fact]
    public void GenericTerminalSlotResolvesAndCanBeCleared()
    {
        var bindings = new PasteShortcutBindings();
        var shortcut = new PasteShortcut(HotkeyModifiers.Control, 0x54);

        Assert.Equal(PasteShortcutAction.GenericTerminal, bindings.Resolve(shortcut));
        Assert.Equal(PasteMethod.MiddleClick, bindings.GetBinding(PasteShortcutAction.GenericTerminal).Method);

        var cleared = bindings.ClearShortcut(PasteShortcutAction.GenericTerminal);
        Assert.Null(cleared.GenericTerminal.Shortcut);
        Assert.Equal(PasteMethod.None, cleared.GenericTerminal.Method);
        Assert.True(cleared.IsValid());
    }

    [Fact]
    public void GenericTerminalShortcutConflictingWithCustomIsInvalid()
    {
        var bindings = new PasteShortcutBindings()
            .SetShortcut(PasteShortcutAction.Custom, new PasteShortcut(HotkeyModifiers.Control, 0x54))
            .SetCustomInput(new RemoteInput(HotkeyModifiers.Control, 0x50, RemoteMouseButton.None));

        Assert.False(bindings.IsValid());
    }

    private static readonly PasteShortcut CustomShortcut = new(HotkeyModifiers.Control, 0x4B);

    [Fact]
    public void CustomSlotIsInactiveUntilBothSidesAreSet()
    {
        var onlyShortcut = new PasteShortcutBindings().SetShortcut(PasteShortcutAction.Custom, CustomShortcut);
        Assert.Equal(PasteMethod.None, onlyShortcut.Custom.Method);
        Assert.True(onlyShortcut.IsValid());
        Assert.Null(onlyShortcut.Resolve(CustomShortcut));

        var onlyInput = new PasteShortcutBindings().SetCustomInput(new RemoteInput(HotkeyModifiers.Control, 0x50, RemoteMouseButton.None));
        Assert.Equal(PasteMethod.None, onlyInput.Custom.Method);
        Assert.True(onlyInput.IsValid());

        var both = onlyShortcut.SetCustomInput(new RemoteInput(HotkeyModifiers.Control, 0x50, RemoteMouseButton.None));
        Assert.Equal(PasteMethod.CustomInput, both.Custom.Method);
        Assert.True(both.IsValid());
        Assert.Equal(PasteShortcutAction.Custom, both.Resolve(CustomShortcut));
    }

    [Fact]
    public void ClearingCustomInputDeactivatesSlotButKeepsShortcut()
    {
        var bindings = new PasteShortcutBindings()
            .SetShortcut(PasteShortcutAction.Custom, CustomShortcut)
            .SetCustomInput(new RemoteInput(HotkeyModifiers.None, 0, RemoteMouseButton.Middle))
            .SetCustomInput(null);

        Assert.Equal(CustomShortcut, bindings.Custom.Shortcut);
        Assert.Equal(PasteMethod.None, bindings.Custom.Method);
        Assert.True(bindings.IsValid());
    }

    [Fact]
    public void ClearShortcutOnCustomSlotRemovesShortcutAndKeepsInput()
    {
        var input = new RemoteInput(HotkeyModifiers.Shift, 0, RemoteMouseButton.Middle);
        var bindings = new PasteShortcutBindings()
            .SetShortcut(PasteShortcutAction.Custom, CustomShortcut)
            .SetCustomInput(input)
            .ClearShortcut(PasteShortcutAction.Custom);

        Assert.Null(bindings.Custom.Shortcut);
        Assert.Equal(input, bindings.Custom.CustomInput);
        Assert.Equal(PasteMethod.None, bindings.Custom.Method);
        Assert.True(bindings.IsValid());
    }

    [Theory]
    [InlineData(HotkeyModifiers.Windows, 0x50u, RemoteMouseButton.None, false)]
    [InlineData(HotkeyModifiers.None, 0x50u, RemoteMouseButton.None, true)]
    [InlineData(HotkeyModifiers.Control, 0x11u, RemoteMouseButton.None, false)]
    [InlineData(HotkeyModifiers.None, 0u, RemoteMouseButton.None, false)]
    [InlineData(HotkeyModifiers.Shift, 0u, RemoteMouseButton.Middle, true)]
    [InlineData(HotkeyModifiers.None, 0u, RemoteMouseButton.Right, true)]
    [InlineData(HotkeyModifiers.Shift, 0x50u, RemoteMouseButton.Middle, false)]
    public void RemoteInputValidationRejectsUnsupportedCombinations(
        HotkeyModifiers modifiers,
        uint virtualKey,
        RemoteMouseButton mouseButton,
        bool expected)
    {
        Assert.Equal(expected, new RemoteInput(modifiers, virtualKey, mouseButton).IsValid());
    }

    [Fact]
    public void InconsistentCustomMethodIsInvalid()
    {
        var bindings = new PasteShortcutBindings
        {
            Custom = new PasteShortcutBinding(CustomShortcut, PasteMethod.CustomInput),
        };

        Assert.False(bindings.IsValid());
    }

    [Theory]
    [InlineData(PasteMethod.ControlV, HotkeyModifiers.Control, 0x56u, RemoteMouseButton.None)]
    [InlineData(PasteMethod.ControlShiftV, HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x56u, RemoteMouseButton.None)]
    [InlineData(PasteMethod.EmacsYank, HotkeyModifiers.Control, 0x59u, RemoteMouseButton.None)]
    [InlineData(PasteMethod.ShiftMiddleClick, HotkeyModifiers.Shift, 0u, RemoteMouseButton.Middle)]
    [InlineData(PasteMethod.MiddleClick, HotkeyModifiers.None, 0u, RemoteMouseButton.Middle)]
    public void LegacyCustomMethodIsMigratedToEquivalentInput(
        PasteMethod legacyMethod,
        HotkeyModifiers modifiers,
        uint virtualKey,
        RemoteMouseButton mouseButton)
    {
        var legacy = new PasteShortcutBindings
        {
            Custom = new PasteShortcutBinding(CustomShortcut, legacyMethod),
        };

        var migrated = legacy.MigrateLegacyCustom();

        Assert.Equal(PasteMethod.CustomInput, migrated.Custom.Method);
        Assert.Equal(new RemoteInput(modifiers, virtualKey, mouseButton), migrated.Custom.CustomInput);
        Assert.Equal(CustomShortcut, migrated.Custom.Shortcut);
        Assert.True(migrated.IsValid());
    }

    [Theory]
    [InlineData(PasteMethod.GvimRegisterPaste)]
    [InlineData(PasteMethod.ClipboardOnly)]
    public void LegacyCustomMethodWithoutKeyEquivalentIsCleared(PasteMethod legacyMethod)
    {
        var legacy = new PasteShortcutBindings
        {
            Custom = new PasteShortcutBinding(CustomShortcut, legacyMethod),
        };

        var migrated = legacy.MigrateLegacyCustom();

        Assert.Null(migrated.Custom.Shortcut);
        Assert.Null(migrated.Custom.CustomInput);
        Assert.Equal(PasteMethod.None, migrated.Custom.Method);
        Assert.True(migrated.IsValid());
    }

    [Fact]
    public void LegacyCustomWithoutShortcutIsMigratedToInactiveSlot()
    {
        var legacy = new PasteShortcutBindings
        {
            Custom = new PasteShortcutBinding(null, PasteMethod.ControlV),
        };

        var migrated = legacy.MigrateLegacyCustom();

        Assert.Equal(PasteMethod.None, migrated.Custom.Method);
        Assert.True(migrated.IsValid());
    }
}
