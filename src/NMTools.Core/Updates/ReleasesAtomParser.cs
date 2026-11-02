using System.Xml;
using System.Xml.Linq;

namespace NMTools.Core.Updates;

/// <summary>
/// 从 <c>releases.atom</c> 中按 tag 查找更新说明（见 ADR 0006）。
///
/// 两条硬性规则：
/// - **tag 只从 <c>&lt;link rel="alternate" href=".../releases/tag/{tag}"&gt;</c> 取，不取 <c>&lt;title&gt;</c>**。
///   <c>&lt;title&gt;</c> 是可在 GitHub 后台随意改写的 release 显示名（例如 "NMTools v0.7.0"），
///   只有链接里的才是真正的 tag。
/// - **不负责判断"哪个是最新版"**。Atom 包含预发布和草稿，用它挑最新版会把预发布当成稳定版推给用户；
///   最新版由 <c>releases/latest</c> 的 302 语义决定。
/// </summary>
public static class ReleasesAtomParser
{
    private static readonly XNamespace AtomNamespace = "http://www.w3.org/2005/Atom";

    /// <summary>
    /// 查找 tag 对应的更新说明，返回**已截断**的纯文本，供界面展示。
    /// entry 不存在或结构异常时返回空字符串。
    /// </summary>
    public static string FindNotes(string? atomXml, string? tagName)
    {
        var text = FindFullNotes(atomXml, tagName);
        return text.Length > UpdateManifest.MaxNotesLength
            ? text[..UpdateManifest.MaxNotesLength]
            : text;
    }

    /// <summary>
    /// 同 <see cref="FindNotes"/>，但不截断。
    ///
    /// 摘要必须从完整文本里提取：64 位哈希正好落在截断处时，用截断后的文本会找不到它，
    /// 导致"发布了带哈希的说明却校验不上"。
    /// </summary>
    public static string FindFullNotes(string? atomXml, string? tagName)
    {
        if (string.IsNullOrWhiteSpace(atomXml) || string.IsNullOrWhiteSpace(tagName))
        {
            return string.Empty;
        }

        var entry = FindEntry(atomXml, tagName);
        if (entry is null)
        {
            return string.Empty;
        }

        // content 是 HTML（<h1>、<li> 等），降级成纯文本再交给界面。
        var content = entry.Element(AtomNamespace + "content")?.Value
            ?? entry.Element(AtomNamespace + "summary")?.Value
            ?? string.Empty;

        return HtmlText.ToPlainText(content);
    }

    /// <summary>
    /// 按 tag 精确匹配 entry。匹配依据是 alternate 链接的末段，大小写不敏感
    /// （GitHub 的 tag 大小写不敏感）。
    /// </summary>
    internal static XElement? FindEntry(string atomXml, string tagName)
    {
        try
        {
            // 禁用 DTD 和外部实体解析：来源是外部文档，不能让它触发网络或本地文件读取。
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreWhitespace = false,
            };

            using var reader = XmlReader.Create(new System.IO.StringReader(atomXml), settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            var feed = document.Root;
            if (feed is null || feed.Name != AtomNamespace + "feed")
            {
                return null;
            }

            foreach (var entry in feed.Elements(AtomNamespace + "entry"))
            {
                if (MatchesTag(entry, tagName))
                {
                    return entry;
                }
            }

            return null;
        }
        catch (XmlException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool MatchesTag(XElement entry, string tagName)
    {
        foreach (var link in entry.Elements(AtomNamespace + "link"))
        {
            if (!string.Equals(
                    (string?)link.Attribute("rel"),
                    "alternate",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var href = (string?)link.Attribute("href");
            var extracted = UpdateSource.TryExtractTagFromReleaseUrl(href);
            if (extracted is not null
                && string.Equals(extracted, tagName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
