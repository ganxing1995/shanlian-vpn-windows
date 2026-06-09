# RELEASE

## 发布命令

安装 .NET 8 SDK 后执行：

```powershell
dotnet publish .\ShanlianVpn.Windows\ShanlianVpn.Windows.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false
```

发布目录：

```text
ShanlianVpn.Windows/bin/Release/net8.0-windows/win-x64/publish
```

## 发布包检查

发布包必须包含：

```text
ShanlianVpn.Windows.exe
tools/sing-box/windows-amd64/sing-box.exe
```

## 验收步骤

1. 以管理员身份启动 `ShanlianVpn.Windows.exe`。
2. 使用现有账号登录。
3. 确认订阅页能显示套餐、状态、到期时间和设备数量。
4. 确认设备注册成功，账号页只显示设备短码。
5. 在线路页选择美国或日本线路。
6. 点击连接，确认生成 `%AppData%\ShanlianVPN\runtime-config.json`。
7. 确认 sing-box 进程启动。
8. 浏览器访问网页，确认网络可用。
9. 点击断开连接，确认 sing-box 进程停止且系统网络恢复。

## 安全检查

发布前确认：

- Git 不包含 `sing-box.exe` 以外的本机运行时文件。
- Git 不包含 token、密码、节点密码、完整 device_id。
- 日志不输出 raw config。
- UI 不展示 host、port、auth_password、node id、协议名或 sing-box 技术日志。

