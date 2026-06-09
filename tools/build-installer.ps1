param()

$ErrorActionPreference = "Stop"

function Decode-Utf8Base64 {
    param([string]$Value)
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value))
}

function Get-RepoRoot {
    return (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
}

function Find-InnoCompiler {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    $candidates = @(
        "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
        "C:\Program Files\Inno Setup 6\ISCC.exe",
        "C:\Program Files (x86)\Inno Setup 5\ISCC.exe",
        "C:\Program Files\Inno Setup 5\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) {
            return $candidate
        }
    }

    return $null
}

$repoRoot = Get-RepoRoot
$buildRelease = Join-Path $repoRoot "tools\build-release.ps1"
$issPath = Join-Path $repoRoot "installer\ShanlianVPN.iss"
$outputDir = Join-Path $repoRoot "installer\output"
$setupName = "$(Decode-Utf8Base64 '6Zeq6L+eVlBO')-1.0.0-setup.exe"
$setupPath = Join-Path $outputDir $setupName

Write-Host "Repo root: $repoRoot"
& powershell.exe -ExecutionPolicy Bypass -File $buildRelease

$iscc = Find-InnoCompiler
if ([string]::IsNullOrWhiteSpace($iscc)) {
    Write-Host "Inno Setup is not installed."
    Write-Host "Please install Inno Setup 6 from: https://jrsoftware.org/isdl.php"
    Write-Host "After installing, run: powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1"
    Write-Host "Temporary runnable publish dir: $(Join-Path $repoRoot 'publish')"
    exit 2
}

if (Test-Path -LiteralPath $outputDir) {
    Remove-Item -LiteralPath $outputDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

& $iscc "/DCustomRepoRoot=$repoRoot" $issPath
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup build failed."
}

if (-not (Test-Path -LiteralPath $setupPath)) {
    throw "Installer not found: $setupPath"
}

$sizeMb = [Math]::Round((Get-Item -LiteralPath $setupPath).Length / 1MB, 2)
Write-Host "Installer: $setupPath"
Write-Host "Installer size MB: $sizeMb"

