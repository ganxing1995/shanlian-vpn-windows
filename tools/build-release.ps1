param(
    [string]$Runtime = "win-x64",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"

function Decode-Utf8Base64 {
    param([string]$Value)
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value))
}

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
$projectPath = Join-Path $repoRoot "ShanlianVpn.Windows\ShanlianVpn.Windows.csproj"
$publishDir = Join-Path $repoRoot "publish"
$appName = Decode-Utf8Base64 "6Zeq6L+eVlBO"
$releaseExe = Join-Path $publishDir "$appName.exe"
$sourceSingBox = Join-Path $repoRoot "tools\sing-box\windows-amd64\sing-box.exe"
$publishSingBox = Join-Path $publishDir "tools\sing-box\windows-amd64\sing-box.exe"

$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

Write-Host "Repo root: $repoRoot"
Write-Host "dotnet: $dotnetPath"

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

$restoreArg = if ($NoRestore) { "--no-restore" } else { "" }
& $dotnetPath publish $projectPath -c Release -r $Runtime --self-contained true -o $publishDir -p:PublishSingleFile=false -p:AssemblyName=$appName $restoreArg
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish Release failed."
}

if (-not (Test-Path -LiteralPath $releaseExe)) {
    throw "Release exe not found: $releaseExe"
}

if (-not (Test-Path -LiteralPath $sourceSingBox)) {
    throw "sing-box source missing: $sourceSingBox"
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $publishSingBox) | Out-Null
Copy-Item -LiteralPath $sourceSingBox -Destination $publishSingBox -Force

if (-not (Test-Path -LiteralPath $publishSingBox)) {
    throw "sing-box was not copied to Release publish output: $publishSingBox"
}

Write-Host "Publish dir: $publishDir"
Write-Host "Release exe: $releaseExe"
Write-Host "sing-box copied: $publishSingBox"

