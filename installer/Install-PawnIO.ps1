param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,
    [Parameter(Mandatory = $true)]
    [string]$RebootMarkerPath
)

$ErrorActionPreference = "Stop"
$requiredVersion = [version]"2.2.0"

function Get-PawnIOVersion {
    $paths = @(
        "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO",
        "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO"
    )

    foreach ($path in $paths) {
        try {
            $value = (Get-ItemProperty -Path $path -Name DisplayVersion -ErrorAction Stop).DisplayVersion
            if ($value) {
                try {
                    return [version]([string]$value)
                }
                catch {
                }
            }
        }
        catch {
        }
    }

    return $null
}

$currentVersion = Get-PawnIOVersion
if ($null -ne $currentVersion -and $currentVersion -ge $requiredVersion) {
    Write-Host "PawnIO $currentVersion is already installed."
    exit 0
}

if (-not (Test-Path -LiteralPath $InstallerPath)) {
    throw "PawnIO installer was not found at $InstallerPath"
}

Write-Host "Installing signed PawnIO hardware access driver $requiredVersion..."
$process = Start-Process -FilePath $InstallerPath -ArgumentList "-install -silent" -Wait -PassThru
if ($process.ExitCode -eq 0) {
    exit 0
}

if ($process.ExitCode -eq 3010) {
    Set-Content -LiteralPath $RebootMarkerPath -Value "PawnIO requested a reboot." -Encoding ASCII
    exit 0
}

throw "PawnIO installer failed with exit code $($process.ExitCode)."
