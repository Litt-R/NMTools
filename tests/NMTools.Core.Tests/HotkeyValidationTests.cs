using System.Windows.Input;
using NMTools.App.Infrastructure;
using NMTools.Core;

namespace NMTools.Core.Tests;

public sealed class HotkeyValidationTests
{
    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, true)]
    [InlineData(0xFFu, true)]
    [InlineData(0x100u, false)]
    public void IsVirtualKeyInRange_AcceptsOnlyOneThroughFF(uint virtualKey, bool expected)
    {
        Assert.Equal(expected, HotkeyValidation.IsVirtualKeyInRange(virtualKey));
    }

    [Theory]
    [InlineData(HotkeyModifiers.None, true)]
    [InlineData(HotkeyModifiers.All, true)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt, true)]
    [InlineData((HotkeyModifiers)16, false)]
    [InlineData((HotkeyModifiers)(8 | 16), false)]
    public void AreModifiersDefined_RejectsUnknownBits(HotkeyModifiers modifiers, bool expected)
    {
        Assert.Equal(expected, HotkeyValidation.AreModifiersDefined(modifiers));
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x20u, true)]
    [InlineData(HotkeyModifiers.Windows, 0x41u, true)]
    [InlineData(HotkeyModifiers.None, 0x20u, false)]
    [InlineData(HotkeyModifiers.Control, 0u, false)]
    [InlineData(HotkeyModifiers.Control, 0x100u, false)]
    [InlineData((HotkeyModifiers)16, 0x20u, false)]
    public void IsValidGlobalHotkey_RequiresModifierAndValidKey(HotkeyModifiers modifiers, uint virtualKey, bool expected)
    {
        Assert.Equal(expected, HotkeyValidation.IsValidGlobalHotkey(modifiers, virtualKey));
    }

    [Theory]
    [InlineData(HotkeyModifiers.None, 0u, true)]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x56u, true)]
    [InlineData(HotkeyModifiers.None, 0x56u, false)]
    [InlineData(HotkeyModifiers.Control, 0u, false)]
    public void IsValidOptionalGlobalHotkey_AllowsEmptyOrValidGlobalHotkey(
        HotkeyModifiers modifiers,
        uint virtualKey,
        bool expected)
    {
        Assert.Equal(expected, HotkeyValidation.IsValidOptionalGlobalHotkey(modifiers, virtualKey));
    }

    [Theory]
    [InlineData(ModifierKeys.None, HotkeyModifiers.None)]
    [InlineData(ModifierKeys.Control, HotkeyModifiers.Control)]
    [InlineData(ModifierKeys.Alt, HotkeyModifiers.Alt)]
    [InlineData(ModifierKeys.Shift, HotkeyModifiers.Shift)]
    [InlineData(ModifierKeys.Windows, HotkeyModifiers.Windows)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift, HotkeyModifiers.Control | HotkeyModifiers.Shift)]
    [InlineData(
        ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows,
        HotkeyModifiers.All)]
    public void ToHotkeyModifiers_MapsEachWpfModifier(ModifierKeys input, HotkeyModifiers expected)
    {
        Assert.Equal(expected, input.ToHotkeyModifiers());
    }
}
