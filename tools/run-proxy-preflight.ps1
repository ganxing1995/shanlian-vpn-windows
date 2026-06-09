$ErrorActionPreference = "Continue"

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

function New-PreflightConfig {
    param([string]$RuntimeConfig, [string]$PreflightConfig)

    $json = Get-Content -LiteralPath $RuntimeConfig -Raw | ConvertFrom-Json
    $preflight = [ordered]@{
        log = @{
            level = "warn"
            disabled = $false
            timestamp = $false
        }
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
        outbounds = $json.outbounds
        route = @{
            default_domain_resolver = "local"
            final = "proxy"
        }
    }

    $text = $preflight | ConvertTo-Json -Depth 64
    [IO.File]::WriteAllText($PreflightConfig, $text, [Text.UTF8Encoding]::new($false))
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$runtimeConfig = Join-Path $env:APPDATA "ShanlianVPN\runtime-config.json"
$preflightConfig = Join-Path $env:TEMP "shanlian-proxy-preflight.json"
$singBoxExe = Join-Path $repoRoot "tools\sing-box\windows-amd64\sing-box.exe"
$blocker = "unknown_error"

Write-Host "Shanlian VPN proxy preflight"
Write-Host "runtime_config_exists=$(Test-Path -LiteralPath $runtimeConfig)"

if (-not (Test-Path -LiteralPath $runtimeConfig)) {
    Write-Host "BLOCKER=unknown_error"
    exit 0
}

New-PreflightConfig -RuntimeConfig $runtimeConfig -PreflightConfig $preflightConfig

$checkOutput = & $singBoxExe check -c $preflightConfig 2>&1
$checkExit = $LASTEXITCODE
Write-Host "preflight_check_exit=$checkExit"
if ($checkExit -ne 0) {
    Write-Host "safe_summary=$(Sanitize-Text ($checkOutput | Out-String))"
    Write-Host "BLOCKER=hysteria2_outbound_failed"
    Remove-Item -LiteralPath $preflightConfig -Force -ErrorAction SilentlyContinue
    exit 0
}

$stdoutLog = Join-Path $env:TEMP "shanlian-preflight-stdout.log"
$stderrLog = Join-Path $env:TEMP "shanlian-preflight-stderr.log"
Remove-Item -LiteralPath $stdoutLog, $stderrLog -Force -ErrorAction SilentlyContinue
$process = Start-Process -FilePath $singBoxExe `
    -ArgumentList @("run", "-c", $preflightConfig) `
    -NoNewWindow `
    -RedirectStandardOutput $stdoutLog `
    -RedirectStandardError $stderrLog `
    -PassThru
Start-Sleep -Seconds 3

if ($process.HasExited) {
    Write-Host "preflight_process_running=False"
    Write-Host "exit_code=$($process.ExitCode)"
    $blocker = "hysteria2_outbound_failed"
} else {
    Write-Host "preflight_process_running=True"
    $googleHttp = & curl.exe --max-time 20 -L -sS -o NUL -w "%{http_code}" -x socks5h://127.0.0.1:20808 https://www.google.com/generate_204 2>$null
    $googleExit = $LASTEXITCODE
    $cloudflareHttp = & curl.exe --max-time 20 -L -sS -o NUL -w "%{http_code}" -x socks5h://127.0.0.1:20808 https://cloudflare.com/cdn-cgi/trace 2>$null
    $cloudflareExit = $LASTEXITCODE

    Write-Host "google_generate_204=HTTP $googleHttp curl_exit=$googleExit"
    Write-Host "cloudflare_trace=HTTP $cloudflareHttp curl_exit=$cloudflareExit"

    $googleStatus = 0
    $cloudflareStatus = 0
    [void][int]::TryParse([string]$googleHttp, [ref]$googleStatus)
    [void][int]::TryParse([string]$cloudflareHttp, [ref]$cloudflareStatus)

    if (($googleExit -eq 0 -and $googleStatus -ge 200 -and $googleStatus -lt 400) `
        -or ($cloudflareExit -eq 0 -and $cloudflareStatus -ge 200 -and $cloudflareStatus -lt 400)) {
        $blocker = "none"
    } else {
        $blocker = "hysteria2_outbound_failed"
    }
}

if (-not $process.HasExited) {
    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    $process.WaitForExit(3000) | Out-Null
}

$summary = ""
if (Test-Path -LiteralPath $stdoutLog) {
    $summary += Get-Content -LiteralPath $stdoutLog -Raw
}

if (Test-Path -LiteralPath $stderrLog) {
    $summary += " "
    $summary += Get-Content -LiteralPath $stderrLog -Raw
}

Write-Host "safe_summary=$(Sanitize-Text $summary)"
Write-Host "BLOCKER=$blocker"
Remove-Item -LiteralPath $preflightConfig -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $stdoutLog, $stderrLog -Force -ErrorAction SilentlyContinue
