using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SharpCompress.Compressors.Xz;

namespace ApplPi.Core;

/// <param name="Verifying">False while writing, true while reading the card back.</param>
/// <param name="Fraction">How far through the image, from 0 to 1.</param>
/// <param name="Bytes">Bytes written (or checked) so far.</param>
public readonly record struct WriteProgress(bool Verifying, double Fraction, long Bytes);

/// <summary>
/// Writes an .img.xz image to a whole disk and reads it back to check it. Needs administrator rights. The card's
/// partitions are dismounted and locked first, and the first block (the partition table) is written last, so Windows
/// doesn't mount a half-written card.
/// </summary>
public static partial class ImageWriter
{
    // A multiple of every sector size (512 or 4096 bytes)
    const int ChunkSize = 4 << 20;
    const int SectorSize = 4096;

    public static void Write(RemovableDisk disk, string imagePath, IProgress<WriteProgress>? progress = null)
    {
        var volumeLocks = disk.DriveLetters.Select(LockVolume).ToList();
        try
        {
            using var device = Open($@"\\.\PhysicalDrive{disk.Number}");
            var buffer = new AlignedBuffer(ChunkSize);
            try
            {
                byte[]? firstChunk = null;
                long offset = 0;
                foreach (var length in Chunks(imagePath, buffer, progress, verifying: false, () => offset))
                {
                    if (offset + length > disk.Size)
                    {
                        throw new InvalidOperationException($"The image is bigger than the card ({disk.Size / 1e9:0.0} GB).");
                    }
                    if (offset == 0)
                    {
                        firstChunk = buffer.Span[..length].ToArray();
                    }
                    else
                    {
                        RandomAccess.Write(device, buffer.Span[..length], offset);
                    }
                    offset += length;
                }
                if (firstChunk is null)
                {
                    throw new InvalidOperationException("The image is empty.");
                }
                firstChunk.CopyTo(buffer.Span);
                RandomAccess.Write(device, buffer.Span[..firstChunk.Length], 0);
                FlushFileBuffers(device);

                Verify(device, imagePath, buffer, progress);
                // Windows reads the new partition table and mounts the boot partition
                DeviceIoControl(device, IoctlDiskUpdateProperties);
            }
            finally
            {
                buffer.Dispose();
            }
        }
        finally
        {
            volumeLocks.ForEach(handle => handle.Dispose());
        }
    }

    static void Verify(SafeFileHandle device, string imagePath, AlignedBuffer imageChunk, IProgress<WriteProgress>? progress)
    {
        using var cardChunk = new AlignedBuffer(ChunkSize);
        long offset = 0;
        foreach (var length in Chunks(imagePath, imageChunk, progress, verifying: true, () => offset))
        {
            var read = RandomAccess.Read(device, cardChunk.Span[..length], offset);
            if (read != length || !cardChunk.Span[..length].SequenceEqual(imageChunk.Span[..length]))
            {
                throw new IOException($"The card doesn't match the image at {offset / (1 << 20)} MiB: it may be faulty.");
            }
            offset += length;
        }
    }

    /// <summary>
    /// Decompresses the image into the buffer a chunk at a time, yielding each chunk's length (padded with zeros to
    /// whole sectors at the end).
    /// </summary>
    static IEnumerable<int> Chunks(string imagePath, AlignedBuffer buffer, IProgress<WriteProgress>? progress,
        bool verifying, Func<long> offset)
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
            progress?.Report(new WriteProgress(verifying, (double)file.Position / file.Length, offset()));
            yield return padded;
        }
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

    static SafeFileHandle Open(string path)
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
