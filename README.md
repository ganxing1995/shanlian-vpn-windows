# 闪连 VPN Windows 客户端

Windows 桌面 MVP 客户端，技术栈为 .NET 8 + WPF，复用现有 Laravel API：

`https://api.lianshu.shop`

第一阶段重点是跑通登录、设备绑定、订阅检查、节点选择、生成 sing-box runtime config、启动/停止 VPN。

## 快速开始

1. 安装 .NET 8 SDK。
2. 按 [docs/SETUP.md](docs/SETUP.md) 放置 `sing-box.exe`。
3. 检查本机环境：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\check-env.ps1
```

4. 使用 Visual Studio 2022 或执行：

```powershell
dotnet build .\ShanlianVpn.Windows.sln
dotnet run --project .\ShanlianVpn.Windows\ShanlianVpn.Windows.csproj
```

连接 TUN 需要管理员权限，请以管理员身份启动客户端。
