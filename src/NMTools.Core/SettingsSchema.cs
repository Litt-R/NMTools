namespace NMTools.Core;

/// <summary>
/// 设置文件的版本策略。
///
/// 这里定义"哪些版本能被读"和"当前写哪个版本"，具体的字段迁移由
/// <c>AppSettingsStore</c> 按版本逐级执行。把它放在 Core 是为了能用单元测试
/// 锁住兼容范围：新增字段时只需要改 <see cref="CurrentVersion"/> 并加一条迁移步骤，
/// 不会再出现"忘记把新版本号加进白名单导致用户设置被当成损坏文件丢掉"。
/// </summary>
public static class SettingsSchema
{
    /// <summary>当前写入的版本号。每次改变持久化结构就加 1。</summary>
    public const int CurrentVersion = 7;

    /// <summary>能够读取并迁移的最早版本号。</summary>
    public const int OldestSupportedVersion = 1;

    /// <summary>
    /// 是否是可读取的版本。范围是 [OldestSupportedVersion, CurrentVersion] 的闭区间，
    /// 高于当前版本的文件来自更新版本的程序，不能读取（否则会丢字段），
    /// 低于最早版本的文件已经没有迁移路径。
    /// </summary>
    public static bool IsSupported(int version) =>
        version >= OldestSupportedVersion && version <= CurrentVersion;

    /// <summary>
    /// 该版本是否已有粘贴方式槽位（版本 5 引入）。更早的版本需要补全套默认绑定。
    /// </summary>
    public static bool HasPasteBindings(int version) => version >= 5;

    /// <summary>该版本的自定义槽位使用旧模型，需要迁移成自定义输入（仅版本 5）。</summary>
    public static bool NeedsLegacyCustomMigration(int version) => version == 5;

    /// <summary>该版本是否包含更新检查开关（版本 7 引入）。</summary>
    public static bool HasUpdateSettings(int version) => version >= 7;
}
