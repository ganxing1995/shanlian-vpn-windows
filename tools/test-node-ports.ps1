$ErrorActionPreference = "Continue"

function Get-RuntimeNodeInfo {
    $configPath = Join-Path $env:APPDATA "ShanlianVPN\runtime-config.json"
    if (-not (Test-Path -LiteralPath $configPath)) {
        return $null
    }

    $json = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    foreach ($outbound in $json.outbounds) {
        if ($outbound.type -eq "hysteria2") {
            $ports = New-Object System.Collections.Generic.List[int]
            if ($outbound.server_port) {
                [void]$ports.Add([int]$outbound.server_port)
            }

            if ($outbound.server_ports) {
                foreach ($range in $outbound.server_ports) {
                    $text = [string]$range
                    if ($text -match '^(\d+):(\d+)$') {
                        [void]$ports.Add([int]$Matches[1])
                    } elseif ($text -match '^\d+$') {
                        [void]$ports.Add([int]$text)
                    }
                }
            }

            foreach ($fallback in @(443, 8443, 2053, 2087, 2096, 2083)) {
                if (-not $ports.Contains($fallback)) {
                    [void]$ports.Add($fallback)
                }
            }

            return [pscustomobject]@{
                Server = [string]$outbound.server
                Ports = $ports | Select-Object -Unique
            }
        }
    }

    return $null
}

function Test-UdpSend {
    param([string]$Server, [int]$Port)
    try {
        $client = [Net.Sockets.UdpClient]::new()
        $client.Client.SendTimeout = 2000
        $client.Connect($Server, $Port)
        [byte[]]$bytes = @(0)
        [void]$client.Send($bytes, $bytes.Length)
        $client.Close()
        return "udp_sent_no_response_expected"
    } catch {
        return "udp_send_failed"
    }
}

function Test-TcpSocket {
    param([string]$Server, [int]$Port)
    try {
        $client = [Net.Sockets.TcpClient]::new()
        $async = $client.BeginConnect($Server, $Port, $null, $null)
        $ok = $async.AsyncWaitHandle.WaitOne(2000)
        if (-not $ok) {
            $client.Close()
            return "tcp_timeout"
        }

        $client.EndConnect($async)
        $client.Close()
        return "tcp_connected"
    } catch {
        return "tcp_closed_or_udp_only"
    }
}

$node = Get-RuntimeNodeInfo
if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.Server)) {
    Write-Host "runtime_node_missing"
    Write-Host "BLOCKER=unknown_error"
    exit 0
}

Write-Host "Shanlian VPN node port safe test"
Write-Host "server_present=True"

try {
    $addresses = [Net.Dns]::GetHostAddresses($node.Server)
    Write-Host "server_dns_resolved=$($addresses.Length -gt 0)"
} catch {
    Write-Host "server_dns_resolved=False"
    Write-Host "BLOCKER=server_unreachable"
    exit 0
}

foreach ($port in $node.Ports) {
    $udp = Test-UdpSend -Server $node.Server -Port $port
    $tcp = Test-TcpSocket -Server $node.Server -Port $port
    Write-Host "port=$port udp=$udp tcp=$tcp"
}

Write-Host "BLOCKER=none"
