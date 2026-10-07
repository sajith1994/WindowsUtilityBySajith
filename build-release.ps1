param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [switch]$CreateInstaller
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$appProject = Join-Path $root "src\CentricDeviceMonitor\CentricDeviceMonitor.csproj"
$serviceProject = Join-Path $root "src\CentricDeviceMonitor.Service\CentricDeviceMonitor.Service.csproj"
$trayProject = Join-Path $root "src\CentricDeviceMonitor.Tray\CentricDeviceMonitor.Tray.csproj"
$appProjectDirectory = Split-Path -Parent $appProject
$serviceProjectDirectory = Split-Path -Parent $serviceProject
$trayProjectDirectory = Split-Path -Parent $trayProject
$output = Join-Path $root "dist\$Runtime"
$serviceOutput = Join-Path $root "dist\service-$Runtime"
$trayOutput = Join-Path $root "dist\tray-$Runtime"
$versionPropsPath = Join-Path $root "Directory.Build.props"
if (-not (Test-Path $versionPropsPath)) {
    throw "Directory.Build.props was not found; the release version cannot be determined."
}
[xml]$versionProps = Get-Content $versionPropsPath -Raw
$buildVersion = [string]$versionProps.Project.PropertyGroup.AppVersion
if ($buildVersion -notmatch '^\d+\.\d+\.\d+$') {
    throw "AppVersion in Directory.Build.props must use Major.Minor.Patch format. Current value: '$buildVersion'."
}
Write-Host "Building Windows Utility version $buildVersion" -ForegroundColor Cyan

function Ensure-PawnIOInstaller {
    $dependencyDirectory = Join-Path $root "installer\dependencies"
    $pawnIoInstaller = Join-Path $dependencyDirectory "PawnIO_setup.exe"
    $pawnIoUrl = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe"

    if (-not (Test-Path $dependencyDirectory)) {
        New-Item -ItemType Directory -Path $dependencyDirectory -Force | Out-Null
    }

    Write-Host "Downloading the official signed PawnIO 2.2.0 hardware sensor driver..."
    $temporaryPath = $pawnIoInstaller + ".download"
    if (Test-Path $temporaryPath) {
        Remove-Item $temporaryPath -Force
    }

    Invoke-WebRequest -Uri $pawnIoUrl -OutFile $temporaryPath -UseBasicParsing
    if (-not (Test-Path $temporaryPath) -or (Get-Item $temporaryPath).Length -lt 100000) {
        throw "The PawnIO download was incomplete."
    }

    $signature = Get-AuthenticodeSignature -FilePath $temporaryPath
    if ($signature.Status -eq "NotSigned" -or $signature.Status -eq "HashMismatch") {
        Remove-Item $temporaryPath -Force -ErrorAction SilentlyContinue
        throw "The downloaded PawnIO installer did not pass Authenticode validation."
    }

    Move-Item $temporaryPath $pawnIoInstaller -Force
    return $pawnIoInstaller
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET 10 SDK is required. Install the .NET 10 SDK before building."
}

$installedSdks = & dotnet --list-sdks
if (-not ($installedSdks -match '^10\.')) {
    throw ".NET 10 SDK was not found. Install the .NET 10 SDK before building."
}

$selectedSdk = & dotnet --version
Write-Host "Using .NET SDK $selectedSdk" -ForegroundColor Cyan
if (-not ($selectedSdk -match '^10\.')) {
    throw "This project must be built with the .NET 10 SDK. The selected SDK is $selectedSdk."
}

foreach ($path in @($output, $serviceOutput, $trayOutput)) {
    if (Test-Path $path) {
        Remove-Item $path -Recurse -Force
    }
}

foreach ($projectDirectory in @($appProjectDirectory, $serviceProjectDirectory, $trayProjectDirectory)) {
    foreach ($folderName in @("bin", "obj")) {
        $folderPath = Join-Path $projectDirectory $folderName
        if (Test-Path $folderPath) {
            Remove-Item $folderPath -Recurse -Force
        }
    }
}

Write-Host "Restoring application packages..."
& dotnet restore $appProject --force-evaluate
if ($LASTEXITCODE -ne 0) {
    throw "Application package restore failed."
}

Write-Host "Restoring background service packages..."
& dotnet restore $serviceProject --force-evaluate
if ($LASTEXITCODE -ne 0) {
    throw "Background service package restore failed."
}

Write-Host "Restoring notification-area companion packages..."
& dotnet restore $trayProject --force-evaluate
if ($LASTEXITCODE -ne 0) {
    throw "Notification-area companion package restore failed."
}

Write-Host "Publishing Windows Utility by Sajith for $Runtime..."
& dotnet publish $appProject `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $output `
    -p:Version=$buildVersion `
    -p:AssemblyVersion="$buildVersion.0" `
    -p:FileVersion="$buildVersion.0" `
    -p:InformationalVersion=$buildVersion `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -p:SatelliteResourceLanguages=en

if ($LASTEXITCODE -ne 0) {
    throw "Application publishing failed."
}

Write-Host "Publishing Windows Utility background service for $Runtime..."
& dotnet publish $serviceProject `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $output `
    -p:Version=$buildVersion `
    -p:AssemblyVersion="$buildVersion.0" `
    -p:FileVersion="$buildVersion.0" `
    -p:InformationalVersion=$buildVersion `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -p:SatelliteResourceLanguages=en

if ($LASTEXITCODE -ne 0) {
    throw "Background service publishing failed."
}

Write-Host "Publishing Windows Utility service-status tray companion for $Runtime..."
& dotnet publish $trayProject `
    --configuration Release `
    --runtime $Runtime `
    --self-contained true `
    --output $output `
    -p:Version=$buildVersion `
    -p:AssemblyVersion="$buildVersion.0" `
    -p:FileVersion="$buildVersion.0" `
    -p:InformationalVersion=$buildVersion `
    -p:PublishSingleFile=false `
    -p:DebugType=none `
    -p:SatelliteResourceLanguages=en

if ($LASTEXITCODE -ne 0) {
    throw "Notification-area companion publishing failed."
}

# All three projects publish into $output and share a single copy of the .NET runtime.
# Publishing them self-contained AND single-file gave each executable its own embedded copy,
# which tripled the runtime in the installer.
$serviceExe = Join-Path $output "CentricDeviceMonitorService.exe"
if (-not (Test-Path $serviceExe)) {
    throw "The background service executable was not produced."
}

$trayExe = Join-Path $output "CentricDeviceMonitorTray.exe"
if (-not (Test-Path $trayExe)) {
    throw "The notification-area companion executable was not produced."
}

# Debug symbols and XML docs are not needed at runtime and only inflate the installer.
Get-ChildItem $output -Include *.pdb, *.xml -Recurse -ErrorAction SilentlyContinue |
    Remove-Item -Force -ErrorAction SilentlyContinue

$publishedSizeMb = [math]::Round((Get-ChildItem $output -Recurse -File |
    Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host "Published payload size: $publishedSizeMb MB" -ForegroundColor Cyan

$publishedApplication = Join-Path $output "WindowsUtilityBySajith.exe"
$publishedService = Join-Path $output "CentricDeviceMonitorService.exe"
$publishedTray = Join-Path $output "CentricDeviceMonitorTray.exe"
Write-Host "Published application: $publishedApplication" -ForegroundColor Green
Write-Host "Published service: $publishedService" -ForegroundColor Green
Write-Host "Published service-status tray companion: $publishedTray" -ForegroundColor Green

if ($CreateInstaller) {
    if ($Runtime -ne "win-x64") {
        throw "The included installer currently targets win-x64."
    }

    $pawnIoInstaller = Ensure-PawnIOInstaller
    Write-Host "Bundling PawnIO dependency: $pawnIoInstaller" -ForegroundColor Green

    $candidatePaths = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }

    $isccPath = $candidatePaths | Select-Object -First 1
    if (-not $isccPath) {
        throw "Inno Setup 6 was not found. Install it, or run .\create-installer.ps1 -InstallInnoSetup."
    }

    Write-Host "Creating installer..."
    & $isccPath "/DMyAppVersion=$buildVersion" (Join-Path $root "installer\CentricDeviceMonitor.iss")
    if ($LASTEXITCODE -ne 0) {
        throw "Installer creation failed."
    }

    $installer = Get-ChildItem (Join-Path $root "dist\installer") -Filter "WindowsUtilityBySajith-Setup-*.exe" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($installer) {
        Write-Host "Installer created: $($installer.FullName)" -ForegroundColor Green
        Write-Host "The installer registers the Windows Utility background service as LocalSystem with Automatic (Delayed Start)." -ForegroundColor Green
    }
}
