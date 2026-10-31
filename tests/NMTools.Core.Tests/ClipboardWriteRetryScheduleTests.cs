using NMTools.App.Services;

namespace NMTools.Core.Tests;

public sealed class ClipboardWriteRetryScheduleTests
{
    [Fact]
    public void MaxAttempts_IsDelayCountPlusFirstAttempt()
    {
        Assert.Equal(
            ClipboardWriteRetrySchedule.DelaysMilliseconds.Count + 1,
            ClipboardWriteRetrySchedule.MaxAttempts);
    }

    [Fact]
    public void TotalRetryDelay_StaysSmall()
    {
        // 外部退避只是补充；单次尝试本身约 1 秒，因此退避合计必须保持很小，
        // 否则会无谓拉长用户等待。
        Assert.Equal(300, ClipboardWriteRetrySchedule.TotalBudgetMilliseconds);
    }

    [Fact]
    public void EstimatedWorstCase_StaysBelowFiveSeconds()
    {
        // 用户提交后最坏等待必须可控：占用 2 秒左右的竞争能救回，
        // 但不能让人等上七八秒才看到失败提示。
        var worst = ClipboardWriteRetrySchedule.EstimatedWorstCaseMilliseconds;
        Assert.InRange(worst, 3000, 5000);
        Assert.Equal(4300, worst);
    }

    [Fact]
    public void Delays_AreStrictlyIncreasing()
    {
        var delays = ClipboardWriteRetrySchedule.DelaysMilliseconds;
        for (var i = 1; i < delays.Count; i++)
        {
            Assert.True(
                delays[i] > delays[i - 1],
                $"退避序列必须严格递增，但第 {i} 项 {delays[i]} 未大于第 {i - 1} 项 {delays[i - 1]}。");
        }
    }

    [Fact]
    public void GetDelayAfterAttempt_ReturnsExpectedDelays()
    {
        Assert.Equal(50, ClipboardWriteRetrySchedule.GetDelayAfterAttempt(1));
        Assert.Equal(100, ClipboardWriteRetrySchedule.GetDelayAfterAttempt(2));
        Assert.Equal(150, ClipboardWriteRetrySchedule.GetDelayAfterAttempt(3));
    }

    [Fact]
    public void GetDelayAfterAttempt_ReturnsZeroForLastAttempt()
    {
        // 最后一次尝试失败后不应再等待，否则用户要额外白等一段时间才看到提示。
        Assert.Equal(
            0,
            ClipboardWriteRetrySchedule.GetDelayAfterAttempt(ClipboardWriteRetrySchedule.MaxAttempts));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(999)]
    public void GetDelayAfterAttempt_ReturnsZeroForOutOfRange(int attempt)
    {
        Assert.Equal(0, ClipboardWriteRetrySchedule.GetDelayAfterAttempt(attempt));
    }

    [Fact]
    public void AllDelays_ArePositive()
    {
        Assert.All(
            ClipboardWriteRetrySchedule.DelaysMilliseconds,
            delay => Assert.True(delay > 0, "退避间隔必须为正数。"));
    }
}
