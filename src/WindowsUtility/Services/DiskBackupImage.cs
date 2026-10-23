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
    IReadOnlyList<BackupPartition> Partitions,
    string ParentImageFileName = "",
    string BaseImageFileName = "")
{
    /// <summary>
    /// An incremental image holds only what changed since its parent (a VHDX differencing disk): it needs its parent,
    /// and the parent's parent down to the full image, in the same folder. Windows joins them when it is attached.
    /// </summary>
    [JsonIgnore]
    public bool IsIncremental => ParentImageFileName.Length > 0;

    /// <summary>The full image this one builds on (itself, for a full image or one made before 2.0.91).</summary>
    [JsonIgnore]
    public string ChainBase => BaseImageFileName.Length > 0 ? BaseImageFileName : ImageFileName;

    public const int CurrentFormatVersion = 1;
    public const string ImageExtension = ".vhdx";
    public const string DescriptionExtension = ".backup.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Full path of the image file, next to the description file it was loaded from.</summary>
    [JsonIgnore]
    public string ImagePath { get; init; } = string.Empty;

    [JsonIgnore]
    public string Title => $"{ComputerName}, {CreatedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

    /// <summary>This image and every image it builds on, nearest first; stops where one is missing.</summary>
    public List<BackupImage> Chain()
    {
        List<BackupImage> chain = new() { this };
        BackupImage current = this;
        while (current.IsIncremental && chain.Count < 1000)
        {
            BackupImage? parent = Load(Path.Combine(Path.GetDirectoryName(current.ImagePath) ?? string.Empty, current.ParentImageFileName));
            if (parent is null)
            {
                break;
            }

            chain.Add(parent);
            current = parent;
        }

        return chain;
    }

    /// <summary>True when every image this one needs is present in its folder.</summary>
    public bool ChainComplete()
    {
        List<BackupImage> chain = Chain();
        return !chain[^1].IsIncremental && chain.All(image => File.Exists(image.ImagePath));
    }

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
        if (image is null || image.FormatVersion > CurrentFormatVersion || image.Partitions is not { Count: > 0 } || string.IsNullOrEmpty(image.ImageFileName))
        {
            return null;
        }

        image = image with { ParentImageFileName = image.ParentImageFileName ?? string.Empty, BaseImageFileName = image.BaseImageFileName ?? string.Empty };

        return image with { ImagePath = Path.Combine(Path.GetDirectoryName(descriptionPath) ?? string.Empty, image.ImageFileName) };
    }
}
