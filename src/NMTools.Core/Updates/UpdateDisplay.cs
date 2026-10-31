namespace NMTools.Core.Updates;

/// <summary>
/// 更新提示界面用到的纯展示逻辑。放在 Core 是为了能用单元测试覆盖文案规则，
/// 避免把这些字符串散落在 XAML 和 code-behind 里各写一遍。
/// </summary>
public static class UpdateDisplay
{
    public const string DialogTitle = "NMTools - 发现新版本";

    /// <summary>没有更新说明时的占位文案。</summary>
    public const string NoNotesPlaceholder = "该版本没有提供更新说明。";

    /// <summary>打开 Release 页面失败时的提示。</summary>
    public const string OpenReleasePageFailedMessage =
        "无法打开浏览器，请手动访问 GitHub Releases 页面下载新版本。";

    public static string FormatVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        var normalized = ReleaseVersion.Normalize(version);
        return normalized.Revision > 0
            ? $"v{normalized.Major}.{normalized.Minor}.{normalized.Build}.{normalized.Revision}"
            : $"v{normalized.Major}.{normalized.Minor}.{normalized.Build}";
    }

    /// <summary>
    /// 新版本卡片上的标题行，例如「新版本 v0.7.0（当前 v0.6.1）」。
    /// </summary>
    public static string FormatHeadline(Version newVersion, Version currentVersion)
    {
        return $"新版本 {FormatVersion(newVersion)}（当前 {FormatVersion(currentVersion)}）";
    }

    /// <summary>
    /// 更新说明正文。GitHub 的 body 可能包含 Markdown，这里原样展示纯文本，
    /// 不做渲染也不做解释——用户需要的是准确信息，而不是被重新排版的版本。
    /// </summary>
    public static string FormatNotes(string? notes)
    {
        return string.IsNullOrWhiteSpace(notes) ? NoNotesPlaceholder : notes.Trim();
    }

    /// <summary>
    /// 下载体积，例如「12.3 MB」。未知时返回 null，由调用方决定是否显示。
    /// </summary>
    public static string? FormatSize(long? bytes)
    {
        if (bytes is not > 0)
        {
            return null;
        }

        var value = bytes.Value;
        if (value >= 1024L * 1024 * 1024)
        {
            return $"{value / (1024d * 1024 * 1024):0.#} GB";
        }

        if (value >= 1024L * 1024)
        {
            return $"{value / (1024d * 1024):0.#} MB";
        }

        return $"{Math.Max(1, value / 1024)} KB";
    }
}
