param(
    [switch]$InstallInnoSetup
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

function Find-InnoSetupCompiler {
    $candidatePaths = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }

    return $candidatePaths | Select-Object -First 1
}

if (-not (Find-InnoSetupCompiler)) {
    if (-not $InstallInnoSetup) {
        throw "Inno Setup 6 is not installed. Run this command again with -InstallInnoSetup, or install Inno Setup 6 manually."
    }

    if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) {
        throw "Windows Package Manager was not found. Install Inno Setup 6 manually and run this script again."
    }

    Write-Host "Installing Inno Setup 6..."
    & winget.exe install --id JRSoftware.InnoSetup --exact --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup installation failed."
    }
}

& (Join-Path $root "build-release.ps1") -Runtime win-x64 -CreateInstaller
