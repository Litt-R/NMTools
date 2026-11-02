using System.Net;
using System.Text;

namespace NMTools.Core.Updates;

/// <summary>
/// 把网页端点返回的 HTML 转成展示用纯文本。
///
/// ADR 0006 把更新说明的来源从 API 的 JSON <c>body</c> 换成了 Atom 的 <c>&lt;content&gt;</c>，
/// 而后者是**渲染后的 HTML**（<c>&lt;h1&gt;</c>、<c>&lt;li&gt;</c> 等），不是 Markdown。
/// 直接展示会让用户看到标签本身，所以在 Core 里统一降级成纯文本。
///
/// 只做"去掉标记、保留文字"这一件事：不解码实体以外的内容、不加载任何外部资源、
/// 不生成富文本。目标是准确传达信息，而不是还原排版。
/// </summary>
public static class HtmlText
{
    /// <summary>
    /// 转成纯文本：块级标签换行、列表项加前缀、其余标签丢弃，最后解码 HTML 实体。
    /// </summary>
    public static string ToPlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(html.Length);
        var index = 0;

        while (index < html.Length)
        {
            var open = html.IndexOf('<', index);
            if (open < 0)
            {
                builder.Append(WebUtility.HtmlDecode(html[index..]));
                break;
            }

            if (open > index)
            {
                builder.Append(WebUtility.HtmlDecode(html[index..open]));
            }

            var close = html.IndexOf('>', open);
            if (close < 0)
            {
                // 残缺的标签：丢弃剩余部分，避免把 "<img src=..." 半截当正文。
                break;
            }

            AppendTagBoundary(builder, html[(open + 1)..close]);
            index = close + 1;
        }

        return Normalize(builder.ToString());
    }

    /// <summary>
    /// 根据标签名补上分隔符。只认少数几个对阅读有意义的标签，
    /// 其余（<c>a</c>、<c>strong</c>、<c>code</c> 等行内标签）直接丢弃。
    /// </summary>
    private static void AppendTagBoundary(StringBuilder builder, string tagBody)
    {
        var isClosing = tagBody.StartsWith('/');
        var name = tagBody.AsSpan(isClosing ? 1 : 0);
        var space = name.IndexOfAny(" \t\r\n/");
        if (space >= 0)
        {
            name = name[..space];
        }

        if (name.Equals("li", StringComparison.OrdinalIgnoreCase))
        {
            // 只有开标签才加前缀；闭合标签只负责换行，否则会多出"- - "和孤立的短横线。
            AppendLineBreak(builder);
            if (!isClosing)
            {
                builder.Append("- ");
            }

            return;
        }

        if (name.Equals("br", StringComparison.OrdinalIgnoreCase))
        {
            AppendLineBreak(builder);
            return;
        }

        // h1..h6、p、ul、ol、div、blockquote、pre、tr 等块级标签前后换行。
        if (name.Equals("p", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ul", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ol", StringComparison.OrdinalIgnoreCase)
            || name.Equals("div", StringComparison.OrdinalIgnoreCase)
            || name.Equals("blockquote", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pre", StringComparison.OrdinalIgnoreCase)
            || name.Equals("tr", StringComparison.OrdinalIgnoreCase)
            || IsHeading(name))
        {
            AppendLineBreak(builder);
        }
    }

    private static bool IsHeading(ReadOnlySpan<char> name)
    {
        return name.Length == 2
            && (name[0] is 'h' or 'H')
            && name[1] is >= '1' and <= '6';
    }

    /// <summary>补一个换行，但避免连续空行。</summary>
    private static void AppendLineBreak(StringBuilder builder)
    {
        if (builder.Length == 0)
        {
            return;
        }

        // 行尾已有换行时不再叠加，否则 Markdown 里的空行会被放大成大片空白。
        var end = builder.Length - 1;
        while (end >= 0 && (builder[end] == ' ' || builder[end] == '\t'))
        {
            end--;
        }

        if (end >= 0 && builder[end] == '\n')
        {
            return;
        }

        builder.Append('\n');
    }

    /// <summary>
    /// 统一换行符、去掉行尾空白、把连续空行压成一个，并去掉首尾空白。
    /// 保留相对缩进之外的原始文字，不改写内容。
    /// </summary>
    private static string Normalize(string text)
    {
        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        var builder = new StringBuilder(text.Length);
        var blankPending = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd();
            if (trimmed.Length == 0)
            {
                blankPending = builder.Length > 0;
                continue;
            }

            if (blankPending)
            {
                builder.Append('\n');
                blankPending = false;
            }

            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            builder.Append(trimmed);
        }

        return builder.ToString();
    }
}
