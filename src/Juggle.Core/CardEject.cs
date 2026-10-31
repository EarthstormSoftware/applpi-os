using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Juggle.Core;

/// <summary>
/// Ejects a card safely, so everything written to it is on it before it's taken out. On Windows as Explorer's Eject:
/// each of its volumes is locked and dismounted (which writes out what's in the cache), then the media is ejected (a
/// card reader stays, empty). On Linux: everything is written out, its partitions are unmounted, then the media is
/// ejected (as root with eject; as a user, the desktop's udisks powers the reader off).
/// </summary>
public static partial class CardEject
{
    /// <summary>Ejects the card, throwing IOException (saying why) if it can't.</summary>
    public static void Eject(RemovableDisk disk)
    {
        var now = RemovableDisk.FindAll().FirstOrDefault(candidate => candidate.Id == disk.Id)
            ?? throw new IOException("It isn't in the card reader any more.");
        if (OperatingSystem.IsWindows())
        {
            EjectWindows(now);
        }
        else
        {
            EjectLinux(now);
        }
    }

    [SupportedOSPlatform("windows")]
    static void EjectWindows(RemovableDisk disk)
    {
        var volumes = new List<SafeFileHandle>();
        try
        {
            foreach (var letter in disk.Volumes)
            {
                volumes.Add(Step($"opening {letter}", () => ImageWriter.Open($@"\\.\{letter}")));
                Dismount(volumes[^1], letter);
            }
            // Without a volume (none mounted), the disk itself, which needs administrator rights
            using var device = volumes.Count == 0 ? Step("opening the disk", () => ImageWriter.Open(disk.DevicePath)) : null;
            var handle = volumes.FirstOrDefault() ?? device!;
            Step("allowing removal", () => AllowRemoval(handle));
            Step("ejecting it", () => ImageWriter.DeviceIoControl(handle, IoctlStorageEjectMedia));
        }
        finally
        {
            volumes.ForEach(volume => volume.Dispose());
        }
    }

    /// <summary>
    /// Writes out what's in the cache for a volume and dismounts it. Locking it first fails while another program has
    /// it open, as Explorer, AutoPlay and virus scanners do with a card just written, so it's tried for a while; then
    /// it's dismounted anyway, which closes their handles to it.
    /// </summary>
    [SupportedOSPlatform("windows")]
    static void Dismount(SafeFileHandle volume, string letter)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (DeviceIoControl(volume, FsctlLockVolume, 0, 0, 0, 0, out _, 0))
            {
                break;
            }
            Thread.Sleep(500);
        }
        Step($"writing out {letter}", () =>
        {
            if (!FlushFileBuffers(volume))
            {
                throw new IOException(new Win32Exception().Message);
            }
        });
        Step($"dismounting {letter}", () => ImageWriter.DeviceIoControl(volume, FsctlDismountVolume));
    }

    // A reader may refuse to eject while removal is prevented
    static unsafe void AllowRemoval(SafeFileHandle handle)
    {
        byte prevent = 0;
        if (!DeviceIoControl(handle, IoctlStorageMediaRemoval, (nint)(&prevent), 1, 0, 0, out _, 0))
        {
            throw new IOException(new Win32Exception().Message);
        }
    }

    /// <summary>Does one step of ejecting, saying which if it fails.</summary>
    static T Step<T>(string what, Func<T> step)
    {
        try
        {
            return step();
        }
        catch (IOException error)
        {
            throw new IOException($"{what}: {error.Message.TrimEnd('.')}", error);
        }
    }

    static void Step(string what, Action step) => Step(what, () =>
    {
        step();
        return 0;
    });

    static void EjectLinux(RemovableDisk disk)
    {
        Platform.Run("sync");
        var root = Platform.IsAdministrator;
        foreach (var partition in disk.Partitions.Where(part => part.MountPoint is not null))
        {
            if (root)
            {
                Platform.Run("umount", partition.MountPoint!);
            }
            else
            {
                Platform.Run("udisksctl", "unmount", "--block-device", partition.Device);
            }
        }
        try
        {
            if (root)
            {
                Platform.Run("eject", disk.DevicePath);
            }
            else
            {
                Platform.Run("udisksctl", "power-off", "--block-device", disk.DevicePath);
            }
        }
        catch (Exception error) when (error is IOException or Win32Exception)
        {
            // Written out and unmounted, it can be taken out: ejecting only tells the reader so
        }
    }

    const uint FsctlLockVolume = 0x00090018;
    const uint FsctlDismountVolume = 0x00090020;
    const uint IoctlStorageMediaRemoval = 0x002D4804;
    const uint IoctlStorageEjectMedia = 0x002D4808;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(SafeFileHandle file);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, nint inBuffer, uint inSize,
        nint outBuffer, uint outSize, out uint returned, nint overlapped);
}
