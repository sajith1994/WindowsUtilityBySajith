param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Install", "Uninstall")]
    [string]$Action,

    [string]$ServiceExe = ""
)

$ErrorActionPreference = "Stop"
$serviceName = "CentricDeviceMonitorService"
$displayName = "Windows Utility by Sajith Background Service"
$description = "Provides unattended device connectivity, CPU temperature, CPU/RAM, network health, and scheduled power monitoring for Windows Utility by Sajith."

function Wait-ServiceState {
    param(
        [string]$Name,
        [string]$DesiredStatus,
        [int]$TimeoutSeconds = 20
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
        if ($null -eq $service) {
            return $DesiredStatus -eq "Deleted"
        }

        if ($service.Status.ToString() -eq $DesiredStatus) {
            return $true
        }

        Start-Sleep -Milliseconds 500
    } while ((Get-Date) -lt $deadline)

    return $false
}

if ($Action -eq "Uninstall") {
    $existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        if ($existing.Status -ne "Stopped") {
            Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
            [void](Wait-ServiceState -Name $serviceName -DesiredStatus "Stopped" -TimeoutSeconds 20)
        }

        & sc.exe delete $serviceName | Out-Null
        Start-Sleep -Milliseconds 700
    }

    exit 0
}

if ([string]::IsNullOrWhiteSpace($ServiceExe) -or -not (Test-Path $ServiceExe)) {
    throw "The service executable was not found: $ServiceExe"
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $existing -and $existing.Status -ne "Stopped") {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    if (-not (Wait-ServiceState -Name $serviceName -DesiredStatus "Stopped" -TimeoutSeconds 20)) {
        throw "The existing Windows Utility background service could not be stopped."
    }
}

$quotedBinaryPath = '"' + $ServiceExe + '"'

if ($null -eq $existing) {
    New-Service `
        -Name $serviceName `
        -BinaryPathName $quotedBinaryPath `
        -DisplayName $displayName `
        -Description $description `
        -StartupType Automatic | Out-Null
}
else {
    & sc.exe config $serviceName binPath= $quotedBinaryPath start= auto obj= LocalSystem DisplayName= $displayName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Windows could not update the service configuration."
    }
}

# Automatic delayed start is stored as an Automatic service plus DelayedAutostart=1.
& sc.exe config $serviceName start= delayed-auto obj= LocalSystem | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "Windows could not enable Automatic (Delayed Start) for the service."
}

$serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
New-ItemProperty -Path $serviceRegistryPath -Name DelayedAutostart -PropertyType DWord -Value 1 -Force | Out-Null
Set-ItemProperty -Path $serviceRegistryPath -Name Description -Value $description

# Restart the service automatically if the monitoring process ever crashes.
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
& sc.exe failureflag $serviceName 1 | Out-Null

Start-Service -Name $serviceName
if (-not (Wait-ServiceState -Name $serviceName -DesiredStatus "Running" -TimeoutSeconds 25)) {
    throw "Windows Utility background service was installed but did not reach the Running state."
}
