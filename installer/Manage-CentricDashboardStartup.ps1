param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Install", "Uninstall", "Run")]
    [string]$Action,

    [string]$ApplicationExe = ""
)

$ErrorActionPreference = "Stop"
$taskName = "Windows Utility by Sajith Dashboard"
$legacyTaskName = "Centric Device Monitor Dashboard"

function Get-InteractiveUser {
    try {
        $computerSystem = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
        if (-not [string]::IsNullOrWhiteSpace($computerSystem.UserName)) {
            return $computerSystem.UserName
        }
    }
    catch {
    }

    $explorer = Get-Process explorer -IncludeUserName -ErrorAction SilentlyContinue |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_.UserName) } |
        Select-Object -First 1
    if ($null -ne $explorer) {
        return $explorer.UserName
    }

    if (-not [string]::IsNullOrWhiteSpace($env:USERDOMAIN) -and
        -not [string]::IsNullOrWhiteSpace($env:USERNAME)) {
        return "$($env:USERDOMAIN)\$($env:USERNAME)"
    }

    throw "The interactive Windows account could not be determined."
}

function Remove-LegacyRunEntry {
    try {
        Remove-ItemProperty `
            -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" `
            -Name "CentricDeviceMonitor" `
            -ErrorAction SilentlyContinue
    }
    catch {
    }
}

if ($Action -eq "Uninstall") {
    & schtasks.exe /Delete /TN $taskName /F 2>$null | Out-Null
    & schtasks.exe /Delete /TN $legacyTaskName /F 2>$null | Out-Null
    Remove-LegacyRunEntry
    exit 0
}

if ($Action -eq "Run") {
    & schtasks.exe /Run /TN $taskName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Windows could not start the Windows Utility dashboard task."
    }
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ApplicationExe) -or -not (Test-Path $ApplicationExe)) {
    throw "The dashboard executable was not found: $ApplicationExe"
}

$user = Get-InteractiveUser
$taskCommand = '"' + $ApplicationExe + '" --startup'

Remove-LegacyRunEntry
& schtasks.exe /Delete /TN $legacyTaskName /F 2>$null | Out-Null

& schtasks.exe /Create `
    /TN $taskName `
    /TR $taskCommand `
    /SC ONLOGON `
    /RU $user `
    /RL HIGHEST `
    /IT `
    /F | Out-Null

if ($LASTEXITCODE -ne 0) {
    throw "Windows could not create the elevated Windows Utility dashboard startup task for $user."
}
