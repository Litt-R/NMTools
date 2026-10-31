using Microsoft.Win32;
using NMTools.App.Infrastructure;

namespace NMTools.Core.Tests;

public sealed class AutoStartServiceTests : IDisposable
{
    private readonly string _rootKey = $@"Software\NMTools.Tests\{Guid.NewGuid():N}";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NMTools.AutoStartTests", Guid.NewGuid().ToString("N"));
    private readonly string _startupDirectory;
    private readonly string _launcherPath;

    public AutoStartServiceTests()
    {
        Directory.CreateDirectory(_directory);
        _startupDirectory = Path.Combine(_directory, "startup");
        _launcherPath = Path.Combine(_directory, AutoStartService.LauncherFileName);
        File.WriteAllText(_launcherPath, "' test launcher");
    }

    [Fact]
    public void ArgumentsRunLauncherWithoutConsoleWindowAndStartInTray()
    {
        var arguments = AutoStartService.BuildArguments(@"C:\Tools\NMTools\Start-NMTools.vbs");

        Assert.Equal("//B //Nologo \"C:\\Tools\\NMTools\\Start-NMTools.vbs\" --tray", arguments);
    }

    [Fact]
    public void IsDisabledByDefault()
    {
        var service = CreateService(_launcherPath);

        Assert.True(service.IsAvailable);
        Assert.False(service.IsEnabled());
    }

    [Fact]
    public void EnableCreatesNamedShortcutAndDisableRemovesIt()
    {
        var service = CreateService(_launcherPath);

        service.SetEnabled(true);
        Assert.True(service.IsEnabled());
        Assert.Equal(AutoStartService.ShortcutFileName, Path.GetFileName(service.ShortcutPath));
        var shortcut = AutoStartService.ReadShortcut(service.ShortcutPath);
        Assert.NotNull(shortcut);
        Assert.Equal(AutoStartService.WscriptPath, shortcut!.Value.Target, ignoreCase: true);
        Assert.Equal(AutoStartService.BuildArguments(_launcherPath), shortcut.Value.Arguments);

        service.SetEnabled(false);
        Assert.False(service.IsEnabled());
        Assert.False(File.Exists(service.ShortcutPath));
    }

    [Fact]
    public void DisablingWhenNeverEnabledDoesNotThrow()
    {
        var service = CreateService(_launcherPath);

        service.SetEnabled(false);

        Assert.False(service.IsEnabled());
    }

    [Fact]
    public void EnableWithoutLauncherThrowsAndWritesNothing()
    {
        var service = CreateService(Path.Combine(_directory, "missing", AutoStartService.LauncherFileName));

        Assert.False(service.IsAvailable);
        Assert.Throws<InvalidOperationException>(() => service.SetEnabled(true));
        Assert.False(File.Exists(service.ShortcutPath));
    }

    [Fact]
    public void RefreshDoesNothingWhenDisabled()
    {
        var service = CreateService(_launcherPath);

        Assert.False(service.RefreshPathIfEnabled());
        Assert.False(File.Exists(service.ShortcutPath));
    }

    [Fact]
    public void RefreshRewritesShortcutPointingAtOldLauncher()
    {
        var oldLauncher = Path.Combine(_directory, "old", AutoStartService.LauncherFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(oldLauncher)!);
        File.WriteAllText(oldLauncher, "' old launcher");
        CreateService(oldLauncher).SetEnabled(true);

        var service = CreateService(_launcherPath);

        Assert.True(service.RefreshPathIfEnabled());
        Assert.Equal(
            AutoStartService.BuildArguments(_launcherPath),
            AutoStartService.ReadShortcut(service.ShortcutPath)!.Value.Arguments);
        Assert.False(service.RefreshPathIfEnabled());
    }

    [Fact]
    public void LegacyRunEntryIsRecognizedAndMigratedToShortcut()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
        {
            key.SetValue(AutoStartService.ValueName, "wscript.exe //B //Nologo \"D:\\Old\\Start-NMTools.vbs\"");
        }

        var service = CreateService(_launcherPath);
        Assert.True(service.IsEnabled());

        Assert.True(service.RefreshPathIfEnabled());
        Assert.True(File.Exists(service.ShortcutPath));
        Assert.Null(ReadLegacyValue());
        Assert.True(service.IsEnabled());
    }

    [Fact]
    public void DisableAlsoRemovesLegacyRunEntry()
    {
        using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
        {
            key.SetValue(AutoStartService.ValueName, "legacy");
        }

        var service = CreateService(_launcherPath);
        service.SetEnabled(false);

        Assert.False(service.IsEnabled());
        Assert.Null(ReadLegacyValue());
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_rootKey, throwOnMissingSubKey: false);
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string RunKeyPath => $@"{_rootKey}\Run";

    private AutoStartService CreateService(string launcherPath) => new(_startupDirectory, RunKeyPath, launcherPath);

    private string? ReadLegacyValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(AutoStartService.ValueName) as string;
    }
}
