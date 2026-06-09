using System.Reflection;
using System.Text;

namespace ShanlianVpn.Windows.Services;

public static class DiagnosticsService
{
    public static string BuildSafeDiagnostics()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"App 版本：{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"}");
        builder.AppendLine($"Windows 版本：{Environment.OSVersion.VersionString}");
        builder.AppendLine($"当前状态：{AppState.ConnectionStatus}");
        builder.AppendLine($"当前线路：{AppState.SelectedNode?.DisplayCountry ?? "未选择"}");
        builder.AppendLine($"最近错误码：{(string.IsNullOrWhiteSpace(AppState.LastErrorCode) ? "--" : AppState.LastErrorCode)}");
        builder.AppendLine($"失败阶段：{(string.IsNullOrWhiteSpace(AppState.LastErrorStage) ? "--" : AppState.LastErrorStage)}");
        builder.AppendLine($"设备短码：{AppState.DeviceShortCode}");
        builder.AppendLine($"订阅状态：{(AppState.Subscription?.IsActive == true ? "有效" : "不可用")}");
        return builder.ToString();
    }
}
