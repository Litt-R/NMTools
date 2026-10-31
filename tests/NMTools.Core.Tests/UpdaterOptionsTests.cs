using NMTools.Updater;

namespace NMTools.Core.Tests;

/// <summary>
/// 更新器命令行参数解析。
///
/// 这里有一个真实踩过的坑：AppContext.BaseDirectory 以反斜杠结尾，
/// 拼进 "--app-dir \"D:\...\program\"" 时反斜杠会转义右引号，整个命令行被解析错，
/// 更新器报"更新参数无效"。解析端必须容忍并规范化尾部反斜杠。
/// </summary>
public sealed class UpdaterOptionsTests
{
    [Fact]
    public void Parse_ReadsAllArguments()
    {
        var options = UpdaterOptions.Parse(
        [
            "--pid", "1234",
            "--app-dir", @"D:\NMTools\program",
            "--staged-dir", @"D:\NMTools\program\.update-staging\content",
        ]);

        Assert.NotNull(options);
        Assert.Equal(1234, options!.ProcessId);
        Assert.Equal(@"D:\NMTools\program", options.AppDirectory);
        Assert.Equal(@"D:\NMTools\program\.update-staging\content", options.StagedDirectory);
    }

    [Fact]
    public void Parse_StripsTrailingSeparatorFromAppDirectory()
    {
        // AppContext.BaseDirectory 就长这样，带尾部反斜杠。
        var options = UpdaterOptions.Parse(
        [
            "--pid", "1234",
            "--app-dir", @"D:\NMTools\program\",
            "--staged-dir", @"D:\NMTools\program\.update-staging\content",
        ]);

        Assert.NotNull(options);
        Assert.Equal(@"D:\NMTools\program", options!.AppDirectory);
    }

    [Fact]
    public void Parse_StripsTrailingSeparatorFromStagedDirectory()
    {
        var options = UpdaterOptions.Parse(
        [
            "--pid", "1234",
            "--app-dir", @"D:\NMTools\program",
            "--staged-dir", @"D:\NMTools\program\.update-staging\content\",
        ]);

        Assert.NotNull(options);
        Assert.Equal(@"D:\NMTools\program\.update-staging\content", options!.StagedDirectory);
    }

    [Fact]
    public void Parse_DerivesStagingRootFromStagedDirectory()
    {
        var options = UpdaterOptions.Parse(
        [
            "--pid", "1234",
            "--app-dir", @"D:\NMTools\program",
            "--staged-dir", @"D:\NMTools\program\.update-staging\content",
        ]);

        Assert.NotNull(options);
        Assert.Equal(@"D:\NMTools\program\.update-staging", options!.StagingRoot);
    }

    public static TheoryData<string[]> InvalidArgumentSets => new()
    {
        new[] { "--pid", "0", "--app-dir", "a", "--staged-dir", "b" },
        new[] { "--pid", "-5", "--app-dir", "a", "--staged-dir", "b" },
        new[] { "--pid", "abc", "--app-dir", "a", "--staged-dir", "b" },
        new[] { "--pid", "1", "--app-dir", "a" },
        new[] { "--pid", "1", "--staged-dir", "b" },
        new[] { "--app-dir", "a", "--staged-dir", "b" },
        new[] { "--pid", "1", "--app-dir", "a", "--staged-dir", "b", "--unknown", "x" },
        new[] { "--pid", "1", "--app-dir" },
        Array.Empty<string>(),
    };

    [Theory]
    [MemberData(nameof(InvalidArgumentSets))]
    public void Parse_RejectsInvalidArguments(string[] args)
    {
        Assert.Null(UpdaterOptions.Parse(args));
    }

    [Theory]
    [InlineData("--pid", "")]
    [InlineData("--app-dir", "")]
    [InlineData("--staged-dir", "  ")]
    public void Parse_RejectsBlankPathArguments(string flag, string value)
    {
        var args = new List<string> { "--pid", "1", "--app-dir", @"C:\a", "--staged-dir", @"C:\a\b" };

        // 替换目标参数为空值。
        var index = args.IndexOf(flag);
        args[index + 1] = value;

        Assert.Null(UpdaterOptions.Parse(args.ToArray()));
    }
}
