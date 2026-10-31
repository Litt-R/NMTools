using System.IO.Compression;
using NMTools.App.Services;

namespace NMTools.Core.Tests;

/// <summary>
/// 解压器的路径安全与实际包结构兼容性。
///
/// 这里的用例直接对应过一次真实故障：发布脚本用 Compress-Archive 打包时会在 zip 里
/// 套一层版本目录，而解压后只按 "program/NMTools.App.exe" 查找，导致更新失败。
/// </summary>
public sealed class UpdatePackageExtractorTests : IDisposable
{
    private readonly string _root;

    public UpdatePackageExtractorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "NMTools_Extract_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void Extract_AcceptsArchiveWrappedInVersionDirectory()
    {
        // 这是发布脚本产出的真实形状：NMTools-v0.7.0-win-x64/program/NMTools.App.exe
        var archive = CreateArchive(("NMTools-v0.7.0-win-x64/program/NMTools.App.exe", "exe"));

        var result = new UpdatePackageExtractor().Extract(archive, Destination());

        Assert.True(result.IsSuccess, $"解压失败：{result.FailureReason}");
        Assert.Equal("NMTools-v0.7.0-win-x64", Path.GetFileName(result.ExtractedDirectory));
        Assert.True(File.Exists(Path.Combine(result.ExtractedDirectory!, "program", "NMTools.App.exe")));
    }

    [Fact]
    public void Extract_AcceptsArchiveWithoutWrapperDirectory()
    {
        // 平铺形状也要支持，避免打包方式变化就更新不了。
        var archive = CreateArchive(("program/NMTools.App.exe", "exe"));

        var result = new UpdatePackageExtractor().Extract(archive, Destination());

        Assert.True(result.IsSuccess, $"解压失败：{result.FailureReason}");
        Assert.True(File.Exists(Path.Combine(result.ExtractedDirectory!, "program", "NMTools.App.exe")));
    }

    [Fact]
    public void Extract_RejectsArchiveWithoutAppExecutable()
    {
        var archive = CreateArchive(("NMTools-v0.7.0-win-x64/README.txt", "readme"));

        var result = new UpdatePackageExtractor().Extract(archive, Destination());

        Assert.False(result.IsSuccess);
        Assert.Equal(
            NMTools.Core.Updates.UpdateExtractFailureReason.MissingRequiredFile,
            result.FailureReason);
    }

    [Fact]
    public void Extract_RejectsPathTraversal()
    {
        // 恶意条目不能写到解压目录之外。
        var archive = CreateArchive(("../escaped.txt", "evil"));

        var result = new UpdatePackageExtractor().Extract(archive, Destination());

        Assert.False(result.IsSuccess);
        Assert.Equal(NMTools.Core.Updates.UpdateExtractFailureReason.UnsafeEntry, result.FailureReason);
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")), "文件逃逸出了目标目录。");
    }

    [Fact]
    public void Extract_RejectsAbsolutePath()
    {
        var archive = CreateArchive(("C:/Windows/System32/evil.dll", "evil"));

        var result = new UpdatePackageExtractor().Extract(archive, Destination());

        Assert.False(result.IsSuccess);
        Assert.Equal(NMTools.Core.Updates.UpdateExtractFailureReason.UnsafeEntry, result.FailureReason);
    }

    [Fact]
    public void Extract_FailsOnCorruptArchive()
    {
        var path = Path.Combine(_root, "broken.zip");
        File.WriteAllText(path, "this is not a zip file");

        var result = new UpdatePackageExtractor().Extract(path, Destination());

        Assert.False(result.IsSuccess);
        Assert.Equal(NMTools.Core.Updates.UpdateExtractFailureReason.InvalidArchive, result.FailureReason);
    }

    [Fact]
    public void Extract_ClearsPreviousRunBeforeExtracting()
    {
        // 上一次失败的残留不能混进新的解压结果。
        var destination = Destination();
        Directory.CreateDirectory(Path.Combine(destination, "leftover"));
        File.WriteAllText(Path.Combine(destination, "leftover", "stale.txt"), "stale");

        var archive = CreateArchive(("NMTools-v0.7.0-win-x64/program/NMTools.App.exe", "exe"));
        var result = new UpdatePackageExtractor().Extract(archive, destination);

        Assert.True(result.IsSuccess);
        Assert.False(Directory.Exists(Path.Combine(destination, "leftover")));
    }

    private string Destination() => Path.Combine(_root, "out-" + Guid.NewGuid().ToString("N"));

    private string CreateArchive(params (string EntryPath, string Content)[] entries)
    {
        var path = Path.Combine(_root, "pkg-" + Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryPath, content) in entries)
        {
            var entry = archive.CreateEntry(entryPath);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }

        return path;
    }
}
