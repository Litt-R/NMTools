using NMTools.Core.Updates;

namespace NMTools.Core.Tests;

public sealed class UpdateManifestParserTests
{
    private const string FullReleaseJson = """
    {
      "tag_name": "v0.7.0",
      "name": "NMTools v0.7.0",
      "body": "- 新增自动更新检查\n- 修复托盘图标",
      "draft": false,
      "prerelease": false,
      "html_url": "https://github.com/Litt-R/NMTools/releases/tag/v0.7.0",
      "assets": [
        {
          "name": "NMTools-v0.7.0-win-x64.zip",
          "size": 12345678,
          "browser_download_url": "https://github.com/Litt-R/NMTools/releases/download/v0.7.0/NMTools-v0.7.0-win-x64.zip"
        }
      ]
    }
    """;

    [Fact]
    public void Parse_ValidRelease_ReturnsManifest()
    {
        var manifest = UpdateManifestParser.Parse(FullReleaseJson);

        Assert.NotNull(manifest);
        Assert.Equal(new Version(0, 7, 0, 0), manifest!.Version);
        Assert.Equal("v0.7.0", manifest.TagName);
        Assert.Contains("新增自动更新检查", manifest.Notes);
        Assert.Equal(12345678L, manifest.DownloadSizeBytes);
    }

    [Fact]
    public void Parse_ValidRelease_FindsArchiveAssetByName()
    {
        var manifest = UpdateManifestParser.Parse(FullReleaseJson);

        Assert.NotNull(manifest);
        Assert.Equal(
            "https://github.com/Litt-R/NMTools/releases/download/v0.7.0/NMTools-v0.7.0-win-x64.zip",
            manifest!.DownloadUrl);
    }

    [Fact]
    public void Parse_ArchiveAssetWithWrongName_IsIgnored()
    {
        // 只有发布脚本产出的那个 zip 才是便携包，其他资产（源码包、校验文件）不能被当成更新包。
        var json = FullReleaseJson.Replace(
            "NMTools-v0.7.0-win-x64.zip",
            "NMTools-v0.7.0-win-arm64.zip");

        var manifest = UpdateManifestParser.Parse(json);

        Assert.NotNull(manifest);
        Assert.Null(manifest!.DownloadUrl);
        Assert.Null(manifest.DownloadSizeBytes);
    }

    [Fact]
    public void Parse_ArchiveAssetWithUntrustedHost_IsIgnored()
    {
        // 响应里的下载链接必须落在本仓库域名下，否则不采用。
        var json = FullReleaseJson.Replace(
            "https://github.com/Litt-R/NMTools/releases/download",
            "https://evil.example.com/Litt-R/NMTools/releases/download");

        var manifest = UpdateManifestParser.Parse(json);

        Assert.NotNull(manifest);
        Assert.Null(manifest!.DownloadUrl);
    }

    [Fact]
    public void Parse_IgnoresReleasePageUrlEntirely()
    {
        // 程序不打开任何外部页面，清单里也就不该保留页面链接——
        // 界面拿不到地址，就不存在把用户引到浏览器的可能。
        var json = FullReleaseJson.Replace(
            "\"html_url\": \"https://github.com/Litt-R/NMTools/releases/tag/v0.7.0\"",
            "\"html_url\": \"https://evil.example.com/phish\"");

        var manifest = UpdateManifestParser.Parse(json);

        Assert.NotNull(manifest);
        Assert.DoesNotContain(
            typeof(UpdateManifest).GetProperties(),
            property => property.Name.Contains("Url", StringComparison.Ordinal)
                && property.PropertyType == typeof(string)
                && property.Name != nameof(UpdateManifest.DownloadUrl));
    }

    [Theory]
    [InlineData("true", "false")]
    [InlineData("false", "true")]
    public void Parse_DraftOrPrerelease_IsRejected(string draft, string prerelease)
    {
        var json = FullReleaseJson
            .Replace("\"draft\": false", $"\"draft\": {draft}")
            .Replace("\"prerelease\": false", $"\"prerelease\": {prerelease}");

        Assert.Null(UpdateManifestParser.Parse(json));
    }

    [Fact]
    public void Parse_PrereleaseTag_IsRejected()
    {
        var json = FullReleaseJson.Replace("\"tag_name\": \"v0.7.0\"", "\"tag_name\": \"v0.7.0-beta.1\"");

        Assert.Null(UpdateManifestParser.Parse(json));
    }

    [Fact]
    public void Parse_MissingTagName_IsRejected()
    {
        var json = FullReleaseJson.Replace("\"tag_name\": \"v0.7.0\",", string.Empty);

        Assert.Null(UpdateManifestParser.Parse(json));
    }

    [Fact]
    public void Parse_MissingBody_UsesEmptyNotes()
    {
        var json = FullReleaseJson.Replace("\"body\": \"- 新增自动更新检查\\n- 修复托盘图标\",", string.Empty);

        var manifest = UpdateManifestParser.Parse(json);

        Assert.NotNull(manifest);
        Assert.Equal(string.Empty, manifest!.Notes);
    }

    [Fact]
    public void Parse_LongBody_IsTruncated()
    {
        var longBody = new string('说', UpdateManifest.MaxNotesLength + 500);
        var json = FullReleaseJson.Replace("- 新增自动更新检查\\n- 修复托盘图标", longBody);

        var manifest = UpdateManifestParser.Parse(json);

        Assert.NotNull(manifest);
        Assert.Equal(UpdateManifest.MaxNotesLength, manifest!.Notes.Length);
    }

    [Fact]
    public void Parse_MissingAssets_YieldsNullDownload()
    {
        var json = FullReleaseJson[..FullReleaseJson.IndexOf("\"assets\"", StringComparison.Ordinal)]
            + "\"assets\": []\n}";

        var manifest = UpdateManifestParser.Parse(json);

        Assert.NotNull(manifest);
        Assert.Null(manifest!.DownloadUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("{ broken")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("123")]
    public void Parse_InvalidInput_ReturnsNull(string? json)
    {
        Assert.Null(UpdateManifestParser.Parse(json));
    }

    [Fact]
    public void Parse_RateLimitJsonObject_ReturnsNull()
    {
        // GitHub 达到速率限制时可能返回不含 tag_name 的 JSON 对象，不能让它当成有效版本。
        const string rateLimited = """
        {
          "message": "API rate limit exceeded for 1.2.3.4.",
          "documentation_url": "https://docs.github.com/rest/overview/resources-in-the-rest-api#rate-limiting"
        }
        """;

        Assert.Null(UpdateManifestParser.Parse(rateLimited));
    }

    [Fact]
    public void ExpectedArchiveName_MatchesPublishScriptConvention()
    {
        // scripts/publish-portable.ps1 生成 NMTools-v<版本>-win-x64.zip，两边必须一致。
        Assert.Equal(
            "NMTools-v0.7.0-win-x64.zip",
            UpdateManifest.ExpectedArchiveName(new Version(0, 7, 0, 0)));
    }
}
