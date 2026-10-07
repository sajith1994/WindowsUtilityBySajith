using System.Diagnostics;
using System.Management;
using System.Security.Principal;
using System.Text;
using CentricDeviceMonitor.Models;

namespace CentricDeviceMonitor.Services;

public sealed class DiskPreparationService
{
    public async Task<IReadOnlyList<DiskPreparationInfo>> GetDisksAsync()
    {
        return await Task.Run(() =>
        {
            List<DiskPreparationInfo> result = new();
            try
            {
                ManagementScope scope = new(@"\\.\root\Microsoft\Windows\Storage");
                scope.Connect();

                ObjectQuery query = new(
                    "SELECT Number, FriendlyName, SerialNumber, Size, AllocatedSize, LargestFreeExtent, NumberOfPartitions, PartitionStyle, BusType, HealthStatus, IsBoot, IsSystem, IsOffline, IsReadOnly FROM MSFT_Disk");
                using ManagementObjectSearcher searcher = new(scope, query);
                ManagementObjectCollection collection = searcher.Get();

                foreach (ManagementObject item in collection)
                {
                    result.Add(new DiskPreparationInfo
                    {
                        Number = Convert.ToInt32(item["Number"] ?? -1),
                        FriendlyName = Clean(Convert.ToString(item["FriendlyName"])) ?? "Unknown disk",
                        SerialNumber = Clean(Convert.ToString(item["SerialNumber"])) ?? string.Empty,
                        SizeBytes = ConvertToUInt64(item["Size"]),
                        AllocatedSizeBytes = ConvertToUInt64(item["AllocatedSize"]),
                        LargestFreeExtentBytes = ConvertToUInt64(item["LargestFreeExtent"]),
                        NumberOfPartitions = ConvertToInt32(item["NumberOfPartitions"]),
                        PartitionStyle = PartitionStyleText(Convert.ToInt32(item["PartitionStyle"] ?? 0)),
                        BusType = BusTypeText(Convert.ToInt32(item["BusType"] ?? 0)),
                        HealthStatus = HealthStatusText(Convert.ToInt32(item["HealthStatus"] ?? 5)),
                        IsBoot = Convert.ToBoolean(item["IsBoot"] ?? false),
                        IsSystem = Convert.ToBoolean(item["IsSystem"] ?? false),
                        IsOffline = Convert.ToBoolean(item["IsOffline"] ?? false),
                        IsReadOnly = Convert.ToBoolean(item["IsReadOnly"] ?? false)
                    });
                }
            }
            catch (Exception exception)
            {
                ApplicationLogService.WriteException("Disk preparation enumeration", exception);
                throw new InvalidOperationException(
                    "Windows Storage Management could not enumerate physical disks. Try running the utility as Administrator.",
                    exception);
            }

            return (IReadOnlyList<DiskPreparationInfo>)result.OrderBy(disk => disk.Number).ToList();
        });
    }

    public async Task<IReadOnlyList<DiskPartitionInfo>> GetPartitionsAsync(int diskNumber)
    {
        return await Task.Run(() =>
        {
            List<DiskPartitionInfo> result = new();
            try
            {
                ManagementScope scope = new(@"\\.\root\Microsoft\Windows\Storage");
                scope.Connect();
                ObjectQuery query = new(
                    $"SELECT DiskNumber, PartitionNumber, DriveLetter, Size, GptType, MbrType, IsBoot, IsSystem, IsActive, IsHidden, IsReadOnly FROM MSFT_Partition WHERE DiskNumber = {diskNumber}");
                using ManagementObjectSearcher searcher = new(scope, query);
                foreach (ManagementObject item in searcher.Get().Cast<ManagementObject>())
                {
                    string driveLetterRaw = Convert.ToString(item["DriveLetter"])?.Trim() ?? string.Empty;
                    char? driveLetter = driveLetterRaw.Length > 0 && char.IsLetter(driveLetterRaw[0])
                        ? char.ToUpperInvariant(driveLetterRaw[0])
                        : null;
                    string gptType = Clean(Convert.ToString(item["GptType"])) ?? string.Empty;
                    int mbrType = ConvertToInt32(item["MbrType"]);

                    result.Add(new DiskPartitionInfo
                    {
                        DiskNumber = Convert.ToInt32(item["DiskNumber"] ?? diskNumber),
                        PartitionNumber = Convert.ToInt32(item["PartitionNumber"] ?? 0),
                        DriveLetter = driveLetter,
                        SizeBytes = ConvertToUInt64(item["Size"]),
                        Type = PartitionTypeText(gptType, mbrType),
                        IsBoot = Convert.ToBoolean(item["IsBoot"] ?? false),
                        IsSystem = Convert.ToBoolean(item["IsSystem"] ?? false),
                        IsActive = Convert.ToBoolean(item["IsActive"] ?? false),
                        IsHidden = Convert.ToBoolean(item["IsHidden"] ?? false),
                        IsReadOnly = Convert.ToBoolean(item["IsReadOnly"] ?? false)
                    });
                }
            }
            catch (Exception exception)
            {
                ApplicationLogService.WriteException($"Partition enumeration for disk {diskNumber}", exception);
                throw new InvalidOperationException($"Windows could not enumerate partitions on Disk {diskNumber}.", exception);
            }

            return (IReadOnlyList<DiskPartitionInfo>)result.OrderBy(partition => partition.PartitionNumber).ToList();
        });
    }

    public async Task<string> CleanDiskAsync(DiskPreparationInfo disk, CancellationToken cancellationToken = default)
    {
        DiskPreparationInfo current = await RequireModifiableDiskAsync(disk.Number, "clean");
        if (string.Equals(current.PartitionStyle, "RAW", StringComparison.OrdinalIgnoreCase))
        {
            return $"Disk {current.Number} is already RAW. There is no partition table to clean.";
        }

        string command = $$"""
            $ErrorActionPreference = 'Stop'
            $disk = Get-Disk -Number {{current.Number}}
            if ($disk.IsBoot -or $disk.IsSystem) { throw 'Refusing to clean a Windows boot/system disk.' }
            if ($disk.IsOffline) { Set-Disk -Number {{current.Number}} -IsOffline $false }
            if ($disk.IsReadOnly) { Set-Disk -Number {{current.Number}} -IsReadOnly $false }
            Clear-Disk -Number {{current.Number}} -RemoveData -RemoveOEM -Confirm:$false
            Start-Sleep -Milliseconds 300
            Update-Disk -Number {{current.Number}} -ErrorAction SilentlyContinue
            Write-Output ('Disk {{current.Number}} cleaned successfully. It is now RAW/uninitialized and ready for GPT or MBR initialization.')
            """;

        return await RunPowerShellHiddenAsync(command, cancellationToken);
    }

    public async Task<string> InitializeDiskAsync(
        DiskPreparationInfo disk,
        string partitionStyle,
        CancellationToken cancellationToken = default)
    {
        DiskPreparationInfo current = await RequireModifiableDiskAsync(disk.Number, "initialize");
        if (!string.Equals(current.PartitionStyle, "RAW", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Disk {current.Number} is already initialized as {current.PartitionStyle}. Use 'Erase + prepare full disk' if you intentionally want to erase and reinitialize it.");
        }

        string normalizedStyle = NormalizePartitionStyle(partitionStyle);
        string command = $$"""
            $ErrorActionPreference = 'Stop'
            $disk = Get-Disk -Number {{current.Number}}
            if ($disk.IsBoot -or $disk.IsSystem) { throw 'Refusing to initialize a Windows boot/system disk.' }
            if ($disk.IsOffline) { Set-Disk -Number {{current.Number}} -IsOffline $false }
            if ($disk.IsReadOnly) { Set-Disk -Number {{current.Number}} -IsReadOnly $false }
            $disk = Get-Disk -Number {{current.Number}}
            if ($disk.PartitionStyle -ne 'RAW') { throw ('Disk is already initialized as ' + $disk.PartitionStyle + '.') }
            Initialize-Disk -Number {{current.Number}} -PartitionStyle {{normalizedStyle}} -PassThru | Out-Null
            Start-Sleep -Milliseconds 250
            Update-Disk -Number {{current.Number}} -ErrorAction SilentlyContinue
            Write-Output ('Disk {{current.Number}} initialized as {{normalizedStyle}}. You can now create one or more partitions.')
            """;

        return await RunPowerShellHiddenAsync(command, cancellationToken);
    }

    public async Task<string> CreatePartitionAsync(
        DiskPreparationInfo disk,
        ulong? sizeBytes,
        string fileSystem,
        string volumeLabel,
        CancellationToken cancellationToken = default)
    {
        DiskPreparationInfo current = await RequireModifiableDiskAsync(disk.Number, "create a partition on");
        if (string.Equals(current.PartitionStyle, "RAW", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This disk is RAW. Initialize it as GPT or MBR before creating partitions.");
        }

        if (!string.Equals(current.PartitionStyle, "GPT", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(current.PartitionStyle, "MBR", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Disk {current.Number} has unsupported partition style '{current.PartitionStyle}'.");
        }

        (string normalizedFileSystem, string safeLabel) = NormalizeVolumeOptions(fileSystem, volumeLabel);
        string sizeCommand = sizeBytes.HasValue
            ? $"$requestedSize = [UInt64]{sizeBytes.Value}; $partition = New-Partition -DiskNumber {current.Number} -Size $requestedSize -AssignDriveLetter"
            : $"$partition = New-Partition -DiskNumber {current.Number} -UseMaximumSize -AssignDriveLetter";
        string sizeValidationCommand = sizeBytes.HasValue
            ? $"if ([UInt64]{sizeBytes.Value} -gt [UInt64]$disk.LargestFreeExtent) {{ throw ('Requested partition size is larger than the largest free extent (' + [math]::Round($disk.LargestFreeExtent / 1GB, 2) + ' GB).') }}"
            : string.Empty;

        string command = $$"""
            $ErrorActionPreference = 'Stop'
            $disk = Get-Disk -Number {{current.Number}}
            if ($disk.IsBoot -or $disk.IsSystem) { throw 'Refusing to modify a Windows boot/system disk.' }
            if ($disk.IsOffline) { Set-Disk -Number {{current.Number}} -IsOffline $false }
            if ($disk.IsReadOnly) { Set-Disk -Number {{current.Number}} -IsReadOnly $false }
            $disk = Get-Disk -Number {{current.Number}}
            if ($disk.PartitionStyle -eq 'RAW') { throw 'Disk is RAW and must be initialized first.' }
            if ($disk.LargestFreeExtent -lt 8MB) { throw 'There is no usable unallocated space on this disk.' }
            {{sizeValidationCommand}}
            {{sizeCommand}}
            $volume = Format-Volume -Partition $partition -FileSystem {{normalizedFileSystem}} -NewFileSystemLabel '{{safeLabel}}' -Confirm:$false -Force
            $partition = Get-Partition -DiskNumber {{current.Number}} -PartitionNumber $partition.PartitionNumber
            if (-not $partition.DriveLetter) {
                Add-PartitionAccessPath -DiskNumber {{current.Number}} -PartitionNumber $partition.PartitionNumber -AssignDriveLetter
                $partition = Get-Partition -DiskNumber {{current.Number}} -PartitionNumber $partition.PartitionNumber
            }
            $letter = if ($partition.DriveLetter) { [string]$partition.DriveLetter + ':' } else { '(no drive letter)' }
            Write-Output ('Created and formatted Partition ' + $partition.PartitionNumber + ' on Disk {{current.Number}} as ' + $letter + ' (' + [math]::Round($partition.Size / 1GB, 2) + ' GB, {{normalizedFileSystem}}).')
            """;

        return await RunPowerShellHiddenAsync(command, cancellationToken);
    }

    public async Task<string> FormatPartitionAsync(
        DiskPreparationInfo disk,
        DiskPartitionInfo partition,
        string fileSystem,
        string volumeLabel,
        CancellationToken cancellationToken = default)
    {
        DiskPreparationInfo current = await RequireModifiableDiskAsync(disk.Number, "format a partition on");
        DiskPartitionInfo? currentPartition = (await GetPartitionsAsync(current.Number))
            .FirstOrDefault(item => item.PartitionNumber == partition.PartitionNumber);
        if (currentPartition is null)
        {
            throw new InvalidOperationException($"Partition {partition.PartitionNumber} is no longer present on Disk {current.Number}.");
        }

        if (currentPartition.IsProtected)
        {
            throw new InvalidOperationException("Windows Utility will not format a boot or system partition.");
        }

        (string normalizedFileSystem, string safeLabel) = NormalizeVolumeOptions(fileSystem, volumeLabel);
        string command = $$"""
            $ErrorActionPreference = 'Stop'
            $disk = Get-Disk -Number {{current.Number}}
            if ($disk.IsBoot -or $disk.IsSystem) { throw 'Refusing to modify a Windows boot/system disk.' }
            if ($disk.IsOffline) { Set-Disk -Number {{current.Number}} -IsOffline $false }
            if ($disk.IsReadOnly) { Set-Disk -Number {{current.Number}} -IsReadOnly $false }
            $partition = Get-Partition -DiskNumber {{current.Number}} -PartitionNumber {{currentPartition.PartitionNumber}}
            if ($partition.IsBoot -or $partition.IsSystem) { throw 'Refusing to format a Windows boot/system partition.' }
            if ($partition.IsReadOnly) { Set-Partition -DiskNumber {{current.Number}} -PartitionNumber {{currentPartition.PartitionNumber}} -IsReadOnly $false }
            $volume = Format-Volume -Partition $partition -FileSystem {{normalizedFileSystem}} -NewFileSystemLabel '{{safeLabel}}' -Confirm:$false -Force
            $partition = Get-Partition -DiskNumber {{current.Number}} -PartitionNumber {{currentPartition.PartitionNumber}}
            if (-not $partition.DriveLetter) {
                Add-PartitionAccessPath -DiskNumber {{current.Number}} -PartitionNumber {{currentPartition.PartitionNumber}} -AssignDriveLetter
                $partition = Get-Partition -DiskNumber {{current.Number}} -PartitionNumber {{currentPartition.PartitionNumber}}
            }
            $letter = if ($partition.DriveLetter) { [string]$partition.DriveLetter + ':' } else { '(no drive letter)' }
            Write-Output ('Formatted Partition {{currentPartition.PartitionNumber}} on Disk {{current.Number}} as {{normalizedFileSystem}}. Drive: ' + $letter + '.')
            """;

        return await RunPowerShellHiddenAsync(command, cancellationToken);
    }

    public async Task<string> DeletePartitionAsync(
        DiskPreparationInfo disk,
        DiskPartitionInfo partition,
        CancellationToken cancellationToken = default)
    {
        DiskPreparationInfo current = await RequireModifiableDiskAsync(disk.Number, "delete a partition from");
        DiskPartitionInfo? currentPartition = (await GetPartitionsAsync(current.Number))
            .FirstOrDefault(item => item.PartitionNumber == partition.PartitionNumber);
        if (currentPartition is null)
        {
            throw new InvalidOperationException($"Partition {partition.PartitionNumber} is no longer present on Disk {current.Number}.");
        }

        if (currentPartition.IsProtected)
        {
            throw new InvalidOperationException("Windows Utility will not delete a boot or system partition.");
        }

        string command = $$"""
            $ErrorActionPreference = 'Stop'
            $disk = Get-Disk -Number {{current.Number}}
            if ($disk.IsBoot -or $disk.IsSystem) { throw 'Refusing to modify a Windows boot/system disk.' }
            if ($disk.IsOffline) { Set-Disk -Number {{current.Number}} -IsOffline $false }
            if ($disk.IsReadOnly) { Set-Disk -Number {{current.Number}} -IsReadOnly $false }
            $partition = Get-Partition -DiskNumber {{current.Number}} -PartitionNumber {{currentPartition.PartitionNumber}}
            if ($partition.IsBoot -or $partition.IsSystem) { throw 'Refusing to delete a Windows boot/system partition.' }
            Remove-Partition -DiskNumber {{current.Number}} -PartitionNumber {{currentPartition.PartitionNumber}} -Confirm:$false
            Update-Disk -Number {{current.Number}} -ErrorAction SilentlyContinue
            Write-Output ('Deleted Partition {{currentPartition.PartitionNumber}} from Disk {{current.Number}}. Its space is now unallocated and can be used for a new partition.')
            """;

        return await RunPowerShellHiddenAsync(command, cancellationToken);
    }

    public async Task<string> EraseAndPrepareFullDiskAsync(
        DiskPreparationInfo disk,
        string partitionStyle,
        string fileSystem,
        string volumeLabel,
        CancellationToken cancellationToken = default)
    {
        DiskPreparationInfo current = await RequireModifiableDiskAsync(disk.Number, "erase or prepare");
        string normalizedStyle = NormalizePartitionStyle(partitionStyle);
        (string normalizedFileSystem, string safeLabel) = NormalizeVolumeOptions(fileSystem, volumeLabel);

        string command = $$"""
            $ErrorActionPreference = 'Stop'
            $disk = Get-Disk -Number {{current.Number}}
            if ($disk.IsBoot -or $disk.IsSystem) { throw 'Refusing to erase a Windows boot/system disk.' }
            if ($disk.IsOffline) { Set-Disk -Number {{current.Number}} -IsOffline $false }
            if ($disk.IsReadOnly) { Set-Disk -Number {{current.Number}} -IsReadOnly $false }
            $disk = Get-Disk -Number {{current.Number}}
            if ($disk.PartitionStyle -ne 'RAW') {
                Clear-Disk -Number {{current.Number}} -RemoveData -RemoveOEM -Confirm:$false
                Start-Sleep -Milliseconds 350
            }
            $disk = Get-Disk -Number {{current.Number}}
            if ($disk.IsReadOnly) { Set-Disk -Number {{current.Number}} -IsReadOnly $false }
            if ($disk.IsOffline) { Set-Disk -Number {{current.Number}} -IsOffline $false }
            $disk = Get-Disk -Number {{current.Number}}
            if ($disk.PartitionStyle -ne 'RAW') { throw ('Could not return Disk {{current.Number}} to RAW state. Current style: ' + $disk.PartitionStyle) }
            Initialize-Disk -Number {{current.Number}} -PartitionStyle {{normalizedStyle}} -PassThru | Out-Null
            Start-Sleep -Milliseconds 250
            $partition = New-Partition -DiskNumber {{current.Number}} -UseMaximumSize -AssignDriveLetter
            $volume = Format-Volume -Partition $partition -FileSystem {{normalizedFileSystem}} -NewFileSystemLabel '{{safeLabel}}' -Confirm:$false -Force
            $partition = Get-Partition -DiskNumber {{current.Number}} -PartitionNumber $partition.PartitionNumber
            $letter = if ($partition.DriveLetter) { [string]$partition.DriveLetter + ':' } else { '(no drive letter)' }
            Write-Output ('Disk {{current.Number}} was erased, initialized as {{normalizedStyle}}, and prepared as one full-size {{normalizedFileSystem}} volume ' + $letter + ' (' + [math]::Round($partition.Size / 1GB, 2) + ' GB).')
            """;

        return await RunPowerShellHiddenAsync(command, cancellationToken);
    }

    private async Task<DiskPreparationInfo> RequireModifiableDiskAsync(int diskNumber, string operation)
    {
        if (!IsAdministrator())
        {
            throw new InvalidOperationException("Administrator privileges are required for physical disk operations.");
        }

        DiskPreparationInfo? current = (await GetDisksAsync()).FirstOrDefault(item => item.Number == diskNumber);
        if (current is null)
        {
            throw new InvalidOperationException($"Disk {diskNumber} is no longer connected.");
        }

        if (current.IsProtected)
        {
            throw new InvalidOperationException($"Windows Utility will never {operation} a boot or system disk.");
        }

        return current;
    }

    private static string NormalizePartitionStyle(string partitionStyle) =>
        string.Equals(partitionStyle, "MBR", StringComparison.OrdinalIgnoreCase) ? "MBR" : "GPT";

    private static (string FileSystem, string SafeLabel) NormalizeVolumeOptions(string fileSystem, string volumeLabel)
    {
        string normalizedFileSystem = string.Equals(fileSystem, "exFAT", StringComparison.OrdinalIgnoreCase) ? "exFAT" : "NTFS";
        string safeLabel = string.IsNullOrWhiteSpace(volumeLabel) ? "New Volume" : volumeLabel.Trim();
        if (safeLabel.Length > 32)
        {
            safeLabel = safeLabel[..32];
        }

        safeLabel = safeLabel.Replace("'", "''", StringComparison.Ordinal);
        return (normalizedFileSystem, safeLabel);
    }

    private static async Task<string> RunPowerShellHiddenAsync(string command, CancellationToken cancellationToken)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        ProcessStartInfo startInfo = new("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}"
        };

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("PowerShell could not be started.");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        string stdout = (await stdoutTask).Trim();
        string stderr = (await stderrTask).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? $"Disk operation failed with PowerShell exit code {process.ExitCode}."
                : stderr);
        }

        return string.IsNullOrWhiteSpace(stdout) ? "Disk operation completed successfully." : stdout;
    }

    private static bool IsAdministrator()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static int ConvertToInt32(object? value)
    {
        try
        {
            return value is null ? 0 : Convert.ToInt32(value);
        }
        catch
        {
            return 0;
        }
    }

    private static ulong ConvertToUInt64(object? value)
    {
        try
        {
            return value is null ? 0UL : Convert.ToUInt64(value);
        }
        catch
        {
            return 0UL;
        }
    }

    private static string PartitionStyleText(int value) => value switch
    {
        // MSFT_Disk reports 0 for an uninitialized/unknown partition style.
        // In this disk-preparation UI, that is the RAW state accepted by Initialize-Disk.
        0 => "RAW",
        1 => "MBR",
        2 => "GPT",
        _ => "Unknown"
    };

    private static string PartitionTypeText(string gptType, int mbrType)
    {
        if (!string.IsNullOrWhiteSpace(gptType))
        {
            return gptType.ToUpperInvariant() switch
            {
                "{EBD0A0A2-B9E5-4433-87C0-68B6B72699C7}" => "Basic data",
                "{C12A7328-F81F-11D2-BA4B-00A0C93EC93B}" => "EFI system",
                "{DE94BBA4-06D1-4D40-A16A-BFD50179D6AC}" => "Recovery",
                "{E3C9E316-0B5C-4DB8-817D-F92DF00215AE}" => "Microsoft reserved",
                _ => "GPT partition"
            };
        }

        return mbrType switch
        {
            7 => "NTFS/exFAT",
            11 or 12 => "FAT32",
            39 => "Recovery",
            _ when mbrType > 0 => $"MBR type 0x{mbrType:X2}",
            _ => "Partition"
        };
    }

    private static string HealthStatusText(int value) => value switch
    {
        0 => "Healthy",
        1 => "Warning",
        2 => "Unhealthy",
        _ => "Unknown"
    };

    private static string BusTypeText(int value) => value switch
    {
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "1394",
        5 => "SSA",
        6 => "Fibre Channel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        15 => "File-backed virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        18 => "SCM",
        19 => "UFS",
        _ => "Unknown"
    };
}
