$ErrorActionPreference = "Continue"

$allowedBlockers = @(
    "hysteria2_outbound_failed",
    "sing_box_exited",
    "sing_box_start_failed",
    "tun_adapter_missing",
    "server_unreachable",
    "handshake_failed",
    "auth_password_wrong",
    "tls_or_sni_failed",
    "tun_permission_failed",
    "route_failed",
    "dns_failed",
    "internet_check_failed",
    "sing_box_config_invalid",
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
    if ($lower.Contains("hysteria2_outbound_failed") -or $lower.Contains("proxy_preflight_failed")) { return "hysteria2_outbound_failed" }
    if ($lower.Contains("sing_box_start_failed")) { return "sing_box_start_failed" }
    if ($lower.Contains("authentication failed") -or $lower.Contains("unauthorized")) { return "auth_password_wrong" }
    if ($lower.Contains("sing_box_exited") -or $lower.Contains("sing-box exited") -or $lower.Contains("sing_box_exit")) { return "sing_box_exited" }
    if ($lower.Contains("tls handshake") -or $lower.Contains("certificate") -or $lower.Contains("server name") -or $lower.Contains("sni")) { return "tls_or_sni_failed" }
    if ($lower.Contains("handshake failed")) { return "handshake_failed" }
    if ($lower.Contains("permission denied") -or $lower.Contains("access is denied") -or $lower.Contains("wintun")) { return "tun_permission_failed" }
    if ($lower.Contains("route add failed") -or $lower.Contains("network unreachable")) { return "route_failed" }
    if ($lower.Contains("timeout") -or $lower.Contains("unreachable") -or $lower.Contains("no route to host") -or $lower.Contains("context deadline exceeded")) { return "server_unreachable" }
    if ($lower.Contains("dns_failed") -or $lower.Contains("dns failed") -or $lower.Contains("network解析") -or $lower.Contains("解析异常")) { return "dns_failed" }
    if ($lower.Contains("internet_check_failed") -or $lower.Contains("network_unavailable")) { return "internet_check_failed" }
    if ($lower.Contains("sing_box_config_invalid") -or $lower.Contains("config_invalid") -or $lower.Contains("decode config") -or $lower.Contains("invalid config")) { return "sing_box_config_invalid" }
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

function Get-DiagProperty {
    param(
        [object]$Object,
        [string]$Name
    )

    if ($null -eq $Object) {
        return ""
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return ""
    }

    return $property.Value
}

function Test-TunText {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $false
    }

    return $Text -match "Shanlian|Wintun|sing-box|Tunnel|Meta"
}

function Test-ResolveName {
    param([string]$Name)
    try {
        $resolved = Resolve-DnsName $Name -ErrorAction Stop | Select-Object -First 4 Name,Type,IPAddress
        Write-Host "resolve_${Name}=success"
        $resolved | Format-Table -AutoSize
        return $true
    } catch {
        Write-Host "resolve_${Name}=failed"
        return $false
    }
}

function Test-HttpsEndpoint {
    param(
        [string]$Name,
        [string]$Uri
    )

    try {
        $response = Invoke-WebRequest -Uri $Uri -TimeoutSec 10 -UseBasicParsing
        Write-Host "${Name}=HTTP $($response.StatusCode)"
        return $true
    } catch {
        Write-Host "${Name}=failed"
        return $false
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$runtimeConfig = Join-Path $env:APPDATA "ShanlianVPN\runtime-config.json"
$clientLog = Join-Path $env:APPDATA "ShanlianVPN\client.log"
$sessionPath = Join-Path $env:APPDATA "ShanlianVPN\sing-box-session.json"
$connectionDiagnosticsPath = Join-Path $env:APPDATA "ShanlianVPN\connection-diagnostics.json"
$singBoxExe = Join-Path $repoRoot "tools\sing-box\windows-amd64\sing-box.exe"
$blocker = "unknown_error"

Write-Host "Shanlian VPN Windows safe diagnostics"
$isAdmin = Test-Administrator
Write-Host "administrator=$isAdmin"

Write-Host "runtime_config_exists=$(Test-Path -LiteralPath $runtimeConfig)"

Write-Section "connection diagnostics"
$connectionDiagnostics = $null
if (Test-Path -LiteralPath $connectionDiagnosticsPath) {
    try {
        $connectionDiagnostics = Get-Content -LiteralPath $connectionDiagnosticsPath -Raw | ConvertFrom-Json
        Write-Host "latest_session_id=$($connectionDiagnostics.latest_session_id)"
        Write-Host "latest_profile=$($connectionDiagnostics.latest_profile)"
        Write-Host "successful_profile=$($connectionDiagnostics.successful_profile)"
        Write-Host "proxy_preflight_success=$($connectionDiagnostics.proxy_preflight_success)"
        Write-Host "proxy_preflight_blocker=$($connectionDiagnostics.proxy_preflight_blocker)"
        Write-Host "proxy_preflight_detail=$($connectionDiagnostics.proxy_preflight_detail)"
        Write-Host "tun_success=$($connectionDiagnostics.tun_success)"
        Write-Host "final_blocker=$($connectionDiagnostics.final_blocker)"
        foreach ($profileName in @("A", "B", "C")) {
            $check = Get-DiagProperty $connectionDiagnostics "profile_${profileName}_check"
            $start = Get-DiagProperty $connectionDiagnostics "profile_${profileName}_start"
            $tunState = Get-DiagProperty $connectionDiagnostics "profile_${profileName}_tun"
            $routeState = Get-DiagProperty $connectionDiagnostics "profile_${profileName}_route"
            $dnsState = Get-DiagProperty $connectionDiagnostics "profile_${profileName}_dns"
            $httpsState = Get-DiagProperty $connectionDiagnostics "profile_${profileName}_https"
            Write-Host "profile_${profileName}_check=$check"
            Write-Host "profile_${profileName}_start=$start"
            Write-Host "profile_${profileName}_tun=$tunState"
            Write-Host "profile_${profileName}_route=$routeState"
            Write-Host "profile_${profileName}_dns=$dnsState"
            Write-Host "profile_${profileName}_https=$httpsState"
        }

        $diagBlocker = [string]$connectionDiagnostics.final_blocker
        if ([string]::IsNullOrWhiteSpace($diagBlocker) -or $diagBlocker -eq "none") {
            $diagBlocker = [string]$connectionDiagnostics.proxy_preflight_blocker
        }

        if (-not [string]::IsNullOrWhiteSpace($diagBlocker) -and $diagBlocker -ne "none") {
            Set-Blocker $diagBlocker
        }
    } catch {
        Write-Host "connection_diagnostics_read_failed"
    }
} else {
    Write-Host "connection_diagnostics_missing"
}

Write-Section "last sing-box session"
$session = $null
if (Test-Path -LiteralPath $sessionPath) {
    try {
        $session = Get-Content -LiteralPath $sessionPath -Raw | ConvertFrom-Json
        Write-Host "session_id=$($session.session_id)"
        Write-Host "last_pid=$($session.pid)"
        Write-Host "state=$($session.state)"
        Write-Host "start_time=$($session.start_time)"
        Write-Host "exit_time=$($session.exit_time)"
        Write-Host "exit_code=$($session.exit_code)"
        Write-Host "last_stdout_summary=$(Sanitize-Text ([string]$session.stdout_summary))"
        Write-Host "last_stderr_summary=$(Sanitize-Text ([string]$session.stderr_summary))"
        $sessionBlocker = Classify-Text ([string]$session.combined_summary)
        if (-not [string]::IsNullOrWhiteSpace($sessionBlocker)) {
            Set-Blocker $sessionBlocker
        }
    } catch {
        Write-Host "session_read_failed"
    }
} else {
    Write-Host "session_missing"
}

Write-Section "sing-box process"
$singBox = Get-Process sing-box -ErrorAction SilentlyContinue
if ($singBox) {
    $singBox | Select-Object Id, ProcessName, StartTime | Format-Table -AutoSize
} else {
    Write-Host "not running"
    if ($session -and $session.state -eq "exited") {
        Write-Host "process_exited=True"
        Write-Host "exit_code=$($session.exit_code)"
        Set-Blocker "sing_box_exited"
    }
}

Write-Section "recent safe log"
if (Test-Path -LiteralPath $clientLog) {
    $recentLines = @(Get-Content -LiteralPath $clientLog -Tail 160)
    $safeLog = Sanitize-Text ($recentLines | Out-String)
    Write-Host $safeLog
    if ($session) {
        $sessionStart = [DateTimeOffset]::MinValue
        if ([DateTimeOffset]::TryParse([string]$session.start_time, [ref]$sessionStart)) {
            $recentLines = @($recentLines | Where-Object {
                if ($_ -match '^(\S+)') {
                    $lineTime = [DateTimeOffset]::MinValue
                    return [DateTimeOffset]::TryParse($Matches[1], [ref]$lineTime) -and $lineTime -ge $sessionStart
                }

                return $false
            })
        }

        $logBlocker = Get-RecentLogBlocker $recentLines
        if (-not [string]::IsNullOrWhiteSpace($logBlocker)) {
            Set-Blocker $logBlocker
        }
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
        Set-Blocker "sing_box_config_invalid"
    }
} else {
    Write-Host "sing_box_check_skipped"
    Set-Blocker "sing_box_config_invalid"
}

Write-Section "tun adapter"
try {
    $tun = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -like "*Shanlian*" `
            -or $_.Name -match "Wintun|sing-box|Tunnel|Meta|utun" `
            -or $_.InterfaceDescription -match "Wintun|sing-box|Tunnel|Meta|utun"
    }
    if ($tun) {
        $tun | Select-Object Name, Status, ifIndex, InterfaceDescription | Format-Table -AutoSize
    } else {
        Write-Host "tun_adapter_missing"
        if ($singBox) {
            Set-Blocker "tun_adapter_missing"
        }
    }
} catch {
    Write-Host "tun_adapter_check_failed"
}

Write-Section "default route"
try {
    $routes = Get-NetRoute -DestinationPrefix "0.0.0.0/0" | Sort-Object RouteMetric | Select-Object DestinationPrefix, ifIndex, InterfaceAlias, NextHop, RouteMetric
    $routes | Format-Table -AutoSize
    $routeText = $routes | Out-String
    $routeSwitched = Test-TunText $routeText
    Write-Host "default_route_switched=$routeSwitched"
    if ($singBox -and -not $routeSwitched) {
        Set-Blocker "route_failed"
    }
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

Write-Section "resolve dns"
$dnsOk = $false
foreach ($name in @("example.com", "cloudflare.com", "google.com")) {
    if (Test-ResolveName $name) {
        $dnsOk = $true
    }
}

if (-not $dnsOk) {
    Set-Blocker "dns_failed"
}

Write-Section "https check"
$httpsOk = $false
if (Test-HttpsEndpoint "google_generate_204" "https://www.google.com/generate_204") {
    $httpsOk = $true
}

if (Test-HttpsEndpoint "cloudflare_trace" "https://cloudflare.com/cdn-cgi/trace") {
    $httpsOk = $true
}

if (-not $httpsOk) {
    Write-Host "https_check_success=False"
    Set-Blocker "internet_check_failed"
} else {
    Write-Host "https_check_success=True"
}

Write-Section "safe result"
Write-Host "BLOCKER=$blocker"
