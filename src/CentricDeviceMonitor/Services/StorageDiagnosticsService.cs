using System.Diagnostics;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CentricDeviceMonitor.Models;
using Microsoft.Win32.SafeHandles;
using System.IO;

namespace CentricDeviceMonitor.Services;

public sealed class StorageDiagnosticsService
{
    /// <summary>
    /// Sequential benchmark profile. Mirrors CrystalDiskMark's default SEQ1M Q8T1 test so the
    /// numbers can be compared directly: 1 MiB blocks, eight outstanding requests, one thread.
    /// </summary>
    private const int BenchmarkBlockSize = 1024 * 1024;

    private const int BenchmarkQueueDepth = 8;

    /// <summary>
    /// Capacity-verification profile. Larger blocks because this test is throughput-bound rather
    /// than latency-bound, and it has to move the whole free-space region twice.
    /// </summary>
    private const int CapacityBlockSize = 8 * 1024 * 1024;

    private const int CapacityQueueDepth = 8;

    /// <summary>FILE_FLAG_NO_BUFFERING. Bypasses the Windows page cache so results reflect the drive.</summary>
    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    private const int SectorAlignment = 4096;

    /// <summary>
    /// Size of each capacity test file. Every file boundary drains the I/O queue and stalls the
    /// drive, so this is deliberately large: a 10 GB run crosses two boundaries instead of
    /// nineteen. The next file's handle is also opened while the current one is still writing.
    /// </summary>
    private const long CapacityFileChunkBytes = 4L * 1024L * 1024L * 1024L;
    private static readonly string BenchmarkLogPath = Path.Combine(SharedDataPaths.LogsDirectory, "storage-benchmarks.csv");
    private static readonly string CapacityVerificationLogPath = Path.Combine(SharedDataPaths.LogsDirectory, "storage-capacity-verification.csv");

    public IReadOnlyList<StorageVolumeInfo> GetTestableVolumes()
    {
        List<StorageVolumeInfo> volumes = new();
        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is DriveType.CDRom or DriveType.Network or DriveType.NoRootDirectory)
                {
                    continue;
                }

                volumes.Add(new StorageVolumeInfo
                {
                    RootPath = drive.RootDirectory.FullName,
                    Label = drive.VolumeLabel,
                    DriveType = drive.DriveType.ToString(),
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace
                });
            }
            catch
            {
                // A transiently unavailable volume should not block the window.
            }
        }

        return volumes.OrderBy(volume => volume.RootPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyList<StorageDriveHealth>> GetPhysicalDriveHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            IReadOnlyList<StorageDriveHealth> drives = await GetStorageModuleHealthAsync(cancellationToken);
            if (drives.Count > 0)
            {
                return drives;
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Storage reliability counters", exception);
        }

        return await Task.Run(GetWmiFallbackHealth, cancellationToken);
    }

    public async Task<StorageBenchmarkResult> RunSequentialBenchmarkAsync(
        StorageVolumeInfo volume,
        int testSizeMegabytes,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        testSizeMegabytes = Math.Clamp(testSizeMegabytes, 64, 32768);

        long requestedBytes = (long)testSizeMegabytes * 1024L * 1024L;
        long safetyReserve = Math.Max(512L * 1024L * 1024L, Math.Min(requestedBytes / 4, 2L * 1024L * 1024L * 1024L));
        DriveInfo drive = new(volume.RootPath);
        if (!drive.IsReady)
        {
            throw new IOException($"The volume {volume.RootPath} is not ready.");
        }

        if (drive.AvailableFreeSpace < requestedBytes + safetyReserve)
        {
            throw new IOException($"Not enough free space. The test requires about {testSizeMegabytes} MB plus a safety reserve of {safetyReserve / 1024d / 1024d:0} MB.");
        }

        string benchmarkPath = Path.Combine(volume.RootPath, $"CentricDeviceMonitor-Benchmark-{Guid.NewGuid():N}.tmp");

        // Probe once so a volume that refuses unbuffered I/O (some removable media, network-backed
        // volumes, filesystems with an odd sector size) falls back cleanly instead of failing.
        bool unbuffered = await SupportsUnbufferedIoAsync(volume.RootPath, cancellationToken).ConfigureAwait(false);

        try
        {
            // Write and read are timed against separate handles so neither inherits the other's
            // state, matching how CrystalDiskMark runs its sequential passes.
            Stopwatch writeWatch = Stopwatch.StartNew();
            using (SafeFileHandle writeHandle = OpenWriteHandle(benchmarkPath, requestedBytes, unbuffered))
            {
                await WriteSequentialAsync(
                    writeHandle,
                    requestedBytes,
                    BenchmarkBlockSize,
                    BenchmarkQueueDepth,
                    flushAtEnd: !unbuffered,
                    fillEachBlock: false,
                    seed: 0,
                    onProgress: written => progress?.Report((written / (double)requestedBytes) * 0.5),
                    cancellationToken).ConfigureAwait(false);
            }
            writeWatch.Stop();

            Stopwatch readWatch = Stopwatch.StartNew();
            using (SafeFileHandle readHandle = OpenReadHandle(benchmarkPath, unbuffered))
            {
                await ReadSequentialAsync(
                    readHandle,
                    requestedBytes,
                    BenchmarkBlockSize,
                    BenchmarkQueueDepth,
                    verifyBlock: null,
                    onProgress: read => progress?.Report(0.5 + ((read / (double)requestedBytes) * 0.5)),
                    cancellationToken).ConfigureAwait(false);
            }
            readWatch.Stop();

            ApplicationLogService.WriteMessage(
                "Storage benchmark",
                $"{volume.RootPath} SEQ{BenchmarkBlockSize / 1024}K Q{BenchmarkQueueDepth}T1 using " +
                $"{(unbuffered ? "unbuffered" : "buffered")} I/O.");

            double sizeMb = requestedBytes / 1024d / 1024d;
            StorageBenchmarkResult result = new()
            {
                RecordedAt = DateTime.Now,
                Volume = volume.RootPath,
                TestSizeMegabytes = testSizeMegabytes,
                CacheBypassed = unbuffered,
                Profile = $"SEQ{BenchmarkBlockSize / 1024}K Q{BenchmarkQueueDepth}T1",
                SequentialWriteMegabytesPerSecond = sizeMb / Math.Max(writeWatch.Elapsed.TotalSeconds, 0.001),
                SequentialReadMegabytesPerSecond = sizeMb / Math.Max(readWatch.Elapsed.TotalSeconds, 0.001)
            };

            await AppendBenchmarkLogAsync(result, cancellationToken);
            progress?.Report(1.0);
            return result;
        }
        finally
        {
            try
            {
                if (File.Exists(benchmarkPath))
                {
                    File.Delete(benchmarkPath);
                }
            }
            catch (Exception exception)
            {
                ApplicationLogService.WriteException("Delete storage benchmark file", exception);
            }
        }
    }

    public string GetBenchmarkLogPath() => BenchmarkLogPath;

    /// <summary>Opens a write handle, preallocating so the filesystem does not extend it per transfer.</summary>
    private static SafeFileHandle OpenWriteHandle(string path, long totalBytes, bool unbuffered)
    {
        FileOptions options = FileOptions.Asynchronous | FileOptions.SequentialScan;
        options |= unbuffered ? NoBuffering : FileOptions.WriteThrough;
        return File.OpenHandle(path, FileMode.Create, FileAccess.Write, FileShare.None, options, totalBytes);
    }

    /// <summary>Opens a read handle using the same caching mode as the matching write.</summary>
    private static SafeFileHandle OpenReadHandle(string path, bool unbuffered)
    {
        FileOptions options = FileOptions.Asynchronous | FileOptions.SequentialScan;
        if (unbuffered)
        {
            options |= NoBuffering;
        }

        return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, options);
    }

    /// <summary>
    /// Writes <paramref name="totalBytes"/> to an already-open handle, keeping
    /// <paramref name="queueDepth"/> transfers in flight so the device is actually kept busy.
    /// </summary>
    /// <param name="fillEachBlock">
    /// When false the buffers are filled once up front, so the measurement excludes data-generation
    /// cost. The capacity test sets this to true because each block must carry unique content.
    /// </param>
    private static async Task WriteSequentialAsync(
        SafeFileHandle handle,
        long totalBytes,
        int blockSize,
        int queueDepth,
        bool flushAtEnd,
        bool fillEachBlock,
        ulong seed,
        Action<long>? onProgress,
        CancellationToken cancellationToken,
        long startingBlockIndex = 0)
    {
        AlignedIoBuffer[] buffers = new AlignedIoBuffer[queueDepth];
        Task?[] inFlight = new Task?[queueDepth];

        try
        {
            for (int slot = 0; slot < queueDepth; slot++)
            {
                buffers[slot] = new AlignedIoBuffer(blockSize, SectorAlignment);
                if (!fillEachBlock)
                {
                    FastBlockGenerator.Fill(buffers[slot].Span, seed, slot);
                }
            }

            long offset = 0;
            long blockIndex = startingBlockIndex;
            int current = 0;

            while (offset < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Wait for this slot's previous transfer before reusing its buffer.
                if (inFlight[current] is Task pending)
                {
                    await pending.ConfigureAwait(false);
                    inFlight[current] = null;
                }

                int count = (int)Math.Min(blockSize, totalBytes - offset);
                if (fillEachBlock)
                {
                    FastBlockGenerator.Fill(buffers[current].SpanSlice(count), seed, blockIndex);
                }

                inFlight[current] = RandomAccess
                    .WriteAsync(handle, buffers[current].Slice(count), offset, cancellationToken)
                    .AsTask();

                offset += count;
                blockIndex++;
                onProgress?.Invoke(offset);
                current = (current + 1) % queueDepth;
            }

            foreach (Task? task in inFlight)
            {
                if (task is not null)
                {
                    await task.ConfigureAwait(false);
                }
            }

            // Unbuffered writes are already on the media; buffered ones need an explicit flush
            // so the timing does not simply measure the Windows write-back cache.
            if (flushAtEnd)
            {
                RandomAccess.FlushToDisk(handle);
            }
        }
        finally
        {
            // Any transfer still in flight is writing into pinned memory. Drain before unpinning,
            // otherwise a cancellation or early return frees the pin underneath the kernel.
            foreach (Task? task in inFlight)
            {
                if (task is not null)
                {
                    try
                    {
                        await task.ConfigureAwait(false);
                    }
                    catch
                    {
                        // The original failure is the one worth propagating.
                    }
                }
            }

            foreach (AlignedIoBuffer buffer in buffers)
            {
                buffer?.Dispose();
            }
        }
    }

    /// <summary>
    /// Reads <paramref name="totalBytes"/> from an already-open handle at queue depth, optionally
    /// verifying each block. <paramref name="verifyBlock"/> receives the block index and the bytes
    /// just read, and returns false to abort.
    /// </summary>
    private static async Task ReadSequentialAsync(
        SafeFileHandle handle,
        long totalBytes,
        int blockSize,
        int queueDepth,
        Func<long, Memory<byte>, bool>? verifyBlock,
        Action<long>? onProgress,
        CancellationToken cancellationToken,
        long startingBlockIndex = 0)
    {
        AlignedIoBuffer[] buffers = new AlignedIoBuffer[queueDepth];
        Task<int>?[] inFlight = new Task<int>?[queueDepth];
        long[] slotBlockIndex = new long[queueDepth];
        int[] slotCount = new int[queueDepth];

        try
        {
            for (int slot = 0; slot < queueDepth; slot++)
            {
                buffers[slot] = new AlignedIoBuffer(blockSize, SectorAlignment);
            }

            long offset = 0;
            long blockIndex = startingBlockIndex;
            long completed = 0;
            int current = 0;

            while (offset < totalBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (inFlight[current] is Task<int> pending)
                {
                    int read = await pending.ConfigureAwait(false);
                    inFlight[current] = null;
                    completed += read;

                    if (verifyBlock is not null
                        && !verifyBlock(slotBlockIndex[current], buffers[current].Slice(slotCount[current])))
                    {
                        return;
                    }

                    onProgress?.Invoke(completed);
                }

                int count = (int)Math.Min(blockSize, totalBytes - offset);
                slotBlockIndex[current] = blockIndex;
                slotCount[current] = count;
                inFlight[current] = RandomAccess
                    .ReadAsync(handle, buffers[current].Slice(count), offset, cancellationToken)
                    .AsTask();

                offset += count;
                blockIndex++;
                current = (current + 1) % queueDepth;
            }

            // Drain the remaining transfers in issue order so verification stays sequential.
            for (int drained = 0; drained < queueDepth; drained++)
            {
                int slot = (current + drained) % queueDepth;
                if (inFlight[slot] is not Task<int> pending)
                {
                    continue;
                }

                int read = await pending.ConfigureAwait(false);
                inFlight[slot] = null;
                completed += read;

                if (verifyBlock is not null
                    && !verifyBlock(slotBlockIndex[slot], buffers[slot].Slice(slotCount[slot])))
                {
                    return;
                }

                onProgress?.Invoke(completed);
            }

            // A short read means the file was smaller than expected, so the remaining transfers
            // completed instantly and the elapsed time no longer corresponds to the byte count.
            // Reporting that as throughput produces an impossibly high number, so fail instead.
            if (completed < totalBytes)
            {
                throw new IOException(
                    $"Read {completed:N0} of {totalBytes:N0} expected bytes; timing for this pass is not valid.");
            }
        }
        finally
        {
            foreach (Task<int>? task in inFlight)
            {
                if (task is not null)
                {
                    try
                    {
                        await task.ConfigureAwait(false);
                    }
                    catch
                    {
                        // The original failure is the one worth propagating.
                    }
                }
            }

            foreach (AlignedIoBuffer buffer in buffers)
            {
                buffer?.Dispose();
            }
        }
    }

    /// <summary>
    /// Returns true when the volume accepts FILE_FLAG_NO_BUFFERING for both writing and reading.
    /// </summary>
    /// <remarks>
    /// The read side matters more than the write side and has to be probed separately: a volume
    /// can accept an unbuffered write and still reject an unbuffered read, and if that is not
    /// caught the read pass silently falls back to cached I/O and reports RAM speed as drive
    /// speed. The probe uses the same block size and alignment as the real test, because a 4 KB
    /// transfer can succeed where a 1 MiB one fails.
    /// </remarks>
    private static async Task<bool> SupportsUnbufferedIoAsync(string rootPath, CancellationToken cancellationToken)
    {
        string probePath = Path.Combine(rootPath, $"CentricIoProbe-{Guid.NewGuid():N}.tmp");
        using AlignedIoBuffer buffer = new(BenchmarkBlockSize, SectorAlignment);

        try
        {
            using (SafeFileHandle writeHandle = File.OpenHandle(
                probePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                FileOptions.Asynchronous | NoBuffering))
            {
                await RandomAccess.WriteAsync(writeHandle, buffer.Memory, 0, cancellationToken).ConfigureAwait(false);
            }

            using (SafeFileHandle readHandle = File.OpenHandle(
                probePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.Asynchronous | NoBuffering))
            {
                int read = await RandomAccess.ReadAsync(readHandle, buffer.Memory, 0, cancellationToken).ConfigureAwait(false);
                if (read != buffer.Length)
                {
                    ApplicationLogService.WriteMessage(
                        "Storage benchmark",
                        $"Unbuffered read probe on {rootPath} returned {read} of {buffer.Length} bytes; using buffered I/O.");
                    return false;
                }
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteMessage(
                "Storage benchmark",
                $"Unbuffered I/O unavailable on {rootPath}; results will include the Windows cache. {exception.Message}");
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(probePath))
                {
                    File.Delete(probePath);
                }
            }
            catch (Exception exception)
            {
                ApplicationLogService.WriteException("Delete storage I/O probe file", exception);
            }
        }
    }

    public string GetCapacityVerificationLogPath() => CapacityVerificationLogPath;

    public long GetSafeCapacityVerificationBytes(StorageVolumeInfo volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        DriveInfo drive = new(volume.RootPath);
        if (!drive.IsReady)
        {
            return 0;
        }

        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? string.Empty;
        bool systemVolume = string.Equals(
            Path.GetPathRoot(volume.RootPath),
            systemRoot,
            StringComparison.OrdinalIgnoreCase);

        long reserve = systemVolume
            ? 20L * 1024L * 1024L * 1024L
            : Math.Max(2L * 1024L * 1024L * 1024L, Math.Min(10L * 1024L * 1024L * 1024L, drive.TotalSize / 20));

        return Math.Max(0, drive.AvailableFreeSpace - reserve);
    }

    public async Task<StorageCapacityVerificationResult> RunCapacityVerificationAsync(
        StorageVolumeInfo volume,
        long requestedBytes,
        IProgress<StorageCapacityProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        DriveInfo drive = new(volume.RootPath);
        if (!drive.IsReady)
        {
            throw new IOException($"The volume {volume.RootPath} is not ready.");
        }

        long safeMaximum = GetSafeCapacityVerificationBytes(volume);
        if (safeMaximum < 256L * 1024L * 1024L)
        {
            throw new IOException("There is not enough safely usable free space for a capacity verification test.");
        }

        if (requestedBytes <= 0 || requestedBytes > safeMaximum)
        {
            requestedBytes = safeMaximum;
        }

        // Unbuffered I/O only accepts sector-sized transfers, and safeMaximum is derived from raw
        // free space, so round down before any of it reaches the transfer helpers.
        requestedBytes -= requestedBytes % SectorAlignment;
        if (requestedBytes <= 0)
        {
            throw new IOException("There is not enough safely usable free space for a capacity verification test.");
        }

        long freeAtStart = drive.AvailableFreeSpace;
        long reserve = Math.Max(0, freeAtStart - safeMaximum);
        string testDirectory = Path.Combine(volume.RootPath, $"CentricCapacityVerify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testDirectory);

        List<(string Path, long Length)> testFiles = new();

        // One random seed per run. Block content is a pure function of (seed, blockIndex), so a
        // drive cannot pass by replaying data captured from an earlier run.
        ulong verificationSeed = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(sizeof(ulong)));
        bool unbuffered = await SupportsUnbufferedIoAsync(volume.RootPath, cancellationToken).ConfigureAwait(false);

        long writtenTotal = 0;
        long verifiedTotal = 0;
        long blockIndex = 0;
        int fileIndex = 0;

        StorageCapacityVerificationResult result = new()
        {
            RecordedAt = DateTime.Now,
            Volume = volume.RootPath,
            RequestedBytes = requestedBytes,
            FreeBytesAtStart = freeAtStart,
            SafetyReserveBytes = reserve
        };

        Stopwatch totalWatch = Stopwatch.StartNew();
        Stopwatch writeWatch = Stopwatch.StartNew();
        Stopwatch verifyWatch = new();
        Stopwatch progressWatch = Stopwatch.StartNew();
        string progressPhase = string.Empty;
        long lastReportedPhaseBytes = 0;
        double writeCurrentMegabytesPerSecond = 0;
        double writeFastestMegabytesPerSecond = 0;
        double verifyCurrentMegabytesPerSecond = 0;
        double verifyFastestMegabytesPerSecond = 0;

        void ReportCapacityProgress(string phase, bool force = false)
        {
            bool verifying = phase.StartsWith("Verifying", StringComparison.OrdinalIgnoreCase);
            long phaseBytes = verifying ? verifiedTotal : writtenTotal;

            if (!string.Equals(progressPhase, phase, StringComparison.Ordinal))
            {
                progressPhase = phase;
                lastReportedPhaseBytes = phaseBytes;
                progressWatch.Restart();
                force = true;
            }

            double intervalSeconds = progressWatch.Elapsed.TotalSeconds;
            if (!force && intervalSeconds < 1.0)
            {
                return;
            }

            long deltaBytes = Math.Max(0, phaseBytes - lastReportedPhaseBytes);
            double sampledMegabytesPerSecond = intervalSeconds > 0.05
                ? (deltaBytes / 1024d / 1024d) / intervalSeconds
                : 0;

            if (verifying)
            {
                if (sampledMegabytesPerSecond > 0.01)
                {
                    verifyCurrentMegabytesPerSecond = sampledMegabytesPerSecond;
                    verifyFastestMegabytesPerSecond = Math.Max(verifyFastestMegabytesPerSecond, sampledMegabytesPerSecond);
                }
            }
            else if (sampledMegabytesPerSecond > 0.01)
            {
                writeCurrentMegabytesPerSecond = sampledMegabytesPerSecond;
                writeFastestMegabytesPerSecond = Math.Max(writeFastestMegabytesPerSecond, sampledMegabytesPerSecond);
            }

            double writeElapsedSeconds = writeWatch.Elapsed.TotalSeconds;
            double writeAverageMegabytesPerSecond = writeElapsedSeconds > 0.05
                ? (writtenTotal / 1024d / 1024d) / writeElapsedSeconds
                : 0;
            long writeRemainingBytes = Math.Max(0, requestedBytes - writtenTotal);
            TimeSpan? writeEta = writtenTotal >= requestedBytes
                ? TimeSpan.Zero
                : writeAverageMegabytesPerSecond > 0.01
                    ? TimeSpan.FromSeconds((writeRemainingBytes / 1024d / 1024d) / writeAverageMegabytesPerSecond)
                    : null;

            double verifyElapsedSeconds = verifyWatch.Elapsed.TotalSeconds;
            double verifyAverageMegabytesPerSecond = verifyElapsedSeconds > 0.05
                ? (verifiedTotal / 1024d / 1024d) / verifyElapsedSeconds
                : 0;
            long verifyRemainingBytes = Math.Max(0, requestedBytes - verifiedTotal);
            TimeSpan? verifyEta = verifiedTotal >= requestedBytes
                ? TimeSpan.Zero
                : verifyWatch.IsRunning && verifyAverageMegabytesPerSecond > 0.01
                    ? TimeSpan.FromSeconds((verifyRemainingBytes / 1024d / 1024d) / verifyAverageMegabytesPerSecond)
                    : null;

            long processedBytes = writtenTotal + verifiedTotal;
            long totalWorkBytes = requestedBytes * 2;

            progress?.Report(new StorageCapacityProgress
            {
                Phase = phase,
                ProcessedBytes = processedBytes,
                TotalBytes = totalWorkBytes,
                FileIndex = fileIndex,
                RequestedBytes = requestedBytes,
                WrittenBytes = writtenTotal,
                VerifiedBytes = verifiedTotal,
                WriteCurrentMegabytesPerSecond = writeCurrentMegabytesPerSecond,
                WriteAverageMegabytesPerSecond = writeAverageMegabytesPerSecond,
                WriteFastestMegabytesPerSecond = writeFastestMegabytesPerSecond,
                WriteElapsed = writeWatch.Elapsed,
                WriteEstimatedRemaining = writeEta,
                VerifyCurrentMegabytesPerSecond = verifyCurrentMegabytesPerSecond,
                VerifyAverageMegabytesPerSecond = verifyAverageMegabytesPerSecond,
                VerifyFastestMegabytesPerSecond = verifyFastestMegabytesPerSecond,
                VerifyElapsed = verifyWatch.Elapsed,
                VerifyEstimatedRemaining = verifyEta,
                TotalElapsed = totalWatch.Elapsed
            });

            lastReportedPhaseBytes = phaseBytes;
            progressWatch.Restart();
        }

        try
        {
            ReportCapacityProgress("Writing test data", force: true);
            // Phase 1: fill the selected free-space region with data that is unique per block.
            // Content is derived from (verificationSeed, blockIndex), so the verify pass can
            // regenerate the expected bytes instead of holding a hash for every block.
            //
            // Creating and preallocating a multi-gigabyte file takes long enough to empty the
            // device queue, so the next file's handle is opened on a background thread while the
            // current file is still being written.
            Task<SafeFileHandle>? pendingOpen = null;
            try
            {
                while (writtenTotal < requestedBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    fileIndex++;
                    long fileTarget = Math.Min(CapacityFileChunkBytes, requestedBytes - writtenTotal);
                    string filePath = Path.Combine(testDirectory, $"capacity-{fileIndex:0000}.bin");
                    long fileStartBlock = blockIndex;
                    long writtenBeforeFile = writtenTotal;

                    SafeFileHandle handle = pendingOpen is null
                        ? OpenWriteHandle(filePath, fileTarget, unbuffered)
                        : await pendingOpen.ConfigureAwait(false);
                    pendingOpen = null;

                    // Queue the following file before writing this one.
                    long nextWritten = writtenBeforeFile + fileTarget;
                    if (nextWritten < requestedBytes)
                    {
                        long nextTarget = Math.Min(CapacityFileChunkBytes, requestedBytes - nextWritten);
                        string nextPath = Path.Combine(testDirectory, $"capacity-{fileIndex + 1:0000}.bin");
                        pendingOpen = Task.Run(
                            () => OpenWriteHandle(nextPath, nextTarget, unbuffered),
                            cancellationToken);
                    }

                    using (handle)
                    {
                        await WriteSequentialAsync(
                            handle,
                            fileTarget,
                            CapacityBlockSize,
                            CapacityQueueDepth,
                            flushAtEnd: !unbuffered,
                            fillEachBlock: true,
                            seed: verificationSeed,
                            onProgress: fileWritten =>
                            {
                                writtenTotal = writtenBeforeFile + fileWritten;
                                ReportCapacityProgress("Writing test data");
                            },
                            cancellationToken,
                            startingBlockIndex: fileStartBlock).ConfigureAwait(false);
                    }

                    writtenTotal = writtenBeforeFile + fileTarget;
                    blockIndex = fileStartBlock + ((fileTarget + CapacityBlockSize - 1) / CapacityBlockSize);
                    testFiles.Add((filePath, fileTarget));
                }
            }
            finally
            {
                // A handle opened ahead of a failed or cancelled run still has to be closed.
                if (pendingOpen is not null)
                {
                    try
                    {
                        (await pendingOpen.ConfigureAwait(false)).Dispose();
                    }
                    catch
                    {
                        // Nothing useful to do; the directory is deleted in the outer finally.
                    }
                }
            }

            result.WrittenBytes = writtenTotal;
            ReportCapacityProgress("Writing test data", force: true);
            writeWatch.Stop();
            verifyWatch.Start();
            ReportCapacityProgress("Verifying written data", force: true);

            // Phase 2: re-read every block after all writes are complete. Counterfeit flash that wraps
            // physical addresses usually corrupts earlier blocks and is detected here.
            long failedBlock = -1;
            using (AlignedIoBuffer expectedBuffer = new(CapacityBlockSize, SectorAlignment))
            {
                long fileStartBlock = 0;
                Task<SafeFileHandle>? pendingReadOpen = null;

                try
                {
                    for (int index = 0; index < testFiles.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        (string filePath, long length) = testFiles[index];
                        long verifiedBeforeFile = verifiedTotal;
                        long currentFileStartBlock = fileStartBlock;

                        SafeFileHandle handle = pendingReadOpen is null
                            ? OpenReadHandle(filePath, unbuffered)
                            : await pendingReadOpen.ConfigureAwait(false);
                        pendingReadOpen = null;

                        if (index + 1 < testFiles.Count)
                        {
                            string nextPath = testFiles[index + 1].Path;
                            pendingReadOpen = Task.Run(() => OpenReadHandle(nextPath, unbuffered), cancellationToken);
                        }

                        using (handle)
                        {
                            await ReadSequentialAsync(
                                handle,
                                length,
                                CapacityBlockSize,
                                CapacityQueueDepth,
                                verifyBlock: (block, actual) =>
                                {
                                    // Regenerating is cheaper than a cryptographic hash and compares
                                    // the full block rather than a digest of it.
                                    Span<byte> expected = expectedBuffer.SpanSlice(actual.Length);
                                    FastBlockGenerator.Fill(expected, verificationSeed, block);
                                    if (actual.Span.SequenceEqual(expected))
                                    {
                                        return true;
                                    }

                                    failedBlock = block;
                                    return false;
                                },
                                onProgress: fileVerified =>
                                {
                                    verifiedTotal = verifiedBeforeFile + fileVerified;
                                    ReportCapacityProgress("Verifying written data");
                                },
                                cancellationToken,
                                startingBlockIndex: currentFileStartBlock).ConfigureAwait(false);
                        }

                        if (failedBlock >= 0)
                        {
                            result.Passed = false;
                            result.FailedBlockIndex = failedBlock;
                            result.FailureReason = $"Data mismatch at block {failedBlock:N0}. The drive may be counterfeit, failing, or corrupt.";
                            result.VerifiedBytes = verifiedTotal;
                            await AppendCapacityVerificationLogAsync(result, cancellationToken);
                            return result;
                        }

                        fileStartBlock += (length + CapacityBlockSize - 1) / CapacityBlockSize;
                    }
                }
                finally
                {
                    if (pendingReadOpen is not null)
                    {
                        try
                        {
                            (await pendingReadOpen.ConfigureAwait(false)).Dispose();
                        }
                        catch
                        {
                            // Nothing useful to do; the directory is deleted in the outer finally.
                        }
                    }
                }
            }

            result.Passed = true;
            result.VerifiedBytes = verifiedTotal;
            ReportCapacityProgress("Verifying written data", force: true);
            verifyWatch.Stop();
            result.FailureReason = string.Empty;
            await AppendCapacityVerificationLogAsync(result, cancellationToken);
            return result;
        }
        finally
        {
            try
            {
                if (Directory.Exists(testDirectory))
                {
                    Directory.Delete(testDirectory, recursive: true);
                }
            }
            catch (Exception exception)
            {
                ApplicationLogService.WriteException("Delete storage capacity verification files", exception);
            }
        }
    }

    private static async Task<IReadOnlyList<StorageDriveHealth>> GetStorageModuleHealthAsync(CancellationToken cancellationToken)
    {
        const string script = """
$ErrorActionPreference = 'Stop'
$items = @(
    Get-PhysicalDisk | ForEach-Object {
        $disk = $_
        $reliability = $null
        try { $reliability = $disk | Get-StorageReliabilityCounter -ErrorAction Stop } catch { }
        [pscustomobject]@{
            DeviceId = [string]$disk.DeviceId
            FriendlyName = [string]$disk.FriendlyName
            SerialNumber = [string]$disk.SerialNumber
            MediaType = [string]$disk.MediaType
            BusType = [string]$disk.BusType
            HealthStatus = [string]$disk.HealthStatus
            OperationalStatus = (($disk.OperationalStatus | ForEach-Object { [string]$_ }) -join ', ')
            SizeBytes = if ($null -ne $disk.Size) { [uint64]$disk.Size } else { $null }
            WearPercent = if ($null -ne $reliability -and $null -ne $reliability.Wear) { [double]$reliability.Wear } else { $null }
            PowerOnHours = if ($null -ne $reliability -and $null -ne $reliability.PowerOnHours) { [double]$reliability.PowerOnHours } else { $null }
            TemperatureCelsius = if ($null -ne $reliability -and $null -ne $reliability.Temperature) { [double]$reliability.Temperature } else { $null }
            TemperatureMaxCelsius = if ($null -ne $reliability -and $null -ne $reliability.TemperatureMax) { [double]$reliability.TemperatureMax } else { $null }
            ReadErrorsTotal = if ($null -ne $reliability -and $null -ne $reliability.ReadErrorsTotal) { [uint64]$reliability.ReadErrorsTotal } else { $null }
            WriteErrorsTotal = if ($null -ne $reliability -and $null -ne $reliability.WriteErrorsTotal) { [uint64]$reliability.WriteErrorsTotal } else { $null }
            Source = 'Windows Storage reliability counters'
        }
    }
)
$items | ConvertTo-Json -Depth 4 -Compress
""";

        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        ProcessStartInfo startInfo = new()
        {
            FileName = "powershell.exe",
            Arguments = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Windows PowerShell.");
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(timeout.Token);

        string output = (await outputTask).Trim();
        string error = (await errorTask).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Windows Storage reliability query failed." : error);
        }

        if (string.IsNullOrWhiteSpace(output) || output == "null")
        {
            return Array.Empty<StorageDriveHealth>();
        }

        using JsonDocument document = JsonDocument.Parse(output);
        List<StorageDriveHealth> drives = new();
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                StorageDriveHealth? drive = JsonSerializer.Deserialize<StorageDriveHealth>(element.GetRawText());
                if (drive is not null)
                {
                    drives.Add(drive);
                }
            }
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            StorageDriveHealth? drive = JsonSerializer.Deserialize<StorageDriveHealth>(document.RootElement.GetRawText());
            if (drive is not null)
            {
                drives.Add(drive);
            }
        }

        return drives;
    }

    private static IReadOnlyList<StorageDriveHealth> GetWmiFallbackHealth()
    {
        List<StorageDriveHealth> drives = new();
        try
        {
            using ManagementObjectSearcher searcher = new("SELECT DeviceID, Model, SerialNumber, MediaType, InterfaceType, Status, Size FROM Win32_DiskDrive");
            using ManagementObjectCollection results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                _ = ulong.TryParse(Convert.ToString(item["Size"]), out ulong size);
                drives.Add(new StorageDriveHealth
                {
                    DeviceId = Convert.ToString(item["DeviceID"]) ?? string.Empty,
                    FriendlyName = Convert.ToString(item["Model"]) ?? "Unknown disk",
                    SerialNumber = (Convert.ToString(item["SerialNumber"]) ?? string.Empty).Trim(),
                    MediaType = Convert.ToString(item["MediaType"]) ?? "Unspecified",
                    BusType = Convert.ToString(item["InterfaceType"]) ?? "Unknown",
                    HealthStatus = Convert.ToString(item["Status"]) ?? "Unknown",
                    OperationalStatus = Convert.ToString(item["Status"]) ?? "Unknown",
                    SizeBytes = size > 0 ? size : null,
                    Source = "Win32_DiskDrive fallback (SMART lifetime counters unavailable)"
                });
            }
        }
        catch (Exception exception)
        {
            ApplicationLogService.WriteException("Storage WMI fallback", exception);
        }

        return drives;
    }

    private static async Task AppendBenchmarkLogAsync(StorageBenchmarkResult result, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(SharedDataPaths.LogsDirectory);
        bool exists = File.Exists(BenchmarkLogPath);
        await using FileStream stream = new(BenchmarkLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await using StreamWriter writer = new(stream, Encoding.UTF8);
        if (!exists)
        {
            await writer.WriteLineAsync("RecordedAt,Volume,TestSizeMB,SequentialWriteMBps,SequentialReadMBps,Profile,CacheBypassed");
        }

        string line = string.Join(",",
            result.RecordedAt.ToString("O"),
            EscapeCsv(result.Volume),
            result.TestSizeMegabytes,
            result.SequentialWriteMegabytesPerSecond.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            result.SequentialReadMegabytesPerSecond.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            EscapeCsv(result.Profile),
            result.CacheBypassed ? "yes" : "no");
        await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
    }

    private static async Task AppendCapacityVerificationLogAsync(StorageCapacityVerificationResult result, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(SharedDataPaths.LogsDirectory);
        bool exists = File.Exists(CapacityVerificationLogPath);
        await using FileStream stream = new(CapacityVerificationLogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await using StreamWriter writer = new(stream, Encoding.UTF8);
        if (!exists)
        {
            await writer.WriteLineAsync("RecordedAt,Volume,RequestedBytes,WrittenBytes,VerifiedBytes,Passed,FailedBlockIndex,FailureReason");
        }

        string line = string.Join(",",
            result.RecordedAt.ToString("O"),
            EscapeCsv(result.Volume),
            result.RequestedBytes,
            result.WrittenBytes,
            result.VerifiedBytes,
            result.Passed,
            result.FailedBlockIndex?.ToString() ?? string.Empty,
            EscapeCsv(result.FailureReason));
        await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
    }

    private static string EscapeCsv(string value)
    {
        if (!value.Contains(',') && !value.Contains('"') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }
}
