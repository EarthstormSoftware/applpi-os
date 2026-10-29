using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using System.Text.Json;

namespace ApplPi.Core;

/// <summary>A partition of a <see cref="RemovableDisk"/> (Linux): its device, label and where it's mounted, if it is.</summary>
public sealed record DiskPartition(string Device, string? Label, string? MountPoint);

/// <summary>A disk the image may be written to: a card in a card reader (see <see cref="FindAll"/>).</summary>
/// <param name="Id">Windows' disk number (\\.\PhysicalDriveN), or the Linux device's name (sdb for /dev/sdb).</param>
/// <param name="Name">The reader's name, as the system shows it.</param>
/// <param name="Size">The card's size in bytes.</param>
/// <param name="Volumes">Where its partitions are mounted: drive letters such as F:, or Linux mount points.</param>
/// <param name="Partitions">Its partitions (Linux only).</param>
public sealed record RemovableDisk(string Id, string Name, long Size, IReadOnlyList<string> Volumes,
    IReadOnlyList<DiskPartition> Partitions)
{
    const ushort BusUsb = 7;
    const ushort BusSd = 12;
    const ushort BusMmc = 13;

    /// <summary>The disk itself, to write to.</summary>
    public string DevicePath => OperatingSystem.IsWindows() ? $@"\\.\PhysicalDrive{Id}" : $"/dev/{Id}";

    /// <summary>What to show before writing: the disk, its name, size and what's mounted from it.</summary>
    public string Description => $"{(OperatingSystem.IsWindows() ? $"Disk {Id}" : DevicePath)}: {Name}, {Size / 1e9:0.0} GB" +
        (Volumes.Count > 0 ? $" ({string.Join(", ", Volumes)})" : "");

    /// <summary>
    /// The cards in card readers. Only disks a card reader holds (on the USB or SD bus, and removable media, which leaves
    /// out USB hard disks and SSDs), with a card in them, and that aren't the system's: on Linux, nothing on them may be
    /// mounted except where removable media go.
    /// </summary>
    public static IReadOnlyList<RemovableDisk> FindAll() => OperatingSystem.IsWindows()
        ? FindAllWindows()
        : FromLsblk(Lsblk(), name => File.Exists($"/sys/block/{name}/device/type") ? File.ReadAllText($"/sys/block/{name}/device/type").Trim() : null);

    /// <summary>lsblk's JSON, with every mount point (util-linux 2.37 on), or one per partition before that.</summary>
    static string Lsblk()
    {
        const string Columns = "NAME,TYPE,SIZE,VENDOR,MODEL,TRAN,RM,LABEL";
        try
        {
            return Platform.Run("lsblk", "--json", "--bytes", "--output", Columns + ",MOUNTPOINTS");
        }
        catch (IOException)
        {
            return Platform.Run("lsblk", "--json", "--bytes", "--output", Columns + ",MOUNTPOINT");
        }
    }

    /// <summary>
    /// The card readers' cards in lsblk's JSON. A disk on USB must be removable media (a card reader, not a USB disk); on
    /// the MMC bus it must be an SD card (a laptop's card slot, not its built-in eMMC storage), which mmcType gives from
    /// the disk's name. Its partitions may only be mounted where removable media go (/media/&lt;user&gt;/..,
    /// /run/media/.., /mnt/..), which leaves out the disk the system runs from, a Pi's own card included.
    /// </summary>
    internal static IReadOnlyList<RemovableDisk> FromLsblk(string json, Func<string, string?> mmcType)
    {
        using var document = JsonDocument.Parse(json);
        var disks = new List<RemovableDisk>();
        foreach (var device in document.RootElement.GetProperty("blockdevices").EnumerateArray())
        {
            var name = Text(device, "name") ?? "";
            var transport = Text(device, "tran");
            var size = Number(device, "size");
            if (Text(device, "type") != "disk" || size <= 0
                || !(transport == "usb" && Flag(device, "rm") || transport == "mmc" && mmcType(name) == "SD"))
            {
                continue;
            }
            var partitions = device.TryGetProperty("children", out var children)
                ? children.EnumerateArray()
                    .Where(child => Text(child, "type") == "part")
                    .Select(child => new DiskPartition($"/dev/{Text(child, "name")}", Text(child, "label"), MountPoints(child).FirstOrDefault()))
                    .ToList()
                : [];
            var mounted = MountPoints(device)
                .Concat(device.TryGetProperty("children", out children) ? children.EnumerateArray().SelectMany(MountPoints) : [])
                .ToList();
            if (!mounted.All(IsRemovableMediaPlace))
            {
                continue;
            }
            var label = string.Join(' ', new[] { Text(device, "vendor"), Text(device, "model") }.Where(part => part is { Length: > 0 }));
            disks.Add(new RemovableDisk(name, label.Length > 0 ? label : "Card reader", size, mounted, partitions));
        }
        return disks.OrderBy(disk => disk.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Where a desktop or a person mounts removable media: /media/&lt;user&gt;/&lt;name&gt;, /run/media/.., /mnt/..</summary>
    static bool IsRemovableMediaPlace(string mountPoint) =>
        mountPoint.Split('/', StringSplitOptions.RemoveEmptyEntries) is var parts
        && (parts is ["media", _, _, ..] || parts is ["run", "media", _, ..] || parts is ["mnt", _, ..]);

    static IEnumerable<string> MountPoints(JsonElement device) =>
        device.TryGetProperty("mountpoints", out var points) && points.ValueKind == JsonValueKind.Array
            ? points.EnumerateArray().Select(point => point.GetString()).OfType<string>()
            : Text(device, "mountpoint") is { } point ? [point] : [];

    static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;

    // Older lsblk versions give numbers and flags as strings
    static long Number(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        ? value.ValueKind == JsonValueKind.Number ? value.GetInt64() : long.TryParse(value.GetString(), out var number) ? number : 0
        : 0;

    static bool Flag(JsonElement element, string name) => element.TryGetProperty(name, out var value)
        && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.String && value.GetString() == "1");

    [SupportedOSPlatform("windows")]
    static List<RemovableDisk> FindAllWindows()
    {
        var removable = Query(@"root\cimv2", "SELECT Index, MediaType FROM Win32_DiskDrive")
            .Where(drive => drive["MediaType"] as string == "Removable Media")
            .Select(drive => Convert.ToInt32(drive["Index"]))
            .ToHashSet();
        var letters = Query(@"root\Microsoft\Windows\Storage", "SELECT DiskNumber, DriveLetter FROM MSFT_Partition")
            .Select(partition => (Disk: Convert.ToInt32(partition["DiskNumber"]), Letter: Convert.ToChar(partition["DriveLetter"])))
            .Where(partition => char.IsAsciiLetter(partition.Letter))
            .ToLookup(partition => partition.Disk, partition => $"{partition.Letter}:");
        return Query(@"root\Microsoft\Windows\Storage",
                "SELECT Number, FriendlyName, Size, BusType, IsSystem, IsBoot FROM MSFT_Disk")
            .Where(disk => Convert.ToUInt16(disk["BusType"]) is BusUsb or BusSd or BusMmc)
            .Where(disk => !(bool)disk["IsSystem"] && !(bool)disk["IsBoot"])
            .Select(disk => (Number: Convert.ToInt32(disk["Number"]), Name: disk["FriendlyName"] as string ?? "Unknown",
                Size: Convert.ToInt64(disk["Size"])))
            .Where(disk => removable.Contains(disk.Number) && disk.Size > 0)
            .OrderBy(disk => disk.Number)
            .Select(disk => new RemovableDisk(disk.Number.ToString(CultureInfo.InvariantCulture), disk.Name, disk.Size,
                letters[disk.Number].Order().ToList(), []))
            .ToList();
    }

    [SupportedOSPlatform("windows")]
    static List<ManagementBaseObject> Query(string scope, string query)
    {
        using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query));
        return searcher.Get().Cast<ManagementBaseObject>().ToList();
    }
}
