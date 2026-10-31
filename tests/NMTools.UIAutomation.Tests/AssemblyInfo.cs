using Xunit;

// 桌面 UI 测试共用同一个键盘、鼠标和前台窗口，不能并行运行。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
