using NMTools.Core.Updates;

namespace NMTools.Core.Tests;

public sealed class ReleaseNotesChecksumTests
{
    private const string Hash = "89b1e7728396c300d631b4d85241a441a9adf33e3657cba7009c2727d6e73b2d";

    [Theory]
    [InlineData($"SHA256: {Hash}")]
    [InlineData($"SHA-256: {Hash}")]
    [InlineData($"sha256：{Hash}")]
    [InlineData($"SHA-256：`{Hash}`")]
    [InlineData($"**SHA256:** {Hash}")]
    [InlineData($"## What's new\n\n- stuff\n\nSHA256: {Hash}\n")]
    public void Extract_FindsChecksumInCommonFormats(string notes)
    {
        Assert.Equal(Hash, ReleaseNotesChecksum.Extract(notes));
    }

    [Fact]
    public void Extract_NormalizesToLowercase()
    {
        Assert.Equal(Hash, ReleaseNotesChecksum.Extract($"SHA256: {Hash.ToUpperInvariant()}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("no checksum here")]
    [InlineData("SHA256: notahash")]
    [InlineData("SHA256: 89b1e7")]
    [InlineData("SHA256:")]
    public void Extract_ReturnsNullWhenAbsent(string? notes)
    {
        Assert.Null(ReleaseNotesChecksum.Extract(notes));
    }

    [Theory]
    [InlineData(Hash, Hash, true)]
    [InlineData(Hash, "0000000000000000000000000000000000000000000000000000000000000000", false)]
    [InlineData(Hash, null, false)]
    [InlineData(null, Hash, false)]
    [InlineData("", "", false)]
    public void Matches_ComparesCaseInsensitivelyAndRejectsEmpty(
        string? expected,
        string? actual,
        bool shouldMatch)
    {
        Assert.Equal(shouldMatch, ReleaseNotesChecksum.Matches(expected, actual));
    }

    [Fact]
    public void Matches_IgnoresCase()
    {
        Assert.True(ReleaseNotesChecksum.Matches(Hash, Hash.ToUpperInvariant()));
        Assert.True(ReleaseNotesChecksum.Matches(Hash.ToUpperInvariant(), Hash));
    }

    [Fact]
    public void Parser_ExtractsChecksumFromReleaseBody()
    {
        var json = $$"""
        {
          "tag_name": "v0.7.0",
          "body": "## What's new\n\n- Updates\n\nSHA256: {{Hash}}",
          "draft": false,
          "prerelease": false,
          "assets": []
        }
        """;

        var manifest = UpdateManifestParser.Parse(json);

        Assert.NotNull(manifest);
        Assert.Equal(Hash, manifest!.ExpectedSha256);
    }

    [Fact]
    public void Parser_WithoutChecksumLeavesItNull()
    {
        const string json = """
        {
          "tag_name": "v0.7.0",
          "body": "Just notes, no hash.",
          "draft": false,
          "prerelease": false,
          "assets": []
        }
        """;

        var manifest = UpdateManifestParser.Parse(json);

        Assert.NotNull(manifest);
        Assert.Null(manifest!.ExpectedSha256);
    }

    [Fact]
    public void Parser_PrefersAssetDigestOverReleaseNotes()
    {
        // GitHub 的 digest 由上传内容算出，不需要人工维护，优先级高于正文里手写的哈希。
        const string digest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var json = $$"""
        {
          "tag_name": "v0.7.0",
          "body": "SHA256: {{Hash}}",
          "draft": false,
          "prerelease": false,
          "assets": [
            {
              "name": "NMTools-v0.7.0-win-x64.zip",
              "size": 1024,
              "digest": "sha256:{{digest}}",
              "browser_download_url": "https://github.com/Litt-R/NMTools/releases/download/v0.7.0/NMTools-v0.7.0-win-x64.zip"
            }
          ]
        }
        """;

        var manifest = UpdateManifestParser.Parse(json);

        Assert.NotNull(manifest);
        Assert.Equal(digest, manifest!.ExpectedSha256);
    }

    [Fact]
    public void Parser_FallsBackToNotesWhenDigestMissing()
    {
        // 较早的 release 没有 digest 字段，退回正文里手写的哈希。
        var json = $$"""
        {
          "tag_name": "v0.7.0",
          "body": "SHA256: {{Hash}}",
          "draft": false,
          "prerelease": false,
          "assets": [
            {
              "name": "NMTools-v0.7.0-win-x64.zip",
              "size": 1024,
              "browser_download_url": "https://github.com/Litt-R/NMTools/releases/download/v0.7.0/NMTools-v0.7.0-win-x64.zip"
            }
          ]
        }
        """;

        var manifest = UpdateManifestParser.Parse(json);

        Assert.NotNull(manifest);
        Assert.Equal(Hash, manifest!.ExpectedSha256);
    }

    [Theory]
    [InlineData("sha512:abc")]
    [InlineData("md5:d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("sha256:nothash")]
    [InlineData("")]
    public void Parser_IgnoresUnusableDigest(string digest)
    {
        var json = $$"""
        {
          "tag_name": "v0.7.0",
          "body": "no hash",
          "draft": false,
          "prerelease": false,
          "assets": [
            {
              "name": "NMTools-v0.7.0-win-x64.zip",
              "size": 1024,
              "digest": "{{digest}}",
              "browser_download_url": "https://github.com/Litt-R/NMTools/releases/download/v0.7.0/NMTools-v0.7.0-win-x64.zip"
            }
          ]
        }
        """;

        var manifest = UpdateManifestParser.Parse(json);

        Assert.NotNull(manifest);
        Assert.Null(manifest!.ExpectedSha256);
    }

    [Fact]
    public void Parser_FindsChecksumBeyondTheNotesTruncationLimit()
    {
        // 摘要出现在超长说明的末尾时也要能取到——截断只影响展示文本。
        var longBody = new string('x', UpdateManifest.MaxNotesLength + 100) + "\n\nSHA256: " + Hash;

        // 用序列化器构造 JSON，避免手写时换行和引号转义出错。
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            tag_name = "v0.7.0",
            body = longBody,
            draft = false,
            prerelease = false,
            assets = Array.Empty<object>(),
        });

        var manifest = UpdateManifestParser.Parse(json);

        Assert.NotNull(manifest);
        Assert.Equal(Hash, manifest!.ExpectedSha256);
        // 展示用的说明仍然被截断。
        Assert.Equal(UpdateManifest.MaxNotesLength, manifest.Notes.Length);
    }
}
