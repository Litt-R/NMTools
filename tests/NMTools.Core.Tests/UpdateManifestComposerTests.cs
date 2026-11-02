using NMTools.Core.Updates;

namespace NMTools.Core.Tests;

public sealed class UpdateManifestComposerTests
{
    private const string Digest = "5e0458a5fe94c3fe72e6b87d1a80132dd833dd13f85dd9dd8203b4b55deec3b5";

    private const string AtomXml = """
    <?xml version="1.0" encoding="UTF-8"?>
    <feed xmlns="http://www.w3.org/2005/Atom">
      <entry>
        <link rel="alternate" type="text/html" href="https://github.com/Litt-R/NMTools/releases/tag/v0.7.0"/>
        <title>v0.7.0</title>
        <content type="html">&lt;ul&gt;&lt;li&gt;Automatic updates.&lt;/li&gt;&lt;/ul&gt;</content>
      </entry>
    </feed>
    """;

    private static readonly string AssetsHtml = $"""
    <a href="/Litt-R/NMTools/releases/download/v0.7.0/NMTools-v0.7.0-win-x64.zip">NMTools</a>
    <span>sha256:{Digest}</span>
    <span>68.8 MB</span>
    """;

    [Fact]
    public void Compose_ValidDocuments_BuildsManifest()
    {
        var manifest = UpdateManifestComposer.Compose(
            new ReleaseDocuments("v0.7.0", AtomXml, AssetsHtml));

        Assert.NotNull(manifest);
        Assert.Equal(new Version(0, 7, 0, 0), manifest!.Version);
        Assert.Equal("v0.7.0", manifest.TagName);
        Assert.Contains("Automatic updates.", manifest.Notes);
        Assert.Equal(Digest, manifest.ExpectedSha256);
        Assert.NotNull(manifest.DownloadSizeBytes);
        Assert.True(manifest.SupportsAutomaticUpdate);
    }

    [Fact]
    public void Compose_DownloadUrlIsDerivedFromTagNotFromResponseBody()
    {
        // ADR 0006：下载地址只由 tag 推导，即使资产列表里写了别的地址也不采用。
        var hostile = """
        <a href="https://evil.example.com/NMTools-v0.7.0-win-x64.zip">x</a>
        """;

        var manifest = UpdateManifestComposer.Compose(
            new ReleaseDocuments("v0.7.0", AtomXml, hostile));

        Assert.NotNull(manifest);
        Assert.Equal(
            "https://github.com/Litt-R/NMTools/releases/download/v0.7.0/NMTools-v0.7.0-win-x64.zip",
            manifest!.DownloadUrl);
    }

    [Fact]
    public void Compose_MissingAtom_StillProducesManifestWithEmptyNotes()
    {
        var manifest = UpdateManifestComposer.Compose(
            new ReleaseDocuments("v0.7.0", null, AssetsHtml));

        Assert.NotNull(manifest);
        Assert.Equal(string.Empty, manifest!.Notes);
    }

    [Fact]
    public void Compose_MissingAssets_StillUpdatableButUnverified()
    {
        // 资产列表拿不到时降级：没有大小和摘要，但仍可下载（与旧行为一致）。
        var manifest = UpdateManifestComposer.Compose(
            new ReleaseDocuments("v0.7.0", AtomXml, null));

        Assert.NotNull(manifest);
        Assert.Null(manifest!.DownloadSizeBytes);
        Assert.Null(manifest.ExpectedSha256);
        Assert.True(manifest.SupportsAutomaticUpdate);
    }

    [Fact]
    public void Compose_FallsBackToChecksumInNotes()
    {
        var xml = AtomXml.Replace(
            "&lt;ul&gt;&lt;li&gt;Automatic updates.&lt;/li&gt;&lt;/ul&gt;",
            $@"SHA256: {Digest}");

        var manifest = UpdateManifestComposer.Compose(
            new ReleaseDocuments("v0.7.0", xml, null));

        Assert.NotNull(manifest);
        Assert.Equal(Digest, manifest!.ExpectedSha256);
    }

    [Fact]
    public void Compose_PrefersAssetDigestOverReleaseNotes()
    {
        // 资产列表的摘要由上传内容算出，不需要人工维护，优先于正文里手写的哈希。
        const string notesHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var xml = AtomXml.Replace(
            "&lt;ul&gt;&lt;li&gt;Automatic updates.&lt;/li&gt;&lt;/ul&gt;",
            $"SHA256: {notesHash}");

        var manifest = UpdateManifestComposer.Compose(
            new ReleaseDocuments("v0.7.0", xml, AssetsHtml));

        Assert.NotNull(manifest);
        Assert.Equal(Digest, manifest!.ExpectedSha256);
    }

    [Theory]
    [InlineData("sha512:abc")]
    [InlineData("md5:d41d8cd98f00b204e9800998ecf8427e")]
    [InlineData("sha256:nothash")]
    public void Compose_IgnoresUnusableDigestInAssets(string digest)
    {
        var html = AssetsHtml.Replace(Digest, digest);

        var manifest = UpdateManifestComposer.Compose(
            new ReleaseDocuments("v0.7.0", AtomXml, html));

        Assert.NotNull(manifest);
        Assert.Null(manifest!.ExpectedSha256);
    }

    [Fact]
    public void Compose_FindsChecksumBeyondNotesTruncationLimit()
    {
        // 摘要出现在超长说明末尾时也要能取到——截断只影响展示文本。
        var longNotes = new string('x', UpdateManifest.MaxNotesLength + 100)
            + "\n\nSHA256: " + Digest;
        var xml = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <entry>
            <link rel="alternate" href="https://github.com/Litt-R/NMTools/releases/tag/v0.7.0"/>
            <content type="html">{longNotes}</content>
          </entry>
        </feed>
        """;

        var manifest = UpdateManifestComposer.Compose(
            new ReleaseDocuments("v0.7.0", xml, null));

        Assert.NotNull(manifest);
        Assert.Equal(Digest, manifest!.ExpectedSha256);
        Assert.Equal(UpdateManifest.MaxNotesLength, manifest.Notes.Length);
    }

    [Theory]
    [InlineData("v0.7.0-beta.1")]
    [InlineData("nightly")]
    public void Compose_UnstableTag_IsRejected(string tag)
    {
        Assert.Null(UpdateManifestComposer.Compose(
            new ReleaseDocuments(tag, AtomXml, AssetsHtml)));
    }

    [Fact]
    public void Compose_MissingTag_IsRejected()
    {
        Assert.Null(UpdateManifestComposer.Compose(
            new ReleaseDocuments(null, AtomXml, AssetsHtml)));
    }

    [Fact]
    public void Compose_NullDocuments_IsRejected()
    {
        Assert.Null(UpdateManifestComposer.Compose(null));
    }
}
