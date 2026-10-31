using NMTools.App.Views;

namespace NMTools.Core.Tests;

public sealed class BubbleTipsTests
{
    [Fact]
    public void TipsAreNonEmptyAndUnique()
    {
        Assert.NotEmpty(BubbleTips.All);
        Assert.All(BubbleTips.All, tip => Assert.False(string.IsNullOrWhiteSpace(tip)));
        Assert.Equal(BubbleTips.All.Count, BubbleTips.All.Distinct().Count());
    }

    [Fact]
    public void TipsFitOnSingleFooterLine()
    {
        Assert.All(BubbleTips.All, tip => Assert.True(
            tip.Length <= BubbleTips.MaxLength,
            $"提示过长（{tip.Length} 字）：{tip}"));
    }

    [Fact]
    public void RotationContainsEveryTipExactlyOnce()
    {
        for (var seed = 0; seed < 100; seed++)
        {
            var rotation = BubbleTips.CreateRotation(new Random(seed));

            Assert.Equal(BubbleTips.All.Count, rotation.Count);
            Assert.Equal(BubbleTips.All.OrderBy(tip => tip), rotation.OrderBy(tip => tip));
        }
    }

    [Fact]
    public void RotationNeverPlacesSameKindOfEncouragementTogether()
    {
        var cheers = new HashSet<string>
        {
            "慢慢写，不着急哦~ (´▽`)ノ",
            "真棒！继续加油ヽ(・ω・)ノ",
            "坐久了？起来站一站、走几步吧~",
            "眼睛离屏一会儿，看看远处~",
        };
        var breakReminders = new HashSet<string>
        {
            "辛苦啦~ 记得喝口水休息一下 (づ｡◕‿◕｡)づ",
            "喝口水，放松下肩膀再继续~",
        };
        Assert.Equal(4, cheers.Count);
        Assert.Equal(2, breakReminders.Count);

        for (var seed = 0; seed < 500; seed++)
        {
            var rotation = BubbleTips.CreateRotation(new Random(seed));
            for (var i = 0; i < rotation.Count; i++)
            {
                var next = rotation[(i + 1) % rotation.Count];
                Assert.False(
                    cheers.Contains(rotation[i]) && cheers.Contains(next),
                    $"种子 {seed}：相邻的鼓励语 “{rotation[i]}” 与 “{next}”。");
                Assert.False(
                    breakReminders.Contains(rotation[i]) && breakReminders.Contains(next),
                    $"种子 {seed}：相邻的休息提醒 “{rotation[i]}” 与 “{next}”。");
            }
        }
    }

    [Fact]
    public void RotationOrderVariesAndSameSeedIsRepeatable()
    {
        var orders = Enumerable.Range(0, 50)
            .Select(seed => string.Join("|", BubbleTips.CreateRotation(new Random(seed))))
            .ToHashSet();

        Assert.True(orders.Count > 10, $"50 个种子只产生了 {orders.Count} 种顺序。");
        Assert.Equal(
            BubbleTips.CreateRotation(new Random(7)),
            BubbleTips.CreateRotation(new Random(7)));
    }

    [Fact]
    public void TipsCoverClipboardHistoryPasteFailureAndSittingReminder()
    {
        Assert.Contains(BubbleTips.All, tip => tip.Contains("Win + V"));
        Assert.Contains(BubbleTips.All, tip => tip.Contains("还在剪贴板"));
        Assert.Contains(BubbleTips.All, tip => tip.Contains("起来站一站"));
    }
}
