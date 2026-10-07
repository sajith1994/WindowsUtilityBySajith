using System.Runtime.InteropServices;

namespace CentricDeviceMonitor.Services;

/// <summary>
/// A pinned, sector-aligned byte buffer.
/// </summary>
/// <remarks>
/// Windows rejects reads and writes on a handle opened with FILE_FLAG_NO_BUFFERING unless the
/// user buffer starts on a sector boundary. A plain <c>byte[]</c> gives no such guarantee, so
/// this type over-allocates, pins the array, and exposes the aligned window inside it. Pinning
/// also keeps the address stable for the lifetime of the transfer, which matters because the
/// garbage collector would otherwise be free to relocate the array mid-flight.
///
/// This avoids needing <c>AllowUnsafeBlocks</c> in the project file.
/// </remarks>
internal sealed class AlignedIoBuffer : IDisposable
{
    private readonly byte[] _raw;
    private readonly int _offset;
    private readonly int _length;
    private GCHandle _handle;
    private bool _disposed;

    public AlignedIoBuffer(int length, int alignment = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(alignment);

        _raw = new byte[length + alignment];
        _handle = GCHandle.Alloc(_raw, GCHandleType.Pinned);

        long address = _handle.AddrOfPinnedObject().ToInt64();
        _offset = (int)((alignment - (address % alignment)) % alignment);
        _length = length;
    }

    public int Length => _length;

    public Memory<byte> Memory => _raw.AsMemory(_offset, _length);

    public Span<byte> Span => _raw.AsSpan(_offset, _length);

    public Memory<byte> Slice(int count) => _raw.AsMemory(_offset, count);

    public Span<byte> SpanSlice(int count) => _raw.AsSpan(_offset, count);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_handle.IsAllocated)
        {
            _handle.Free();
        }
    }
}
