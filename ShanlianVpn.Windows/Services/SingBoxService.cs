using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using ShanlianVpn.Windows.Models;

namespace ShanlianVpn.Windows.Services;

public sealed class SingBoxService
{
    private Process? _process;

    public bool IsRunning => _process is { HasExited: false };

    public async Task StartAsync(string configPath)
    {
        if (!File.Exists(AppPaths.SingBoxExePath))
        {
            SafeLogger.Error("missing_sing_box");
            throw new ApiException("缺少 VPN 核心文件，请联系客服", errorCode: "missing_sing_box");
        }

        if (!IsAdministrator())
        {
            SafeLogger.Error("not_admin");
            throw new ApiException("请以管理员身份运行闪连 VPN", errorCode: "not_admin");
        }

        if (IsRunning)
        {
            return;
        }

        SafeLogger.Info("connect_start");

        var startInfo = new ProcessStartInfo
        {
            FileName = AppPaths.SingBoxExePath,
            Arguments = $"run -c \"{configPath}\"",
            WorkingDirectory = Path.GetDirectoryName(AppPaths.SingBoxExePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };

        if (!_process.Start())
        {
            throw new ApiException("线路连接失败，请切换线路重试", errorCode: "sing_box_start_failed");
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        await Task.Delay(1500);
        if (_process.HasExited)
        {
            SafeLogger.Error("sing_box_exited");
            throw new ApiException("线路连接失败，请切换线路重试", errorCode: "sing_box_exited");
        }

        SafeLogger.Info("sing_box_started");
    }

    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        finally
        {
            _process?.Dispose();
            _process = null;
            SafeLogger.Info("disconnect_success");
        }
    }

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
