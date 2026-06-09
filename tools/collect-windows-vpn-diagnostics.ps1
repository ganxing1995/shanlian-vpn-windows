$ErrorActionPreference = "Continue"

function Write-Section {
    param([string]$Name)
    Write-Host ""
    Write-Host "== $Name =="
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

$runtimeConfig = Join-Path $env:APPDATA "ShanlianVPN\runtime-config.json"
$blocker = "unknown_error"

Write-Host "Shanlian VPN Windows safe diagnostics"
Write-Host "administrator=$(Test-Administrator)"
Write-Host "runtime_config_exists=$(Test-Path -LiteralPath $runtimeConfig)"

Write-Section "sing-box process"
$singBox = Get-Process sing-box -ErrorAction SilentlyContinue
if ($singBox) {
    $singBox | Select-Object Id, ProcessName, StartTime | Format-Table -AutoSize
} else {
    Write-Host "not running"
    $blocker = "sing_box_start_failed"
}

Write-Section "default route"
try {
    Get-NetRoute -DestinationPrefix "0.0.0.0/0" | Sort-Object RouteMetric | Select-Object ifIndex, InterfaceAlias, NextHop, RouteMetric | Format-Table -AutoSize
} catch {
    route print 0.0.0.0
}

Write-Section "dns servers"
try {
    Get-DnsClientServerAddress -AddressFamily IPv4 | Select-Object InterfaceAlias, ServerAddresses | Format-Table -AutoSize
} catch {
    ipconfig /all
}

Write-Section "ping 1.1.1.1"
try {
    $ping = Test-Connection 1.1.1.1 -Count 2 -Quiet
    Write-Host "ping_1_1_1_1=$ping"
    if (-not $ping -and $blocker -eq "unknown_error") {
        $blocker = "internet_check_failed"
    }
} catch {
    Write-Host "ping_1_1_1_1=false"
    if ($blocker -eq "unknown_error") {
        $blocker = "internet_check_failed"
    }
}

Write-Section "nslookup example.com"
try {
    nslookup example.com
} catch {
    Write-Host "nslookup_failed"
    $blocker = "dns_failed"
}

Write-Section "https check"
try {
    $response = Invoke-WebRequest -Uri "https://www.google.com/generate_204" -TimeoutSec 10 -UseBasicParsing
    Write-Host "google_generate_204=HTTP $($response.StatusCode)"
} catch {
    try {
        $response = Invoke-WebRequest -Uri "https://cloudflare.com/cdn-cgi/trace" -TimeoutSec 10 -UseBasicParsing
        Write-Host "cloudflare_trace=HTTP $($response.StatusCode)"
    } catch {
        Write-Host "https_check_failed"
        if ($blocker -eq "unknown_error") {
            $blocker = "internet_check_failed"
        }
    }
}

Write-Section "safe result"
if ($blocker -eq "unknown_error") {
    Write-Host "BLOCKER=none"
} else {
    Write-Host "BLOCKER=$blocker"
}

