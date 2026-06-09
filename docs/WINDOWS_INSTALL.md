# Windows 安装包

## 当前可交付方式

先生成 Release 发布目录：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\build-release.ps1
```

发布目录：

```text
D:\Projects\shanlian-vpn-windows\publish
```

用户可双击：

```text
D:\Projects\shanlian-vpn-windows\publish\闪连VPN.exe
```

## 生成安装包

安装 Inno Setup 6 后执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\build-installer.ps1
```

安装包输出：

```text
D:\Projects\shanlian-vpn-windows\installer\output\闪连VPN-1.0.0-setup.exe
```

Inno Setup 官方下载：

```text
https://jrsoftware.org/isdl.php
```

## 安装包内容

- `闪连VPN.exe`
- `tools\sing-box\windows-amd64\sing-box.exe`
- .NET 自包含运行依赖
- `Resources\logo.svg`
- 桌面快捷方式：`闪连 VPN`
- 开始菜单快捷方式：`闪连 VPN`
- Windows 卸载程序

## 管理员权限

Windows TUN 需要管理员权限。普通方式打开 App 后，首次连接会提示：

```text
请以管理员身份运行闪连 VPN
```

用户可点击 App 内的：

```text
以管理员身份重启
```

