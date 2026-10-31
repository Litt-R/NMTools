namespace NMTools.App.Services;

/// <summary>
/// 剪贴板写入的重试节奏。
///
/// 关键事实（实测得出，不要凭直觉改）：<c>System.Windows.Clipboard.SetText</c> 内部
/// 自带约 1000 ms 的重试循环，只有在剪贴板持续被占用约 1 秒以上时才会抛出
/// <c>CLIPBRD_E_CANT_OPEN</c>。因此每一次"尝试"本身的代价约 1 秒，外部退避间隔
/// 相对而言很小。
///
/// 由此得到两个结论：
/// 1. 增加尝试次数会按"约 1 秒 × 次数"线性拉长用户等待，不能随意堆次数；
/// 2. 真正需要控制的是**总时间预算**，而不是退避间隔。
///
/// 当前配置：4 次尝试，外部退避合计 300 ms，最坏情况约 4 × 1 s + 0.3 s ≈ 4.3 s。
/// 这个上限是有意设的：占用 2 秒左右的竞争仍能自动恢复，而用户不会因为一次
/// 提交等上七八秒才看到结果。
///
/// 数组的每个元素是"下一次尝试之前"的等待毫秒数：
/// 尝试 1 → 等 Delays[0] → 尝试 2 → 等 Delays[1] → ... → 尝试 N。
/// 因此实际尝试次数为 Delays.Count + 1。
/// </summary>
public static class ClipboardWriteRetrySchedule
{
    /// <summary>
    /// 单次 <c>Clipboard.SetText</c> 在剪贴板被持续占用时的大致阻塞时长。
    /// 仅用于估算总耗时和生成用户提示，不作为精确测量值。
    /// </summary>
    public const int ApproximatePerAttemptBlockMilliseconds = 1000;

    public static readonly IReadOnlyList<int> DelaysMilliseconds = new[]
    {
        50, 100, 150,
    };

    /// <summary>
    /// 含首次尝试在内的总尝试次数。
    /// </summary>
    public static int MaxAttempts => DelaysMilliseconds.Count + 1;

    /// <summary>
    /// 完整走完所有重试时，外部退避累计等待的总毫秒数（不含每次尝试自身的阻塞）。
    /// </summary>
    public static int TotalBudgetMilliseconds => DelaysMilliseconds.Sum();

    /// <summary>
    /// 最坏情况下用户需要等待的估算总时长：每次尝试的内部阻塞加外部退避。
    /// </summary>
    public static int EstimatedWorstCaseMilliseconds =>
        MaxAttempts * ApproximatePerAttemptBlockMilliseconds + TotalBudgetMilliseconds;

    /// <summary>
    /// 第 <paramref name="attempt"/> 次尝试失败后应等待的毫秒数；已是最后一次尝试时返回 0。
    /// </summary>
    public static int GetDelayAfterAttempt(int attempt)
    {
        if (attempt < 1 || attempt >= MaxAttempts)
        {
            return 0;
        }

        return DelaysMilliseconds[attempt - 1];
    }
}
