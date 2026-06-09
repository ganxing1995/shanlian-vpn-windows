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
$singBoxExe = Join-Path $repoRoot "tools\sing-box\windows-amd64\sing-box.exe"
$blocker = "unknown_error"

Write-Host "Windows real QA helper"
Write-Host "Repo root: $repoRoot"
Write-Host ""

& powershell.exe -ExecutionPolicy Bypass -File $checkEnv
& powershell.exe -ExecutionPolicy Bypass -File $buildDebug

Write-Host ""
if (-not (Test-Administrator)) {
    Write-Host "BLOCKER=not_admin"
    Write-Host "Please run tools\run-dev-admin.ps1, then click Connect in the app."
    exit 13
}

if ($LaunchApp -or -not (Get-Process ShanlianVpn.Windows -ErrorAction SilentlyContinue)) {
    Write-Host "Launching app: $debugExe"
    Start-Process -FilePath $debugExe -WorkingDirectory (Split-Path -Parent $debugExe)
}

Write-Host "Please login if needed, select a US line, then click Connect in the app."
Read-Host "Press Enter after the app shows a connection result"

$singBox = Get-Process sing-box -ErrorAction SilentlyContinue
if ($singBox) {
    Write-Host "sing_box_process=running"
} else {
    Write-Host "sing_box_process=not_running"
    $blocker = "sing_box_start_failed"
}

if (Test-Path -LiteralPath $runtimeConfig) {
    Write-Host "runtime_config=exists"
    if (Test-Path -LiteralPath $singBoxExe) {
        $check = Start-Process -FilePath $singBoxExe -ArgumentList @("check", "-c", $runtimeConfig) -NoNewWindow -PassThru -Wait
        if ($check.ExitCode -eq 0) {
            Write-Host "sing_box_check=OK"
        } else {
            Write-Host "sing_box_check=FAILED"
            $blocker = "sing_box_config_invalid"
        }
    }
} else {
    Write-Host "runtime_config=missing"
    $blocker = "config_generate_failed"
}

try {
    [System.Net.Dns]::GetHostAddresses("example.com") | Out-Null
    Write-Host "dns_check=OK"
} catch {
    Write-Host "dns_check=FAILED"
    $blocker = "dns_failed"
}

try {
    $response = Invoke-WebRequest -Uri "https://cloudflare.com/cdn-cgi/trace" -TimeoutSec 10 -UseBasicParsing
    Write-Host "internet_check=OK HTTP $($response.StatusCode)"
} catch {
    Write-Host "internet_check=FAILED"
    if ($blocker -eq "unknown_error") {
        $blocker = "internet_check_failed"
    }
}

if ($blocker -eq "unknown_error" -and $singBox) {
    Write-Host "BLOCKER=none"
} else {
    Write-Host "BLOCKER=$blocker"
}
Write-Host ""
Write-Host "Sensitive runtime files should stay local:"
Write-Host "runtime-config exists: $(Test-Path -LiteralPath $runtimeConfig)"
Write-Host "auth.dat exists: $(Test-Path -LiteralPath $authData)"
