param(
    [string]$ApiBaseUrl = "https://api.lianshu.shop"
)

$ErrorActionPreference = "Stop"

function Write-Check {
    param(
        [string]$Name,
        [bool]$Passed,
        [string]$Message
    )

    $status = if ($Passed) { "OK" } else { "FAIL" }
    Write-Host "[$status] $Name - $Message"
}

function Decode-Utf8Base64 {
    param([string]$Value)
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value))
}

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$singBoxPath = Join-Path $repoRoot "tools\sing-box\windows-amd64\sing-box.exe"
$appDataPath = Join-Path $env:APPDATA "ShanlianVPN"
$localDotnetPath = Join-Path $repoRoot ".dotnet\dotnet.exe"
$installSdkMessage = Decode-Utf8Base64 "6K+35a6J6KOFIC5ORVQgOCBTREvvvIzkuI3mmK8gUnVudGltZeOAgg=="
$runAsAdminMessage = Decode-Utf8Base64 "6L+e5o6lIFRVTiDliY3or7fku6XnrqHnkIblkZjouqvku73ov5DooYzpl6rov54gVlBO"

Write-Host "Shanlian VPN Windows environment check"
Write-Host "Repository: $repoRoot"
Write-Host ""

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnetCommand) {
    if (Test-Path -LiteralPath $localDotnetPath) {
        $version = & $localDotnetPath --version 2>$null
        Write-Check ".NET SDK" $true "local .dotnet SDK: $version"
    } else {
        Write-Check ".NET SDK" $false $installSdkMessage
    }
} else {
    $sdks = & dotnet --list-sdks 2>$null
    $hasSdk = $LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace(($sdks | Out-String))
    if ($hasSdk) {
        $version = & dotnet --version 2>$null
        Write-Check ".NET SDK" $true "dotnet --version: $version"
    } elseif (Test-Path -LiteralPath $localDotnetPath) {
        $version = & $localDotnetPath --version 2>$null
        Write-Check ".NET SDK" $true "local .dotnet SDK: $version"
    } else {
        Write-Check ".NET SDK" $false $installSdkMessage
    }
}

Write-Check "sing-box.exe" (Test-Path -LiteralPath $singBoxPath) $singBoxPath

$isAdmin = Test-Administrator
Write-Check "Administrator" $isAdmin $(if ($isAdmin) { "Current PowerShell is elevated" } else { $runAsAdminMessage })

try {
    $response = Invoke-WebRequest -Uri $ApiBaseUrl -Method Head -TimeoutSec 10 -UseBasicParsing
    Write-Check "API base URL" $true "$ApiBaseUrl HTTP $($response.StatusCode)"
} catch {
    try {
        $response = Invoke-WebRequest -Uri $ApiBaseUrl -Method Get -TimeoutSec 10 -UseBasicParsing
        Write-Check "API base URL" $true "$ApiBaseUrl HTTP $($response.StatusCode)"
    } catch {
        Write-Check "API base URL" $false "Cannot access $ApiBaseUrl"
    }
}

try {
    New-Item -ItemType Directory -Force -Path $appDataPath | Out-Null
    $testFile = Join-Path $appDataPath ".write-test"
    Set-Content -LiteralPath $testFile -Value "ok" -Encoding UTF8
    Remove-Item -LiteralPath $testFile -Force
    Write-Check "AppData writable" $true $appDataPath
} catch {
    Write-Check "AppData writable" $false $appDataPath
}
