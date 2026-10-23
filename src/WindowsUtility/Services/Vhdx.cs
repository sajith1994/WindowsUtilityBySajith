using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WindowsUtility.Services;

/// <summary>
/// Windows virtual disk files (VHDX) as the backup uses them: made through Windows' Virtual Disk API, because diskpart
/// cannot choose the block size, and read just far enough to tell a file's block size.
/// </summary>
internal static class Vhdx
{
    /// <summary>
    /// The block size images are made with. A dynamic image grows by whole blocks, and so does an incremental image
    /// for every block that changed: with Windows' usual 32 MB a changed 4 KB cost 32 MB. 1 MB is the smallest allowed.
    /// </summary>
    public const uint ImageBlockSize = 1024 * 1024;

    /// <summary>
    /// The largest block size an image may have for an incremental image to be built on it: a piece the comparison
    /// works in. Images made by diskpart (before 2.0.91, or when the API refused) have 32 MB blocks and are not built on.
    /// </summary>
    public const uint LargestParentBlockSize = 4 * 1024 * 1024;

    private static readonly Guid MicrosoftVendor = new("EC984AEC-A0F9-47E9-901F-71415A66345B");
    private static readonly Guid MetadataRegion = new("8B7CA206-4790-4B9A-B8FE-575F050F886E");
    private static readonly Guid FileParameters = new("CAA16737-FA36-4D43-B3B6-33F0AA44E76B");

    /// <summary>
    /// Creates a dynamic (expandable) VHDX of the given virtual size with the given block size, or, with a parent, a
    /// differencing VHDX of the parent's size that stores only what is written to it. The file must not exist yet.
    /// Throws <see cref="IOException"/> when Windows refuses (for example on some network shares).
    /// </summary>
    public static void Create(string path, ulong virtualSize, uint blockSize = ImageBlockSize, uint logicalSectorSize = 512, string? parentPath = null)
    {
        VirtualStorageType type = new() { DeviceId = 3, VendorId = MicrosoftVendor }; // 3: VHDX
        IntPtr parent = parentPath is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(parentPath);
        try
        {
            CreateVirtualDiskParameters parameters = new()
            {
                Version = 2,
                MaximumSize = parentPath is null ? virtualSize : 0, // a differencing disk has its parent's size
                BlockSizeInBytes = blockSize,
                SectorSizeInBytes = parentPath is null ? logicalSectorSize : 0, // and its parent's sectors
                PhysicalSectorSizeInBytes = 0, // Windows' default, 4 KB, as diskpart makes them
                ParentPath = parent,
                ParentVirtualStorageType = parentPath is null ? default : type
            };
            int result = CreateVirtualDisk(ref type, path, 0, IntPtr.Zero, 0, 0, ref parameters, IntPtr.Zero, out SafeFileHandle handle);
            if (result != 0)
            {
                throw new IOException($"Windows could not create the image file: {new Win32Exception(result).Message}");
            }

            handle.Dispose();
        }
        finally
        {
            if (parent != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(parent);
            }
        }
    }

    /// <summary>The block size of a VHDX file; null when the file is not one or cannot be read.</summary>
    public static uint? BlockSize(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!ReadAt(stream, 0, 8, out byte[] signature) || Encoding.ASCII.GetString(signature) != "vhdxfile")
            {
                return null;
            }

            // The region table at 192 KB: "regi", checksum, entry count; then entries of GUID, file offset, length, flags.
            if (!ReadAt(stream, 192 * 1024, 64 * 1024, out byte[] table) || Encoding.ASCII.GetString(table, 0, 4) != "regi")
            {
                return null;
            }

            long metadataOffset = -1;
            uint entries = Math.Min(BitConverter.ToUInt32(table, 8), 2047);
            for (int index = 0; index < entries; index++)
            {
                int entry = 16 + 32 * index;
                if (new Guid(table.AsSpan(entry, 16)) == MetadataRegion)
                {
                    metadataOffset = BitConverter.ToInt64(table, entry + 16);
                }
            }

            // The metadata table: "metadata", reserved, entry count; then entries of item GUID, offset in the region, length.
            if (metadataOffset <= 0 || !ReadAt(stream, metadataOffset, 1024 * 1024, out byte[] metadata) || Encoding.ASCII.GetString(metadata, 0, 8) != "metadata")
            {
                return null;
            }

            int count = Math.Min((int)BitConverter.ToUInt16(metadata, 10), 2047);
            for (int index = 0; index < count; index++)
            {
                int entry = 32 + 32 * index;
                if (new Guid(metadata.AsSpan(entry, 16)) == FileParameters)
                {
                    uint offset = BitConverter.ToUInt32(metadata, entry + 16);
                    return offset + 4 <= metadata.Length ? BitConverter.ToUInt32(metadata, (int)offset) : null;
                }
            }

            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool ReadAt(FileStream stream, long position, int length, out byte[] bytes)
    {
        bytes = new byte[length];
        if (stream.Length < position + Math.Min(length, 64))
        {
            return false;
        }

        stream.Position = position;
        int read = 0;
        while (read < length)
        {
            int got = stream.Read(bytes, read, length - read);
            if (got <= 0)
            {
                break;
            }

            read += got;
        }

        return read >= Math.Min(length, 64);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VirtualStorageType
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    /// <summary>CREATE_VIRTUAL_DISK_PARAMETERS, version 2 (the union with the earlier versions is 8-byte aligned).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct CreateVirtualDiskParameters
    {
        public uint Version;
        private readonly uint _padding;
        public Guid UniqueId;
        public ulong MaximumSize;
        public uint BlockSizeInBytes;
        public uint SectorSizeInBytes;
        public uint PhysicalSectorSizeInBytes;
        private readonly uint _padding2;
        public IntPtr ParentPath;
        public IntPtr SourcePath;
        public uint OpenFlags;
        public VirtualStorageType ParentVirtualStorageType;
        public VirtualStorageType SourceVirtualStorageType;
        public Guid ResiliencyGuid;
    }

    [DllImport("virtdisk.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateVirtualDisk(ref VirtualStorageType virtualStorageType, string path, uint virtualDiskAccessMask, IntPtr securityDescriptor,
        uint flags, uint providerSpecificFlags, ref CreateVirtualDiskParameters parameters, IntPtr overlapped, out SafeFileHandle handle);
}
