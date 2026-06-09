$ErrorActionPreference = "Continue"

$allowedBlockers = @(
    "not_admin",
    "sing_box_not_running",
    "server_unreachable",
    "handshake_failed",
    "auth_password_wrong",
    "tls_or_sni_failed",
    "tun_permission_failed",
    "route_failed",
    "dns_failed",
    "internet_check_failed",
    "config_invalid",
    "unknown_error"
)

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

function Sanitize-Text {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return "--"
    }

    $safe = $Value -replace '(?i)(password|auth_password|token|authorization|configJson|raw_config)(\s*[=:]\s*)\S+', '$1$2[redacted]'
    $safe = $safe -replace '(?i)(check|run)\s+-c\s+"?[^"\r\n]+"?', '$1 -c [path]'
    $safe = $safe -replace '[A-Za-z0-9+/=]{32,}', '[redacted]'
    $safe = $safe -replace '\s+', ' '
    if ($safe.Length -gt 1000) {
        return $safe.Substring(0, 1000)
    }

    return $safe
}

function Classify-Text {
    param([string]$Text)
    $lower = $Text.ToLowerInvariant()
    if ($lower.Contains("authentication failed") -or $lower.Contains("unauthorized")) { return "auth_password_wrong" }
    if ($lower.Contains("tls handshake") -or $lower.Contains("certificate") -or $lower.Contains("server name") -or $lower.Contains("sni")) { return "tls_or_sni_failed" }
    if ($lower.Contains("handshake failed")) { return "handshake_failed" }
    if ($lower.Contains("permission denied") -or $lower.Contains("access is denied") -or $lower.Contains("wintun")) { return "tun_permission_failed" }
    if ($lower.Contains("route add failed") -or $lower.Contains("network unreachable")) { return "route_failed" }
    if ($lower.Contains("timeout") -or $lower.Contains("unreachable") -or $lower.Contains("no route to host") -or $lower.Contains("context deadline exceeded")) { return "server_unreachable" }
    if ($lower.Contains("dns_failed") -or $lower.Contains("dns failed") -or $lower.Contains("network解析") -or $lower.Contains("解析异常")) { return "dns_failed" }
    if ($lower.Contains("internet_check_failed") -or $lower.Contains("network_unavailable")) { return "internet_check_failed" }
    if ($lower.Contains("sing_box_config_invalid") -or $lower.Contains("config_invalid") -or $lower.Contains("decode config") -or $lower.Contains("invalid config")) { return "config_invalid" }
    return ""
}

function Get-RecentLogBlocker {
    param([string[]]$Lines)
    for ($index = $Lines.Count - 1; $index -ge 0; $index--) {
        $line = $Lines[$index]
        if ($line -notmatch '\s(ERROR|DIAGNOSTIC)\s') {
            continue
        }

        $classified = Classify-Text $line
        if (-not [string]::IsNullOrWhiteSpace($classified)) {
            return $classified
        }
    }

    return ""
}

function Set-Blocker {
    param([string]$Candidate)
    if ($script:blocker -eq "unknown_error" -and $allowedBlockers -contains $Candidate) {
        $script:blocker = $Candidate
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$runtimeConfig = Join-Path $env:APPDATA "ShanlianVPN\runtime-config.json"
$clientLog = Join-Path $env:APPDATA "ShanlianVPN\client.log"
$singBoxExe = Join-Path $repoRoot "tools\sing-box\windows-amd64\sing-box.exe"
$blocker = "unknown_error"

Write-Host "Shanlian VPN Windows safe diagnostics"
$isAdmin = Test-Administrator
Write-Host "administrator=$isAdmin"
if (-not $isAdmin) {
    Set-Blocker "not_admin"
}

Write-Host "runtime_config_exists=$(Test-Path -LiteralPath $runtimeConfig)"

Write-Section "sing-box process"
$singBox = Get-Process sing-box -ErrorAction SilentlyContinue
if ($singBox) {
    $singBox | Select-Object Id, ProcessName, StartTime | Format-Table -AutoSize
} else {
    Write-Host "not running"
    Set-Blocker "sing_box_not_running"
}

Write-Section "recent safe log"
if (Test-Path -LiteralPath $clientLog) {
    $recentLines = @(Get-Content -LiteralPath $clientLog -Tail 160)
    $safeLog = Sanitize-Text ($recentLines | Out-String)
    Write-Host $safeLog
    $logBlocker = Get-RecentLogBlocker $recentLines
    if (-not [string]::IsNullOrWhiteSpace($logBlocker)) {
        Set-Blocker $logBlocker
    }
} else {
    Write-Host "client_log_missing"
}

Write-Section "sing-box check"
if ((Test-Path -LiteralPath $runtimeConfig) -and (Test-Path -LiteralPath $singBoxExe)) {
    $checkOutput = & $singBoxExe check -c $runtimeConfig 2>&1
    $checkExit = $LASTEXITCODE
    Write-Host "sing_box_check_exit=$checkExit"
    if ($checkExit -ne 0) {
        Write-Host (Sanitize-Text ($checkOutput | Out-String))
        Set-Blocker "config_invalid"
    }
} else {
    Write-Host "sing_box_check_skipped"
    Set-Blocker "config_invalid"
}

Write-Section "tun adapter"
try {
    $tun = Get-NetAdapter -Name "ShanlianVPN" -ErrorAction SilentlyContinue
    if ($tun) {
        $tun | Select-Object Name, Status, ifIndex, InterfaceDescription | Format-Table -AutoSize
    } else {
        Write-Host "tun_adapter_missing"
        if ($singBox) {
            Set-Blocker "tun_permission_failed"
        }
    }
} catch {
    Write-Host "tun_adapter_check_failed"
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
    if (-not $ping) {
        Set-Blocker "internet_check_failed"
    }
} catch {
    Write-Host "ping_1_1_1_1=false"
    Set-Blocker "internet_check_failed"
}

Write-Section "nslookup example.com"
try {
    $nslookup = nslookup example.com 2>&1
    $nsText = $nslookup | Out-String
    Write-Host (Sanitize-Text $nsText)
    $lowerNs = $nsText.ToLowerInvariant()
    $hasAnswer = $lowerNs.Contains("addresses:") -or $lowerNs.Contains("address:")
    if (($LASTEXITCODE -ne 0 -or $lowerNs.Contains("timed out")) -and -not $hasAnswer) {
        Set-Blocker "dns_failed"
    }
} catch {
    Write-Host "nslookup_failed"
    Set-Blocker "dns_failed"
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
        Set-Blocker "internet_check_failed"
    }
}

Write-Section "safe result"
Write-Host "BLOCKER=$blocker"
