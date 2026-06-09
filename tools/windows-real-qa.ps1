param(
    [switch]$LaunchApp
)

$ErrorActionPreference = "Stop"

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

$repoRoot = Get-RepoRoot
$checkEnv = Join-Path $repoRoot "tools\check-env.ps1"
$buildDebug = Join-Path $repoRoot "tools\build-debug.ps1"
$debugExe = Join-Path $repoRoot "ShanlianVpn.Windows\bin\Debug\net8.0-windows\ShanlianVpn.Windows.exe"
$runtimeConfig = Join-Path $env:APPDATA "ShanlianVPN\runtime-config.json"
$authData = Join-Path $env:APPDATA "ShanlianVPN\auth.dat"

Write-Host "Windows real QA helper"
Write-Host "Repo root: $repoRoot"
Write-Host ""

& powershell.exe -ExecutionPolicy Bypass -File $checkEnv
& powershell.exe -ExecutionPolicy Bypass -File $buildDebug

Write-Host ""
Write-Host "Manual QA checklist:"
Write-Host "1. Login with an existing test account."
Write-Host "2. Confirm user info, subscription, device register, devices, and nodes load."
Write-Host "3. Select a US line."
Write-Host "4. Connect only from an Administrator session."
Write-Host "5. Confirm process running, DNS example.com, HTTPS check, and browser access."
Write-Host "6. Disconnect and confirm network recovery."
Write-Host ""
Write-Host "Sensitive runtime files should stay local:"
Write-Host "runtime-config exists: $(Test-Path -LiteralPath $runtimeConfig)"
Write-Host "auth.dat exists: $(Test-Path -LiteralPath $authData)"

if ($LaunchApp) {
    if (-not (Test-Administrator)) {
        throw "LaunchApp requires Administrator PowerShell. Use tools\run-dev-admin.ps1."
    }

    Write-Host "Launching app: $debugExe"
    Start-Process -FilePath $debugExe -WorkingDirectory (Split-Path -Parent $debugExe)
}

