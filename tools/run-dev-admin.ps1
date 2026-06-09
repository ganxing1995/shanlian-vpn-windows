param()

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
$scriptPath = $PSCommandPath

if (-not (Test-Administrator)) {
    Write-Host "Not elevated. Requesting Administrator PowerShell..."
    Start-Process -FilePath "powershell.exe" -Verb RunAs -ArgumentList @(
        "-NoExit",
        "-ExecutionPolicy", "Bypass",
        "-File", "`"$scriptPath`""
    )
    exit 0
}

$buildScript = Join-Path $repoRoot "tools\build-debug.ps1"
& powershell.exe -ExecutionPolicy Bypass -File $buildScript
if ($LASTEXITCODE -ne 0) {
    throw "Debug build failed."
}

$debugExe = Join-Path $repoRoot "ShanlianVpn.Windows\bin\Debug\net8.0-windows\ShanlianVpn.Windows.exe"
if (-not (Test-Path -LiteralPath $debugExe)) {
    throw "Debug exe not found: $debugExe"
}

Write-Host "Starting Shanlian VPN Windows App..."
Write-Host "Debug exe: $debugExe"
Start-Process -FilePath $debugExe -WorkingDirectory (Split-Path -Parent $debugExe)

