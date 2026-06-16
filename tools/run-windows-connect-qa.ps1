param(
    [string]$ApiBaseUrl = "https://api.lianshu.shop",
    [switch]$KeepConnected
)

$ErrorActionPreference = "Stop"

$allowedBlockers = @(
    "hysteria2_outbound_failed",
    "server_unreachable",
    "auth_password_wrong",
    "tls_or_sni_failed",
    "handshake_failed",
    "tun_adapter_missing",
    "tun_permission_failed",
    "route_failed",
    "dns_failed",
    "internet_check_failed",
    "sing_box_exited",
    "sing_box_start_failed",
    "login_required",
    "auth_state_invalid",
    "qa_script_error",
    "network_conflict",
    "unknown_error"
)

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-ConflictText {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) {
        return $false
    }

    foreach ($line in ($Text -split "(`r`n|`n|`r)")) {
        $lower = $line.ToLowerInvariant()
        if ([string]::IsNullOrWhiteSpace($lower) -or $lower.Contains("shanlian") -or $lower.Contains("闪连")) {
            continue
        }

        if ($lower -match "wintun|wireguard|warp|openvpn|tailscale|anyconnect|tap|tun|clash|mihomo|v2ray|sing-box|zerotier") {
            return $true
        }
    }

    return $false
}

function Get-DevProxyPorts {
    $ports = @(7890, 7897, 10808, 10809)
    try {
        return @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue |
            Where-Object { $ports -contains $_.LocalPort -and ($_.LocalAddress -eq "127.0.0.1" -or $_.LocalAddress -eq "::1") } |
            Select-Object -ExpandProperty LocalPort -Unique |
            Sort-Object)
    } catch {
        return @()
    }
}

function Test-DevProxyProcess {
    try {
        return $null -ne (Get-Process -ErrorAction SilentlyContinue |
            Where-Object { $_.ProcessName -match "clash|mihomo|v2rayn|v2ray|sing-box" } |
            Select-Object -First 1)
    } catch {
        return $false
    }
}

function Test-SystemProxyEnabled {
    try {
        $value = Get-ItemPropertyValue -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings" -Name ProxyEnable -ErrorAction Stop
        return [int]$value -ne 0
    } catch {
        return $false
    }
}

function Get-NetworkEnvironment {
    $devPorts = @(Get-DevProxyPorts)
    $devProxyDetected = $devPorts.Count -gt 0 -or (Test-DevProxyProcess)
    $systemProxyEnabled = Test-SystemProxyEnabled

    $virtualAdapterConflict = $false
    try {
        $adapterText = Get-NetAdapter -ErrorAction SilentlyContinue |
            Where-Object { $_.Status -eq "Up" } |
            Select-Object Name,InterfaceDescription |
            Out-String
        $virtualAdapterConflict = Test-ConflictText $adapterText
    } catch {
    }

    $routeConflict = $false
    try {
        $routeText = Get-NetRoute -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue |
            Select-Object InterfaceAlias,NextHop,RouteMetric |
            Out-String
        $routeConflict = Test-ConflictText $routeText
    } catch {
    }

    $dnsConflict = $false
    try {
        $dnsText = Get-DnsClientServerAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.ServerAddresses.Count -gt 0 } |
            Select-Object InterfaceAlias,ServerAddresses |
            Out-String
        $dnsConflict = Test-ConflictText $dnsText
    } catch {
    }

    $otherSingBoxTun = $false
    try {
        $otherSingBoxTun = $null -ne (Get-Process sing-box -ErrorAction SilentlyContinue | Select-Object -First 1) -and ($virtualAdapterConflict -or $routeConflict)
    } catch {
    }

    $tunConflict = $virtualAdapterConflict -or $routeConflict -or $otherSingBoxTun
    $networkConflict = $tunConflict -or $virtualAdapterConflict -or $routeConflict -or $dnsConflict -or $otherSingBoxTun

    return [pscustomobject]@{
        DevProxyDetected = [bool]$devProxyDetected
        DevProxyPorts = @($devPorts)
        SystemProxyEnabled = [bool]$systemProxyEnabled
        TunConflictDetected = [bool]$tunConflict
        VirtualAdapterConflict = [bool]$virtualAdapterConflict
        RouteConflict = [bool]$routeConflict
        DnsConflict = [bool]$dnsConflict
        NetworkConflict = [bool]$networkConflict
    }
}

function Write-NetworkEnvironment {
    param([object]$Environment)
    Write-Host "dev_proxy_detected=$($Environment.DevProxyDetected)"
    Write-Host "dev_proxy_ports=$(@($Environment.DevProxyPorts) -join ',')"
    Write-Host "system_proxy_enabled=$($Environment.SystemProxyEnabled)"
    Write-Host "tun_conflict_detected=$($Environment.TunConflictDetected)"
    Write-Host "virtual_adapter_conflict=$($Environment.VirtualAdapterConflict)"
    Write-Host "route_conflict=$($Environment.RouteConflict)"
    Write-Host "dns_conflict=$($Environment.DnsConflict)"
    Write-Host "network_conflict=$($Environment.NetworkConflict)"
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
    $lower = ([string]$Text).ToLowerInvariant()
    if ($lower.Contains("auth_password_wrong") -or $lower.Contains("authentication failed") -or $lower.Contains("unauthorized")) { return "auth_password_wrong" }
    if ($lower.Contains("tls_or_sni_failed") -or $lower.Contains("tls handshake") -or $lower.Contains("certificate") -or $lower.Contains("server name") -or $lower.Contains("sni")) { return "tls_or_sni_failed" }
    if ($lower.Contains("handshake_failed") -or $lower.Contains("handshake failed")) { return "handshake_failed" }
    if ($lower.Contains("server_unreachable") -or $lower.Contains("timeout") -or $lower.Contains("unreachable") -or $lower.Contains("connection refused") -or $lower.Contains("no route to host") -or $lower.Contains("context deadline exceeded")) { return "server_unreachable" }
    if ($lower.Contains("permission denied") -or $lower.Contains("access is denied") -or $lower.Contains("administrator")) { return "tun_permission_failed" }
    if ($lower.Contains("route add failed") -or $lower.Contains("network unreachable")) { return "route_failed" }
    if ($lower.Contains("dns")) { return "dns_failed" }
    return "hysteria2_outbound_failed"
}

function Read-Token {
    param([string]$AuthDataPath)
    if (-not (Test-Path -LiteralPath $AuthDataPath)) {
        throw "login_required"
    }

    try {
        Add-Type -AssemblyName System.Security -ErrorAction SilentlyContinue
        $entropy = [Text.Encoding]::UTF8.GetBytes("ShanlianVPN.Windows.Token.v1")
        $bytes = [IO.File]::ReadAllBytes($AuthDataPath)
        $plain = [System.Security.Cryptography.ProtectedData]::Unprotect(
            $bytes,
            $entropy,
            [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        return [Text.Encoding]::UTF8.GetString($plain)
    } catch [System.Security.Cryptography.CryptographicException] {
        throw "auth_state_invalid"
    } catch [System.Management.Automation.RuntimeException] {
        throw "qa_script_error:$($_.Exception.Message)"
    } catch {
        throw "qa_script_error:$($_.Exception.GetType().Name)"
    }
}

function Resolve-Blocker {
    param([string]$ErrorText)
    if ([string]::IsNullOrWhiteSpace($ErrorText)) {
        return "unknown_error"
    }

    if ($ErrorText.StartsWith("qa_script_error", [StringComparison]::OrdinalIgnoreCase)) {
        return "qa_script_error"
    }

    if ($allowedBlockers -contains $ErrorText) {
        return $ErrorText
    }

    return "unknown_error"
}

function Unwrap-ApiData {
    param([object]$Value)
    if ($null -eq $Value) {
        return $null
    }

    if ($Value.PSObject.Properties["data"]) {
        return $Value.data
    }

    if ($Value.PSObject.Properties["result"]) {
        return $Value.result
    }

    return $Value
}

function Invoke-Api {
    param(
        [string]$Endpoint,
        [string]$Token
    )

    $headers = @{ Authorization = "Bearer $Token" }
    $response = Invoke-RestMethod -Method Get -Uri "$ApiBaseUrl$Endpoint" -Headers $headers -TimeoutSec 25
    return Unwrap-ApiData $response
}

function Get-Prop {
    param(
        [object]$Object,
        [string[]]$Names,
        [object]$Default = ""
    )

    if ($null -eq $Object) {
        return $Default
    }

    foreach ($name in $Names) {
        $property = $Object.PSObject.Properties | Where-Object { $_.Name -ieq $name } | Select-Object -First 1
        if ($null -ne $property -and $null -ne $property.Value -and "$($property.Value)" -ne "") {
            return $property.Value
        }
    }

    return $Default
}

function Normalize-PortRanges {
    param([object]$Ports)
    $result = New-Object System.Collections.Generic.List[string]
    if ($null -eq $Ports) {
        return @()
    }

    foreach ($raw in @($Ports)) {
        $value = ([string]$raw).Trim().Replace("-", ":")
        if ([string]::IsNullOrWhiteSpace($value)) {
            continue
        }

        $parts = $value.Split(":")
        if ($parts.Count -eq 1) {
            $port = 0
            if ([int]::TryParse($parts[0], [ref]$port) -and $port -ge 1 -and $port -le 65535) {
                $result.Add("${port}:${port}")
            }
        } elseif ($parts.Count -eq 2) {
            $start = 0
            $end = 0
            if ([int]::TryParse($parts[0], [ref]$start) -and [int]::TryParse($parts[1], [ref]$end) -and $start -ge 1 -and $end -le 65535 -and $start -le $end) {
                $result.Add("${start}:${end}")
            }
        }
    }

    return @($result | Select-Object -Unique)
}

function ConvertTo-NodeConfig {
    param([object]$ApiConfig)
    $root = if ($ApiConfig.PSObject.Properties["config"]) { $ApiConfig.config } else { $ApiConfig }
    $tls = if ($root.PSObject.Properties["tls"]) { $root.tls } else { $root }
    $obfs = if ($root.PSObject.Properties["obfs"]) { $root.obfs } else { $root }

    $config = [ordered]@{
        server = [string](Get-Prop $root @("server", "host", "address"))
        server_port = [int](Get-Prop $root @("server_port", "port") 0)
        password = [string](Get-Prop $root @("password", "auth_password", "auth"))
        tls_server_name = [string](Get-Prop $tls @("server_name", "sni", "tls_server_name"))
        tls_insecure = [bool](Get-Prop $tls @("insecure", "allow_insecure") $false)
        fallback_ports = @(Get-Prop $root @("fallback_ports", "server_ports") @())
        obfs_type = [string](Get-Prop $obfs @("type", "obfs_type"))
        obfs_password = [string](Get-Prop $obfs @("password", "obfs_password"))
        up_mbps = [int](Get-Prop $root @("up_mbps", "upMbps", "up") 0)
        down_mbps = [int](Get-Prop $root @("down_mbps", "downMbps", "down") 0)
    }

    if ([string]::IsNullOrWhiteSpace($config.server) -or $config.server_port -le 0 -or [string]::IsNullOrWhiteSpace($config.password) -or [string]::IsNullOrWhiteSpace($config.tls_server_name)) {
        throw "invalid_node_config"
    }

    return $config
}

function New-HysteriaOutbound {
    param([hashtable]$NodeConfig)
    $outbound = [ordered]@{
        type = "hysteria2"
        tag = "proxy"
        server = $NodeConfig.server
        server_port = $NodeConfig.server_port
        password = $NodeConfig.password
        tls = [ordered]@{
            enabled = $true
            server_name = $NodeConfig.tls_server_name
            insecure = $NodeConfig.tls_insecure
        }
    }

    $ports = Normalize-PortRanges $NodeConfig.fallback_ports
    if ($ports.Count -gt 0) {
        $outbound.server_ports = $ports
    }

    if (-not [string]::IsNullOrWhiteSpace($NodeConfig.obfs_type) -and -not [string]::IsNullOrWhiteSpace($NodeConfig.obfs_password)) {
        $outbound.obfs = [ordered]@{
            type = $NodeConfig.obfs_type
            password = $NodeConfig.obfs_password
        }
    }

    if ($NodeConfig.up_mbps -gt 0) {
        $outbound.up_mbps = $NodeConfig.up_mbps
    }

    if ($NodeConfig.down_mbps -gt 0) {
        $outbound.down_mbps = $NodeConfig.down_mbps
    }

    return $outbound
}

function Write-JsonFile {
    param(
        [object]$Value,
        [string]$Path
    )

    $json = $Value | ConvertTo-Json -Depth 64
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function New-PreflightConfig {
    param(
        [hashtable]$NodeConfig,
        [string]$Path
    )

    $config = [ordered]@{
        log = @{ level = "warn"; disabled = $false; timestamp = $false }
        dns = @{
            servers = @(@{ type = "local"; tag = "local" })
            final = "local"
            strategy = "prefer_ipv4"
        }
        inbounds = @(@{
            type = "mixed"
            tag = "mixed-in"
            listen = "127.0.0.1"
            listen_port = 20808
        })
        outbounds = @(
            (New-HysteriaOutbound $NodeConfig),
            @{ type = "direct"; tag = "direct" },
            @{ type = "block"; tag = "block" }
        )
        route = @{
            default_domain_resolver = "local"
            final = "proxy"
        }
    }

    Write-JsonFile $config $Path
}

function New-TunConfig {
    param(
        [hashtable]$NodeConfig,
        [string]$Profile,
        [string]$Path
    )

    $strictRoute = $Profile -eq "A"
    $simpleDns = $Profile -eq "C"
    $dns = if ($simpleDns) {
        @{
            servers = @(@{ type = "local"; tag = "local" })
            final = "local"
            strategy = "prefer_ipv4"
        }
    } else {
        @{
            servers = @(
                @{ type = "https"; tag = "cloudflare"; server = "1.1.1.1" },
                @{ type = "local"; tag = "local" }
            )
            final = "cloudflare"
            strategy = "prefer_ipv4"
        }
    }

    $route = [ordered]@{
        auto_detect_interface = $true
        default_domain_resolver = "local"
        final = "proxy"
    }

    if (-not $simpleDns) {
        $route.rules = @(@{ protocol = "dns"; action = "hijack-dns" })
    }

    $config = [ordered]@{
        log = @{ level = "warn"; disabled = $false; timestamp = $false }
        dns = $dns
        inbounds = @(@{
            type = "tun"
            tag = "tun-in"
            interface_name = "ShanlianVPN"
            address = @("172.19.0.1/30")
            mtu = 9000
            auto_route = $true
            strict_route = $strictRoute
            stack = "system"
        })
        outbounds = @(
            (New-HysteriaOutbound $NodeConfig),
            @{ type = "direct"; tag = "direct" },
            @{ type = "block"; tag = "block" }
        )
        route = $route
    }

    Write-JsonFile $config $Path
}

function Invoke-SingBoxCheck {
    param([string]$ConfigPath)
    $output = & $script:singBoxExe check -c $ConfigPath 2>&1
    return @{
        ok = $LASTEXITCODE -eq 0
        summary = Sanitize-Text ($output | Out-String)
    }
}

function Start-SingBox {
    param(
        [string]$ConfigPath,
        [string]$StdoutPath,
        [string]$StderrPath
    )

    Remove-Item -LiteralPath $StdoutPath, $StderrPath -Force -ErrorAction SilentlyContinue
    return Start-Process -FilePath $script:singBoxExe `
        -ArgumentList @("run", "-c", $ConfigPath) `
        -WindowStyle Hidden `
        -RedirectStandardOutput $StdoutPath `
        -RedirectStandardError $StderrPath `
        -PassThru
}

function Stop-SingBoxProcess {
    param([object]$Process)
    if ($null -ne $Process -and -not $Process.HasExited) {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
        $Process.WaitForExit(3000) | Out-Null
    }
}

function Get-ProcessSummary {
    param(
        [string]$StdoutPath,
        [string]$StderrPath
    )

    $text = ""
    if (Test-Path -LiteralPath $StdoutPath) { $text += Get-Content -LiteralPath $StdoutPath -Raw }
    if (Test-Path -LiteralPath $StderrPath) { $text += " "; $text += Get-Content -LiteralPath $StderrPath -Raw }
    return Sanitize-Text $text
}

function Test-TunAdapter {
    $tun = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -like "*Shanlian*" `
            -or $_.Name -match "Wintun|sing-box|Tunnel|Meta" `
            -or $_.InterfaceDescription -match "Wintun|sing-box|Tunnel|Meta"
    }
    return $null -ne $tun
}

function Test-DefaultRouteSwitched {
    $routes = Get-NetRoute -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue |
        Sort-Object RouteMetric |
        Select-Object -First 8 InterfaceAlias, NextHop, RouteMetric
    $text = $routes | Out-String
    return $text -match "Shanlian|Wintun|sing-box|Tunnel|Meta"
}

function Wait-Until {
    param(
        [scriptblock]$Predicate,
        [DateTimeOffset]$Deadline,
        [int]$IntervalSeconds = 1
    )

    while ([DateTimeOffset]::UtcNow -lt $Deadline) {
        if (& $Predicate) {
            return $true
        }

        Start-Sleep -Seconds $IntervalSeconds
    }

    return $false
}

function Test-DnsReady {
    foreach ($name in @("example.com", "cloudflare.com", "google.com")) {
        try {
            Resolve-DnsName $name -ErrorAction Stop | Out-Null
            return $true
        } catch {
        }
    }

    return $false
}

function Test-HttpsReady {
    foreach ($uri in @("https://www.google.com/generate_204", "https://cloudflare.com/cdn-cgi/trace")) {
        try {
            $response = Invoke-WebRequest -Uri $uri -TimeoutSec 10 -UseBasicParsing
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 400) {
                return $true
            }
        } catch {
        }

        try {
            $status = & curl.exe --noproxy "*" --max-time 20 -L -sS -o NUL -w "%{http_code}" $uri 2>$null
            $exitCode = $LASTEXITCODE
            $code = 0
            [void][int]::TryParse([string]$status, [ref]$code)
            if ($exitCode -eq 0 -and $code -ge 200 -and $code -lt 400) {
                return $true
            }
        } catch {
        }
    }

    return $false
}

function Get-HttpsProbeSummary {
    $items = New-Object System.Collections.Generic.List[string]
    foreach ($uri in @("https://www.google.com/generate_204", "https://cloudflare.com/cdn-cgi/trace")) {
        try {
            $result = & curl.exe --noproxy "*" --max-time 20 -L -sS -o NUL -w "http=%{http_code} err=%{errormsg}" $uri 2>&1
            $items.Add("$uri exit=$LASTEXITCODE $(Sanitize-Text ($result | Out-String))")
        } catch {
            $items.Add("$uri curl_exception=$(Sanitize-Text "$_")")
        }
    }

    return Sanitize-Text ($items -join " | ")
}

function Test-CurlProxy {
    param([string]$Uri)
    try {
        $status = & curl.exe --max-time 20 -L -sS -o NUL -w "%{http_code}" -x socks5h://127.0.0.1:20808 $Uri 2>$null
        $exitCode = $LASTEXITCODE
        $code = 0
        [void][int]::TryParse([string]$status, [ref]$code)
        return $exitCode -eq 0 -and $code -ge 200 -and $code -lt 400
    } catch {
        return $false
    }
}

function Invoke-Preflight {
    param(
        [hashtable]$NodeConfig,
        [string]$Country
    )

    $configPath = Join-Path $script:appDataPath "proxy-preflight-config.json"
    New-PreflightConfig $NodeConfig $configPath
    $check = Invoke-SingBoxCheck $configPath
    Write-Host "${Country}_preflight_check_success=$($check.ok)"
    if (-not $check.ok) {
        Write-Host "${Country}_preflight_summary=$($check.summary)"
        return @{ success = $false; blocker = Classify-Text $check.summary }
    }

    $stdout = Join-Path $env:TEMP "shanlian-preflight-$Country-stdout.log"
    $stderr = Join-Path $env:TEMP "shanlian-preflight-$Country-stderr.log"
    $process = Start-SingBox $configPath $stdout $stderr
    Start-Sleep -Seconds 3
    try {
        if ($process.HasExited) {
            $summary = Get-ProcessSummary $stdout $stderr
            return @{ success = $false; blocker = "sing_box_exited"; summary = $summary }
        }

        $ok = (Test-CurlProxy "https://www.google.com/generate_204") -or (Test-CurlProxy "https://cloudflare.com/cdn-cgi/trace")
        $summary = Get-ProcessSummary $stdout $stderr
        return @{
            success = $ok
            blocker = if ($ok) { "none" } else { Classify-Text $summary }
            summary = $summary
        }
    } finally {
        Stop-SingBoxProcess $process
        Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-TunProfile {
    param(
        [hashtable]$NodeConfig,
        [string]$Country,
        [string]$Profile
    )

    $configPath = Join-Path $script:appDataPath "runtime-config.json"
    New-TunConfig $NodeConfig $Profile $configPath
    $check = Invoke-SingBoxCheck $configPath
    Write-Host "config_profile=$Profile"
    Write-Host "profile_${Profile}_sing_box_check_success=$($check.ok)"
    if (-not $check.ok) {
        Write-Host "profile_${Profile}_summary=$($check.summary)"
        return @{ success = $false; blocker = "hysteria2_outbound_failed"; check = $false; start = $false; tun = $false; route = $false; dns = $false; https = $false }
    }

    $stdout = Join-Path $env:TEMP "shanlian-tun-$Country-$Profile-stdout.log"
    $stderr = Join-Path $env:TEMP "shanlian-tun-$Country-$Profile-stderr.log"
    $process = $null
    $connected = $false
    $startedAt = [DateTimeOffset]::UtcNow
    try {
        $process = Start-SingBox $configPath $stdout $stderr
        Start-Sleep -Seconds 5
        if ($process.HasExited) {
            $summary = Get-ProcessSummary $stdout $stderr
            Write-Host "profile_${Profile}_sing_box_started=False"
            Write-Host "profile_${Profile}_summary=$summary"
            return @{ success = $false; blocker = "sing_box_exited"; check = $true; start = $false; tun = $false; route = $false; dns = $false; https = $false }
        }

        Write-Host "profile_${Profile}_sing_box_started=True"
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(60)
        $tunOk = Wait-Until { Test-TunAdapter } ([DateTimeOffset]::UtcNow.AddSeconds(20))
        Write-Host "profile_${Profile}_tun_detect_success=$tunOk"
        if (-not $tunOk) {
            Start-Sleep -Seconds ([Math]::Max(0, 90 - [int]([DateTimeOffset]::UtcNow - $startedAt).TotalSeconds))
            return @{ success = $false; blocker = "tun_adapter_missing"; check = $true; start = $true; tun = $false; route = $false; dns = $false; https = $false }
        }

        $routeDeadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
        if ($routeDeadline -gt $deadline) { $routeDeadline = $deadline }
        $routeOk = Wait-Until { Test-DefaultRouteSwitched } $routeDeadline
        Write-Host "profile_${Profile}_route_detect_success=$routeOk"
        if (-not $routeOk) {
            Start-Sleep -Seconds ([Math]::Max(0, 90 - [int]([DateTimeOffset]::UtcNow - $startedAt).TotalSeconds))
            return @{ success = $false; blocker = "route_failed"; check = $true; start = $true; tun = $true; route = $false; dns = $false; https = $false }
        }

        $dnsOk = $false
        for ($i = 0; $i -lt 10 -and [DateTimeOffset]::UtcNow -lt $deadline; $i++) {
            if (Test-DnsReady) {
                $dnsOk = $true
                break
            }

            Start-Sleep -Seconds 2
        }

        Write-Host "profile_${Profile}_dns_check_success=$dnsOk"
        if (-not $dnsOk) {
            Start-Sleep -Seconds ([Math]::Max(0, 90 - [int]([DateTimeOffset]::UtcNow - $startedAt).TotalSeconds))
            return @{ success = $false; blocker = "dns_failed"; check = $true; start = $true; tun = $true; route = $true; dns = $false; https = $false }
        }

        $httpsOk = Wait-Until { Test-HttpsReady } $deadline
        Write-Host "profile_${Profile}_https_check_success=$httpsOk"
        if (-not $httpsOk) {
            Write-Host "profile_${Profile}_https_summary=$(Get-HttpsProbeSummary)"
            Write-Host "profile_${Profile}_sing_box_summary=$(Get-ProcessSummary $stdout $stderr)"
            Start-Sleep -Seconds ([Math]::Max(0, 90 - [int]([DateTimeOffset]::UtcNow - $startedAt).TotalSeconds))
            return @{ success = $false; blocker = "internet_check_failed"; check = $true; start = $true; tun = $true; route = $true; dns = $true; https = $false }
        }

        $connected = $true
        return @{ success = $true; blocker = "none"; process = $process; check = $true; start = $true; tun = $true; route = $true; dns = $true; https = $true }
    } catch {
        return @{ success = $false; blocker = "sing_box_start_failed"; check = $true; start = $false; tun = $false; route = $false; dns = $false; https = $false }
    } finally {
        if (-not $connected) {
            Stop-SingBoxProcess $process
        }

        Remove-Item -LiteralPath $stdout, $stderr -Force -ErrorAction SilentlyContinue
    }
}

function Select-CountryNodes {
    param([object[]]$Nodes)
    $selected = @()
    foreach ($country in @("US", "JP")) {
        $node = $Nodes | Where-Object {
            $code = [string](Get-Prop $_ @("country_code", "region_code"))
            $countryText = [string](Get-Prop $_ @("country", "country_name", "region", "name", "title"))
            ($country -eq "US" -and ($code -match "US|USA" -or $countryText -match "United States|America")) `
                -or ($country -eq "JP" -and ($code -match "JP|JPN" -or $countryText -match "Japan"))
        } | Select-Object -First 1

        if ($null -ne $node) {
            $selected += [pscustomobject]@{ country = $country; node = $node }
        }
    }

    return $selected
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$appDataPath = Join-Path $env:APPDATA "ShanlianVPN"
$authData = Join-Path $appDataPath "auth.dat"
$script:singBoxExe = Join-Path $repoRoot "tools\sing-box\windows-amd64\sing-box.exe"
$script:successResult = $null
$finalBlocker = "unknown_error"
$finalCountry = "none"
$finalProfile = "none"
$profileResults = @{
    A = $false
    B = $false
    C = $false
}
$preflightResults = @{
    US = $false
    JP = $false
}

New-Item -ItemType Directory -Force -Path $appDataPath | Out-Null

Write-Host "Shanlian VPN Windows closed-loop QA"
Write-Host "administrator=$(Test-Administrator)"

if (-not (Test-Administrator)) {
    Write-Host "CONNECTED=false"
    Write-Host "BLOCKER=tun_permission_failed"
    exit 13
}

if (-not (Test-Path -LiteralPath $script:singBoxExe)) {
    Write-Host "CONNECTED=false"
    Write-Host "BLOCKER=sing_box_start_failed"
    exit 2
}

$networkEnvironment = Get-NetworkEnvironment
Write-NetworkEnvironment $networkEnvironment
if ($networkEnvironment.NetworkConflict) {
    Write-Host "CONNECTED=false"
    Write-Host "BLOCKER=network_conflict"
    exit 20
}

try {
    $token = Read-Token $authData
    Write-Host "auth_loaded=True"
    $nodesData = Invoke-Api "/api/nodes" $token
    $nodes = if ($nodesData -is [array]) { $nodesData } elseif ($nodesData.PSObject.Properties["nodes"]) { @($nodesData.nodes) } else { @() }
    $countryNodes = Select-CountryNodes $nodes
    if ($countryNodes.Count -eq 0) {
        throw "nodes_fetch_failed"
    }

    foreach ($entry in $countryNodes) {
        $country = $entry.country
        $nodeId = [string](Get-Prop $entry.node @("id", "node_id"))
        if ([string]::IsNullOrWhiteSpace($nodeId)) {
            continue
        }

        Write-Host "COUNTRY_UNDER_TEST=$country"
        $apiConfig = Invoke-Api "/api/nodes/$([Uri]::EscapeDataString($nodeId))/config" $token
        $nodeConfig = ConvertTo-NodeConfig $apiConfig
        $preflight = Invoke-Preflight $nodeConfig $country
        $preflightResults[$country] = [bool]$preflight.success
        Write-Host "${country}_preflight_success=$($preflight.success)"
        if (-not $preflight.success) {
            $finalBlocker = $preflight.blocker
            Write-Host "${country}_preflight_blocker=$($preflight.blocker)"
            continue
        }

        foreach ($profile in @("A", "B", "C")) {
            $result = Invoke-TunProfile $nodeConfig $country $profile
            $profileResults[$profile] = $profileResults[$profile] -or [bool]$result.success
            if ($result.success) {
                $script:successResult = $result
                $finalBlocker = "none"
                $finalCountry = $country
                $finalProfile = $profile
                break
            }

            $finalBlocker = Resolve-Blocker $result.blocker
        }

        if ($null -ne $script:successResult) {
            break
        }
    }
} catch {
    $errorText = "$_"
    $finalBlocker = Resolve-Blocker $errorText
    if ($finalBlocker -in @("login_required", "auth_state_invalid")) {
        Write-Host "qa_error=$finalBlocker"
    } elseif ($finalBlocker -eq "qa_script_error") {
        Write-Host "qa_error=qa_script_error"
        Write-Host "qa_error_summary=$(Sanitize-Text $errorText)"
    } else {
        Write-Host "qa_error=$(Sanitize-Text $errorText)"
    }
} finally {
    $browserInternet = $false
    $disconnectRecovered = $false
    $finalTunAdapterExists = Test-TunAdapter
    $finalRouteSwitched = Test-DefaultRouteSwitched
    $finalDnsSuccess = Test-DnsReady
    $finalHttpsSuccess = Test-HttpsReady
    if ($null -ne $script:successResult) {
        $finalTunAdapterExists = Test-TunAdapter
        $finalRouteSwitched = Test-DefaultRouteSwitched
        $finalDnsSuccess = Test-DnsReady
        $finalHttpsSuccess = Test-HttpsReady
        $browserInternet = $finalHttpsSuccess
        if (-not $KeepConnected) {
            Stop-SingBoxProcess $script:successResult.process
            Start-Sleep -Seconds 3
            $disconnectRecovered = Test-HttpsReady
        }
    }

    Write-Host "CONNECTED=$($null -ne $script:successResult)"
    if ($null -ne $script:successResult) {
        Write-Host "COUNTRY=$finalCountry"
        Write-Host "PROFILE=$finalProfile"
    }

    Write-Host "US_preflight_success=$($preflightResults.US)"
    Write-Host "JP_preflight_success=$($preflightResults.JP)"
    Write-Host "TUN_profile_A_success=$($profileResults.A)"
    Write-Host "TUN_profile_B_success=$($profileResults.B)"
    Write-Host "TUN_profile_C_success=$($profileResults.C)"
    Write-Host "final_country=$finalCountry"
    Write-Host "final_profile=$finalProfile"
    Write-Host "tun_adapter_exists=$finalTunAdapterExists"
    Write-Host "default_route_switched=$finalRouteSwitched"
    Write-Host "dns_success=$finalDnsSuccess"
    Write-Host "https_success=$finalHttpsSuccess"
    Write-Host "windows_browser_internet=$browserInternet"
    Write-Host "disconnect_network_recovered=$disconnectRecovered"
    Write-Host "BLOCKER=$finalBlocker"
}
