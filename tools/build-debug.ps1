param(
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

function Get-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Get-DotnetPath {
    param([string]$RepoRoot)

    $localDotnet = Join-Path $RepoRoot ".dotnet\dotnet.exe"
    if (Test-Path -LiteralPath $localDotnet) {
        return $localDotnet
    }

    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $dotnet) {
        return $dotnet.Source
    }

    throw "Please install .NET 8 SDK, not Runtime."
}

$repoRoot = Get-RepoRoot
$dotnetPath = Get-DotnetPath $repoRoot
$solutionPath = Join-Path $repoRoot "ShanlianVpn.Windows.sln"
$projectPath = Join-Path $repoRoot "ShanlianVpn.Windows\ShanlianVpn.Windows.csproj"
$debugDir = Join-Path $repoRoot "ShanlianVpn.Windows\bin\Debug\net8.0-windows"
$debugExe = Join-Path $debugDir "ShanlianVpn.Windows.exe"
$sourceSingBox = Join-Path $repoRoot "tools\sing-box\windows-amd64\sing-box.exe"
$outputSingBox = Join-Path $debugDir "tools\sing-box\windows-amd64\sing-box.exe"

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

Write-Host "Repo root: $repoRoot"
Write-Host "dotnet: $dotnetPath"

if (-not $NoRestore) {
    & $dotnetPath restore $solutionPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet restore failed."
    }
}

& $dotnetPath build $projectPath -c Debug --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build Debug failed."
}

if (-not (Test-Path -LiteralPath $debugExe)) {
    throw "Debug exe not found: $debugExe"
}

if (-not (Test-Path -LiteralPath $sourceSingBox)) {
    Write-Host "sing-box source missing: $sourceSingBox"
} elseif (-not (Test-Path -LiteralPath $outputSingBox)) {
    throw "sing-box was not copied to Debug output: $outputSingBox"
} else {
    Write-Host "sing-box copied: $outputSingBox"
}

Write-Host "Debug exe: $debugExe"

