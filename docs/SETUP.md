# SETUP

## 环境

- Windows 10/11 x64
- .NET 8 SDK
- Visual Studio 2022，安装 `.NET desktop development` 工作负载

## sing-box 核心

本项目使用 `sing-box.exe` 作为 Windows 连接核心。

请将 Windows amd64 版本放到：

```text
tools/sing-box/windows-amd64/sing-box.exe
```

`sing-box.exe` 默认不提交到 Git。构建或发布时，如果该文件存在，项目会自动复制到输出目录：

```text
ShanlianVpn.Windows/bin/<Configuration>/net8.0-windows/tools/sing-box/windows-amd64/sing-box.exe
```

## 环境检查

执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\check-env.ps1
```

检查项包括：

- .NET SDK 是否可用
- `sing-box.exe` 是否存在
- 当前 PowerShell 是否管理员运行
- API 域名是否可访问
- `%AppData%\ShanlianVPN` 是否可写

## 运行时文件

客户端运行时生成：

```text
%AppData%\ShanlianVPN\runtime-config.json
%AppData%\ShanlianVPN\auth.dat
%AppData%\ShanlianVPN\client.log
```

说明：

- `runtime-config.json` 包含节点连接敏感信息，只在本机生成，不提交 Git。
- `auth.dat` 使用当前 Windows 用户 DPAPI 加密保存 token。
- `client.log` 只记录安全事件码，不记录 token、密码、完整 device_id、raw config。

## 管理员权限

Windows TUN 模式需要管理员权限。若未以管理员身份运行，点击连接会提示：

```text
请以管理员身份运行闪连 VPN
```
