using Xunit;

namespace NMTools.UIAutomation.Tests;

[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresNmToolsAppFactAttribute : FactAttribute
{
    public RequiresNmToolsAppFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NMTOOLS_APP_PATH")))
        {
            Skip = "Set NMTOOLS_APP_PATH to a built NMTools.App.exe to run desktop UI automation.";
        }
    }
}
