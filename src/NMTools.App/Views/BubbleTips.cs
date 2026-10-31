namespace NMTools.App.Views;

public static class BubbleTips
{
    public const int MaxLength = 30;

    private static readonly string[] Practical =
    {
        "Win + V 可打开剪贴板历史",
        "没粘上？多半是 NX 抖了一下，文字还在剪贴板",
        "Shift + Enter 换行，Esc 取消",
        "按住 Alt 拖拽可移动气泡",
        "不同快捷键对应不同粘贴方式，可在设置修改",
    };

    private static readonly string[] Cheers =
    {
        "慢慢写，不着急哦~ (´▽`)ノ",
        "真棒！继续加油ヽ(・ω・)ノ",
        "坐久了？起来站一站、走几步吧~",
        "眼睛离屏一会儿，看看远处~",
    };

    private static readonly string[] BreakReminders =
    {
        "喝口水，放松下肩膀再继续~",
    };

    public static IReadOnlyList<string> All { get; } = Practical.Concat(Cheers).Concat(BreakReminders).ToArray();

    /// <summary>
    /// 洗牌后的一轮播放顺序：每条恰好出现一次，同类鼓励语或休息提醒不相邻（首尾相接也算）。
    /// </summary>
    public static IReadOnlyList<string> CreateRotation(Random? random = null)
    {
        random ??= Random.Shared;
        var cheers = new HashSet<string>(Cheers);
        var breakReminders = new HashSet<string>(BreakReminders);
        var order = All.ToArray();

        // 避免同类鼓励语或休息提醒连续出现；不同类别可以相邻。
        for (var attempt = 0; attempt < 200; attempt++)
        {
            Shuffle(order, random);
            if (!HasAdjacentCheers(order, cheers) && !HasAdjacentCheers(order, breakReminders))
            {
                break;
            }
        }

        return order;
    }

    private static void Shuffle(string[] items, Random random)
    {
        for (var i = items.Length - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private static bool HasAdjacentCheers(string[] order, HashSet<string> cheers)
    {
        for (var i = 0; i < order.Length; i++)
        {
            if (cheers.Contains(order[i]) && cheers.Contains(order[(i + 1) % order.Length]))
            {
                return true;
            }
        }

        return false;
    }
}
