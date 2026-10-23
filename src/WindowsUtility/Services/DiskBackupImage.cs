using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsUtility.Services;

/// <summary>One partition inside a backup image, as it was on the backed-up disk, and about how much data it held.</summary>
public sealed record BackupPartition(ClonePartition Partition, long BytesCopied);

/// <summary>
/// What a backup holds, saved as JSON next to the image file. The image is a dynamic VHDX made by Windows (it grows
/// only with the data written), holding the chosen partitions at their original places, so Windows can also attach it
/// as a disk and files can be copied out of it. The description remembers which partition is Windows and its letter,
/// which the image itself cannot tell.
/// </summary>
public sealed record BackupImage(
    int FormatVersion,
    DateTime CreatedUtc,
    string ComputerName,
    string ImageFileName,
    string DiskName,
    ulong DiskSize,
    string DiskStyle,
    int SectorSize,
    bool Verified,
    IReadOnlyList<BackupPartition> Partitions)
{
    public const int CurrentFormatVersion = 1;
    public const string ImageExtension = ".vhdx";
    public const string DescriptionExtension = ".backup.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Full path of the image file, next to the description file it was loaded from.</summary>
    [JsonIgnore]
    public string ImagePath { get; init; } = string.Empty;

    [JsonIgnore]
    public string Title => $"{ComputerName}, {CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

    [JsonIgnore]
    public long BytesCopied => Partitions.Sum(partition => partition.BytesCopied);

    [JsonIgnore]
    public bool HasWindows => Partitions.Any(partition => partition.Partition.IsBoot);

    /// <summary>The backed-up disk as the clone planner sees it, so a restore is planned like a clone from it.</summary>
    public CloneDisk ToDisk() => new(-1, $"Backup of {DiskName}", DiskSize, DiskStyle, SectorSize, false, false, "Backup",
        Partitions.Select(partition => partition.Partition).OrderBy(partition => partition.Offset).ToList());

    public static string DescriptionPathFor(string imagePath) =>
        Path.Combine(Path.GetDirectoryName(imagePath) ?? string.Empty, Path.GetFileNameWithoutExtension(imagePath) + DescriptionExtension);

    public void Save(string descriptionPath) => File.WriteAllText(descriptionPath, JsonSerializer.Serialize(this, JsonOptions));

    /// <summary>Loads a backup from its image or description file; null when the file is not one of ours.</summary>
    public static BackupImage? Load(string path)
    {
        string descriptionPath = path.EndsWith(DescriptionExtension, StringComparison.OrdinalIgnoreCase) ? path : DescriptionPathFor(path);
        if (!File.Exists(descriptionPath))
        {
            return null;
        }

        BackupImage? image = JsonSerializer.Deserialize<BackupImage>(File.ReadAllText(descriptionPath));
        if (image is null || image.FormatVersion > CurrentFormatVersion || image.Partitions.Count == 0)
        {
            return null;
        }

        return image with { ImagePath = Path.Combine(Path.GetDirectoryName(descriptionPath) ?? string.Empty, image.ImageFileName) };
    }
}
