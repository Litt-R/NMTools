using NMTools.Core.Updates;

namespace NMTools.Core.Tests;

public sealed class ExpandedAssetsParserTests
{
    private const string Digest = "5e0458a5fe94c3fe72e6b87d1a80132dd833dd13f85dd9dd8203b4b55deec3b5";

    /// <summary>
    /// 取自真实的 expanded_assets 响应（已裁掉无关标记）。类名等结构细节可能变，
    /// 但 /releases/download/ 路径和 sha256: 前缀是解析依赖的特征。
    /// </summary>
    private static readonly string RealHtml = $"""
    <div class="Box">
      <ul>
        <li class="Box-row">
          <a href="/Litt-R/NMTools/releases/download/v0.7.0/NMTools-v0.7.0-win-x64.zip" rel="nofollow">
            <span class="text-bold">NMTools-v0.7.0-win-x64.zip</span>
          </a>
          <span class="text-mono">sha256:{Digest}</span>
          <span class="color-fg-muted">68.8 MB</span>
        </li>
        <li class="Box-row">
          <a href="/Litt-R/NMTools/archive/refs/tags/v0.7.0.zip" rel="nofollow">
            <span class="text-bold">Source code</span> (zip)
          </a>
        </li>
      </ul>
    </div>
    """;

    [Fact]
    public void Find_ReadsDigestAndSize()
    {
        var asset = ExpandedAssetsParser.Find(RealHtml, "NMTools-v0.7.0-win-x64.zip");

        Assert.Equal(Digest, asset.Sha256);
        Assert.NotNull(asset.SizeBytes);
        // 68.8 MB ≈ 72,149,000 字节，允许四舍五入到 0.1 的误差。
        Assert.InRange(asset.SizeBytes!.Value, 72_100_000, 72_200_000);
    }

    [Fact]
    public void Find_DoesNotPickUpDigestFromAnotherAsset()
    {
        // 源码包没有摘要。查询它时不能把相邻便携包的摘要串过来。
        var asset = ExpandedAssetsParser.Find(RealHtml, "v0.7.0.zip");

        Assert.Null(asset.Sha256);
    }

    [Fact]
    public void Find_UnknownAsset_ReturnsEmpty()
    {
        var asset = ExpandedAssetsParser.Find(RealHtml, "NMTools-v0.7.0-win-arm64.zip");

        Assert.Null(asset.Sha256);
        Assert.Null(asset.SizeBytes);
    }

    [Fact]
    public void Find_WithoutDigest_StillReportsSize()
    {
        // GitHub 改版导致摘要缺失时，应当降级为"没有摘要"而不是整体失败。
        var html = RealHtml.Replace($"sha256:{Digest}", string.Empty);

        var asset = ExpandedAssetsParser.Find(html, "NMTools-v0.7.0-win-x64.zip");

        Assert.Null(asset.Sha256);
        Assert.NotNull(asset.SizeBytes);
    }

    [Theory]
    [InlineData("1.5 KB", 1536L)]
    [InlineData("512 Bytes", 512L)]
    [InlineData("2 GB", 2147483648L)]
    public void Find_ParsesSizeUnits(string text, long expected)
    {
        var html = RealHtml.Replace("68.8 MB", text);

        var asset = ExpandedAssetsParser.Find(html, "NMTools-v0.7.0-win-x64.zip");

        Assert.Equal(expected, asset.SizeBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("<div>no assets here</div>")]
    public void Find_InvalidHtml_ReturnsEmpty(string? html)
    {
        var asset = ExpandedAssetsParser.Find(html, "NMTools-v0.7.0-win-x64.zip");

        Assert.Equal(AssetInfo.Empty, asset);
    }
}
