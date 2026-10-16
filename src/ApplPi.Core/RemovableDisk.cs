using System.Management;

namespace ApplPi.Core;

/// <summary>A disk the image may be written to: a card in a card reader (see <see cref="FindAll"/>).</summary>
/// <param name="Number">Windows' disk number (\\.\PhysicalDriveN).</param>
/// <param name="Name">The reader's name, as Windows shows it.</param>
/// <param name="Size">The card's size in bytes.</param>
/// <param name="DriveLetters">The drive letters of the partitions on it, such as F.</param>
public sealed record RemovableDisk(int Number, string Name, long Size, IReadOnlyList<char> DriveLetters)
{
    const ushort BusUsb = 7;
    const ushort BusSd = 12;
    const ushort BusMmc = 13;

    /// <summary>What to show before writing: number, name, size and drive letters.</summary>
    public string Description => $"Disk {Number}: {Name}, {Size / 1e9:0.0} GB" +
        (DriveLetters.Count > 0 ? $" ({string.Join(", ", DriveLetters.Select(letter => $"{letter}:"))})" : "");

    /// <summary>
    /// The cards in card readers. Only disks on the USB or SD bus that Windows reports as removable media (which
    /// leaves out USB hard disks and SSDs), aren't the system or boot disk, and have a card in them.
    /// </summary>
    public static IReadOnlyList<RemovableDisk> FindAll()
    {
        var removable = Query(@"root\cimv2", "SELECT Index, MediaType FROM Win32_DiskDrive")
            .Where(drive => drive["MediaType"] as string == "Removable Media")
            .Select(drive => Convert.ToInt32(drive["Index"]))
            .ToHashSet();
        var letters = Query(@"root\Microsoft\Windows\Storage", "SELECT DiskNumber, DriveLetter FROM MSFT_Partition")
            .Select(partition => (Disk: Convert.ToInt32(partition["DiskNumber"]), Letter: Convert.ToChar(partition["DriveLetter"])))
            .Where(partition => char.IsAsciiLetter(partition.Letter))
            .ToLookup(partition => partition.Disk, partition => partition.Letter);
        return Query(@"root\Microsoft\Windows\Storage",
                "SELECT Number, FriendlyName, Size, BusType, IsSystem, IsBoot FROM MSFT_Disk")
            .Where(disk => Convert.ToUInt16(disk["BusType"]) is BusUsb or BusSd or BusMmc)
            .Where(disk => !(bool)disk["IsSystem"] && !(bool)disk["IsBoot"])
            .Select(disk => new RemovableDisk(Convert.ToInt32(disk["Number"]), disk["FriendlyName"] as string ?? "Unknown",
                Convert.ToInt64(disk["Size"]), letters[Convert.ToInt32(disk["Number"])].Order().ToList()))
            .Where(disk => removable.Contains(disk.Number) && disk.Size > 0)
            .OrderBy(disk => disk.Number)
            .ToList();
    }

    static List<ManagementBaseObject> Query(string scope, string query)
    {
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query));
        return searcher.Get().Cast<ManagementBaseObject>().ToList();
    }
}
