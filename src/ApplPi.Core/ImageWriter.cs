using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using SharpCompress.Compressors.Xz;

namespace ApplPi.Core;

/// <param name="Verifying">False while writing, true while reading the card back.</param>
/// <param name="Bytes">Data written (or checked) so far.</param>
/// <param name="Total">The data to write: the blocks the .bmap lists, or the whole image without one.</param>
public readonly record struct WriteProgress(bool Verifying, long Bytes, long Total)
{
    public double Fraction => Total > 0 ? Math.Min(1, (double)Bytes / Total) : 0;
}

/// <summary>
/// Writes an .img.xz image to a whole disk and reads it back to check it. Needs administrator rights. The card's
/// partitions are dismounted and locked first, and the first block (the partition table) is written last, so Windows
/// doesn't mount a half-written card. With a .bmap beside the image (see <see cref="BlockMap"/>), only the blocks
/// holding data are written and checked: the file systems' free space, most of the image, is skipped. The check
/// compares checksums of what was written with what the card reads back, so the image is only decompressed once.
/// </summary>
public static partial class ImageWriter
{
    // A multiple of every sector size (512 or 4096 bytes)
    const int ChunkSize = 4 << 20;
    const int SectorSize = 4096;

    /// <summary>A part of the image that was written: where, how long, and its SHA-256.</summary>
    readonly record struct WrittenPart(long Offset, int Length, byte[] Hash);

    public static void Write(RemovableDisk disk, string imagePath, IProgress<WriteProgress>? progress = null)
    {
        var volumeLocks = disk.DriveLetters.Select(LockVolume).ToList();
        try
        {
            using var device = Open($@"\\.\PhysicalDrive{disk.Number}");
            WriteTo(device, disk.Size, imagePath, progress);
            // Windows reads the new partition table and mounts the boot partition
            DeviceIoControl(device, IoctlDiskUpdateProperties);
        }
        finally
        {
            volumeLocks.ForEach(handle => handle.Dispose());
        }
    }

    /// <summary>Writes and checks the image on an open disk (or, for tests, a file) of the given size.</summary>
    internal static void WriteTo(SafeFileHandle device, long deviceSize, string imagePath, IProgress<WriteProgress>? progress)
    {
        var map = BlockMap.ForImage(imagePath);
        using var buffer = new AlignedBuffer(ChunkSize);
        var written = new List<WrittenPart>();
        long total = map?.MappedBytes ?? 0;
        long done = 0;

        void WriteParts(long offset, int length)
        {
            foreach (var (start, partLength) in Parts(map, offset, length))
            {
                var data = buffer.Span.Slice(start, partLength);
                RandomAccess.Write(device, data, offset + start);
                written.Add(new WrittenPart(offset + start, partLength, SHA256.HashData(data)));
                done += partLength;
                progress?.Report(new WriteProgress(false, done, total));
            }
        }

        byte[]? firstChunk = null;
        long offset = 0;
        foreach (var length in Chunks(imagePath, buffer))
        {
            if (offset + length > deviceSize)
            {
                throw new InvalidOperationException($"The image is bigger than the card ({deviceSize / 1e9:0.0} GB).");
            }
            if (offset == 0)
            {
                firstChunk = buffer.Span[..length].ToArray();
                if (total == 0)
                {
                    total = ImageSize(firstChunk);
                }
            }
            else
            {
                WriteParts(offset, length);
            }
            offset += length;
        }
        if (firstChunk is null)
        {
            throw new InvalidOperationException("The image is empty.");
        }
        firstChunk.CopyTo(buffer.Span);
        WriteParts(0, firstChunk.Length);
        FlushFileBuffers(device);

        Verify(device, written, buffer, progress, total);
    }

    /// <summary>Reads back what was written and compares its checksums.</summary>
    static void Verify(SafeFileHandle device, List<WrittenPart> written, AlignedBuffer buffer,
        IProgress<WriteProgress>? progress, long total)
    {
        long done = 0;
        foreach (var part in written.OrderBy(part => part.Offset))
        {
            var card = buffer.Span[..part.Length];
            if (RandomAccess.Read(device, card, part.Offset) != part.Length || !SHA256.HashData(card).AsSpan().SequenceEqual(part.Hash))
            {
                throw new IOException($"The card doesn't match the image at {part.Offset / (1 << 20)} MiB: it may be faulty.");
            }
            done += part.Length;
            progress?.Report(new WriteProgress(true, done, total));
        }
    }

    /// <summary>The parts of a chunk to write: those the block map lists, or all of it without one.</summary>
    static IEnumerable<(int Start, int Length)> Parts(BlockMap? map, long offset, int length) =>
        map?.Mapped(offset, length) ?? [(0, length)];

    /// <summary>
    /// Decompresses the image into the buffer a chunk at a time, yielding each chunk's length (padded with zeros to
    /// whole sectors at the end).
    /// </summary>
    static IEnumerable<int> Chunks(string imagePath, AlignedBuffer buffer)
    {
        using var file = File.OpenRead(imagePath);
        using var image = new XZStream(file);
        while (true)
        {
            var length = 0;
            int read;
            while (length < ChunkSize && (read = image.Read(buffer.Span[length..])) > 0)
            {
                length += read;
            }
            if (length == 0)
            {
                yield break;
            }
            var padded = (length + SectorSize - 1) / SectorSize * SectorSize;
            buffer.Span[length..padded].Clear();
            yield return padded;
        }
    }

    /// <summary>
    /// The image's size from the MBR partition table in its first sector: where its last partition ends. (The .xz
    /// file doesn't say how big the image is until its end, and how far through the compressed file says little: the
    /// image ends in an empty file system that compresses to almost nothing.) 0 if there's no partition table.
    /// </summary>
    public static long ImageSize(ReadOnlySpan<byte> firstSector)
    {
        if (firstSector.Length < 512 || firstSector[510] != 0x55 || firstSector[511] != 0xAA)
        {
            return 0;
        }
        long end = 0;
        for (var entry = 0; entry < 4; entry++)
        {
            var partition = firstSector.Slice(446 + entry * 16, 16);
            long start = BitConverter.ToUInt32(partition[8..12]);
            long sectors = BitConverter.ToUInt32(partition[12..16]);
            end = Math.Max(end, (start + sectors) * 512);
        }
        return end;
    }

    static SafeFileHandle LockVolume(char letter)
    {
        var volume = Open($@"\\.\{letter}:");
        try
        {
            DeviceIoControl(volume, FsctlLockVolume);
            DeviceIoControl(volume, FsctlDismountVolume);
            return volume;
        }
        catch
        {
            volume.Dispose();
            throw;
        }
    }

    internal static SafeFileHandle Open(string path)
    {
        var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting,
            FileFlagNoBuffering | FileFlagWriteThrough, 0);
        if (handle.IsInvalid)
        {
            throw new IOException($"Can't open {path}: {new Win32Exception().Message}");
        }
        return handle;
    }

    static void DeviceIoControl(SafeFileHandle device, uint code)
    {
        if (!DeviceIoControl(device, code, 0, 0, 0, 0, out _, 0))
        {
            throw new IOException(new Win32Exception().Message);
        }
    }

    static void FlushFileBuffers(SafeFileHandle device)
    {
        if (!FlushFileBuffersNative(device))
        {
            throw new IOException(new Win32Exception().Message);
        }
    }

    /// <summary>Memory aligned to the sector size, which unbuffered disk I/O needs.</summary>
    sealed unsafe class AlignedBuffer(int size) : IDisposable
    {
        readonly void* memory = NativeMemory.AlignedAlloc((nuint)size, SectorSize);

        public Span<byte> Span => new(memory, size);

        public void Dispose() => NativeMemory.AlignedFree(memory);
    }

    const uint GenericRead = 0x80000000;
    const uint GenericWrite = 0x40000000;
    const uint FileShareRead = 1;
    const uint FileShareWrite = 2;
    const uint OpenExisting = 3;
    const uint FileFlagNoBuffering = 0x20000000;
    const uint FileFlagWriteThrough = 0x80000000;
    const uint FsctlLockVolume = 0x00090018;
    const uint FsctlDismountVolume = 0x00090020;
    const uint IoctlDiskUpdateProperties = 0x00070140;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint creation,
        uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, nint inBuffer, uint inSize,
        nint outBuffer, uint outSize, out uint returned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "FlushFileBuffers", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffersNative(SafeFileHandle file);
}
