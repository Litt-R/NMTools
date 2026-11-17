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
    public void Compose_DoesNotTakeDigestFromReleaseNotes()
    {
        // 摘要只有一个来源：资产列表。说明正文里即便写了哈希也不采用，
        // 否则等于引入第二个需要人工维护的真相来源。
        const string notesHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var xml = AtomXml.Replace(
            "&lt;ul&gt;&lt;li&gt;Automatic updates.&lt;/li&gt;&lt;/ul&gt;",
            $"SHA256: {notesHash}");

        var manifest = UpdateManifestComposer.Compose(
            new ReleaseDocuments("v0.7.0", xml, null));

        Assert.NotNull(manifest);
        Assert.Null(manifest!.ExpectedSha256);
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
        // 页面拿到了但读不出摘要，说明解析器需要维护，不能与"没拿到页面"混为一谈。
        Assert.Equal(AssetLookupStatus.DigestMissing, manifest.AssetStatus);
    }

    [Fact]
    public void Compose_NotesAreTruncatedButManifestIsStillComplete()
    {
        var longNotes = new string('x', UpdateManifest.MaxNotesLength + 100);
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
            new ReleaseDocuments("v0.7.0", xml, AssetsHtml));

        Assert.NotNull(manifest);
        Assert.Equal(Digest, manifest!.ExpectedSha256);
        Assert.Equal(UpdateManifest.MaxNotesLength, manifest.Notes.Length);
    }

    [Fact]
    public void Compose_AssetMissingFromPage_IsDistinctFromDigestMissing()
    {
        // 页面拿到了，但没有我们这个资产名：属于上游结构变化，也要能被识别出来。
        var manifest = UpdateManifestComposer.Compose(
            new ReleaseDocuments("v0.7.0", AtomXml, "<div>no assets here</div>"));

        Assert.NotNull(manifest);
        Assert.Null(manifest!.ExpectedSha256);
        Assert.Equal(AssetLookupStatus.AssetNotFound, manifest.AssetStatus);
    }

    [Fact]
    public void Compose_MissingPage_IsMarkedAsPageMissing()
    {
        var manifest = UpdateManifestComposer.Compose(
            new ReleaseDocuments("v0.7.0", AtomXml, null));

        Assert.NotNull(manifest);
        Assert.Equal(AssetLookupStatus.PageMissing, manifest!.AssetStatus);
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
