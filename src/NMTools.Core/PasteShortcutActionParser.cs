namespace NMTools.Core;

public static class PasteShortcutActionParser
{
    /// <summary>
    /// 把界面按钮的 Tag（XAML 里声明）解析为槽位。Tag 直接使用 <see cref="PasteShortcutAction"/> 的成员名，
    /// 因此不需要再维护一份手写的字符串映射。大小写敏感；数字、空白、逗号组合和未定义的值一律返回 null。
    /// <see cref="Enum.TryParse{TEnum}(string?, out TEnum)"/> 本身会接受 <c>"0"</c>、<c>"99"</c> 这类数字字符串，
    /// 也会把 <c>"Enter,Emacs"</c> 当作按位组合解析，这些都不是合法的槽位，所以这里先按成员名精确匹配。
    /// </summary>
    public static PasteShortcutAction? TryParse(string? slot)
    {
        if (string.IsNullOrEmpty(slot))
        {
            return null;
        }

        foreach (var action in Enum.GetValues<PasteShortcutAction>())
        {
            if (string.Equals(action.ToString(), slot, StringComparison.Ordinal))
            {
                return action;
            }
        }

        return null;
    }
}
