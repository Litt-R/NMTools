namespace NMTools.App.Infrastructure;

/// <summary>
/// 诊断日志抽象。实现必须保证记录内容不包含用户输入正文、剪贴板内容或远程凭据，
/// 并且任何写入失败都不得影响输入桥接主流程。
/// </summary>
public interface IDiagnosticLog
{
    bool IsEnabled { get; }

    void Info(string eventName, string message);

    void Warn(string eventName, string message);

    void Error(string eventName, string message, Exception? exception = null);

    /// <summary>
    /// 记录事件耗时。用于定位"哪一步慢"，不记录任何内容字段。
    /// </summary>
    void Timing(string eventName, long elapsedMilliseconds);

    void Flush();
}
