using NMTools.Core.Updates;

namespace NMTools.Core.Tests;

public sealed class ReleasesAtomParserTests
{
    /// <summary>
    /// 取自真实的 v0.7.0 atom 响应结构。注意 content 是 HTML（标签被转义成实体），
    /// 而 title 是可以被改写的显示名。
    /// </summary>
    private const string AtomXml = """
    <?xml version="1.0" encoding="UTF-8"?>
    <feed xmlns="http://www.w3.org/2005/Atom" xml:lang="en-US">
      <title>Release notes from NMTools</title>
      <entry>
        <id>tag:github.com,2008:Repository/1439462299/v0.7.0</id>
        <link rel="alternate" type="text/html" href="https://github.com/Litt-R/NMTools/releases/tag/v0.7.0"/>
        <title>v0.7.0</title>
        <content type="html">&lt;h1&gt;v0.7.0&lt;/h1&gt;&lt;h2&gt;What&#39;s new&lt;/h2&gt;&lt;ul&gt;&lt;li&gt;&lt;strong&gt;Automatic updates.&lt;/strong&gt; Check for a new version.&lt;/li&gt;&lt;li&gt;Settings are preserved.&lt;/li&gt;&lt;/ul&gt;</content>
      </entry>
      <entry>
        <id>tag:github.com,2008:Repository/1439462299/v0.6.1</id>
        <link rel="alternate" type="text/html" href="https://github.com/Litt-R/NMTools/releases/tag/v0.6.1"/>
        <title>v0.6.1</title>
        <content type="html">&lt;p&gt;Older release.&lt;/p&gt;</content>
      </entry>
    </feed>
    """;

    [Fact]
    public void FindNotes_MatchesEntryAndConvertsHtmlToPlainText()
    {
        var notes = ReleasesAtomParser.FindNotes(AtomXml, "v0.7.0");

        // 标签被去掉，文字保留，列表项变成 "- " 开头。
        Assert.DoesNotContain("<", notes);
        Assert.DoesNotContain("&lt;", notes);
        Assert.Contains("v0.7.0", notes);
        Assert.Contains("What's new", notes);
        Assert.Contains("Automatic updates.", notes);
        Assert.Contains("- Automatic updates. Check for a new version.", notes);
        Assert.Contains("- Settings are preserved.", notes);
    }

    [Fact]
    public void FindNotes_DoesNotLeakOtherReleases()
    {
        var notes = ReleasesAtomParser.FindNotes(AtomXml, "v0.7.0");

        Assert.DoesNotContain("Older release", notes);
    }

    [Fact]
    public void FindNotes_ReadsTagFromLinkNotTitle()
    {
        // title 是可在 GitHub 后台改写的显示名；把它改掉后仍应按链接里的 tag 匹配。
        var renamed = AtomXml.Replace("<title>v0.7.0</title>", "<title>NMTools v0.7.0 Release</title>");

        var notes = ReleasesAtomParser.FindNotes(renamed, "v0.7.0");

        Assert.Contains("Automatic updates.", notes);
    }

    [Fact]
    public void FindNotes_DoesNotMatchOnTitleText()
    {
        // 反向验证：title 恰好等于 tag、但链接指向别的 tag 时，不能匹配。
        var decoy = AtomXml.Replace(
            "https://github.com/Litt-R/NMTools/releases/tag/v0.7.0",
            "https://github.com/Litt-R/NMTools/releases/tag/v0.5.0");

        var notes = ReleasesAtomParser.FindNotes(decoy, "v0.7.0");

        // v0.7.0 的链接已不存在，剩下的 entry 里没有匹配项。
        Assert.Equal(string.Empty, notes);
    }

    [Fact]
    public void FindNotes_UnknownTag_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, ReleasesAtomParser.FindNotes(AtomXml, "v9.9.9"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not xml")]
    [InlineData("<feed")]
    [InlineData("[]")]
    public void FindNotes_InvalidXml_ReturnsEmpty(string? xml)
    {
        Assert.Equal(string.Empty, ReleasesAtomParser.FindNotes(xml, "v0.7.0"));
    }

    [Fact]
    public void FindNotes_XmlWithDoctype_IsRejectedWithoutResolvingEntities()
    {
        // 外部实体解析必须被禁止，不能因为上游文档是外部内容就去读本地文件。
        const string hostile = """
        <?xml version="1.0"?>
        <!DOCTYPE feed [<!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini">]>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <entry>
            <link rel="alternate" href="https://github.com/Litt-R/NMTools/releases/tag/v0.7.0"/>
            <content type="html">&xxe;</content>
          </entry>
        </feed>
        """;

        Assert.Equal(string.Empty, ReleasesAtomParser.FindNotes(hostile, "v0.7.0"));
    }

    [Fact]
    public void FindNotes_LongContent_IsTruncated()
    {
        var longNote = new string('说', UpdateManifest.MaxNotesLength + 500);
        var longEntry = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <feed xmlns="http://www.w3.org/2005/Atom">
          <entry>
            <link rel="alternate" href="https://github.com/Litt-R/NMTools/releases/tag/v0.7.0"/>
            <content type="html">{longNote}</content>
          </entry>
        </feed>
        """;

        var notes = ReleasesAtomParser.FindNotes(longEntry, "v0.7.0");

        Assert.Equal(UpdateManifest.MaxNotesLength, notes.Length);
    }
}
