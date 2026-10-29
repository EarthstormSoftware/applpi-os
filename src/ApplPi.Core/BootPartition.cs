namespace ApplPi.Core;

/// <summary>
/// The boot partition of a card written with the ApplPi OS image (FAT): a drive letter on Windows, a mount point on
/// Linux. One applpi mounted itself (on Linux, as root) is unmounted when it's disposed, or by <see cref="ReleaseAll"/>.
/// </summary>
public sealed class BootPartition(string root, string description, Action? release = null, string? name = null) : IDisposable
{
    const string SettingsFile = "applpi.json";
    const string ErrorFile = "applpi-error.txt";
    const string Label = "bootfs";

    // The partitions applpi has mounted and not yet unmounted
    static readonly List<BootPartition> Mounted = [];

    /// <summary>The partition's root directory, such as F:\ or /media/me/bootfs.</summary>
    public string Root { get; } = root;

    /// <summary>What to call it in a message: its root, or the device of one mounted in a temporary folder.</summary>
    public string Name { get; } = name ?? root;

    /// <summary>What to show the user: the drive letter or device, and the partition's size or mount point.</summary>
    public string Description { get; } = description;

    /// <summary>Whether settings are waiting on the card for the Pi to read at its next boot.</summary>
    public bool HasPendingSettings => File.Exists(Path.Combine(Root, SettingsFile));

    /// <summary>
    /// The image's version and when it was built (BUILD_STAMP, UTC, such as 20261016-2249; v0.1.0 has BUILD_DATE
    /// instead), from applpi-release.txt (KEY=value lines, as /etc/applpi-release on the Pi), or null for images made
    /// before it was added.
    /// </summary>
    public (string Version, string Built)? ReadRelease()
    {
        var path = Path.Combine(Root, "applpi-release.txt");
        if (!File.Exists(path))
        {
            return null;
        }
        var values = File.ReadAllLines(path)
            .Select(line => line.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0].Trim(), pair => pair[1].Trim());
        var built = values.TryGetValue("BUILD_STAMP", out var stamp) ? $"{stamp} UTC" : values.GetValueOrDefault("BUILD_DATE", "unknown");
        return (values.GetValueOrDefault("VERSION", "unknown"), built);
    }

    /// <summary>Why the Pi didn't use the last settings it was given (left on the card by the Pi), or null.</summary>
    public string? ReadError()
    {
        var path = Path.Combine(Root, ErrorFile);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    /// <summary>Writes the settings for the Pi to read at its next boot, replacing any it hasn't read yet.</summary>
    public void WriteSettings(DeviceSettings settings)
    {
        var errors = SettingsRules.Check(settings);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(settings));
        }
        File.WriteAllText(Path.Combine(Root, SettingsFile), settings.ToJson() + "\n");
        File.Delete(Path.Combine(Root, ErrorFile));
    }

    /// <summary>Unmounts the partition if applpi mounted it, once everything on it is written to the card.</summary>
    public void Dispose()
    {
        Action? unmount;
        lock (Mounted)
        {
            unmount = release;
            release = null;
            Mounted.Remove(this);
        }
        unmount?.Invoke();
    }

    /// <summary>Unmounts every partition applpi mounted, as when it's stopped with Ctrl+C.</summary>
    public static void ReleaseAll()
    {
        List<BootPartition> mounted;
        lock (Mounted)
        {
            mounted = [.. Mounted];
        }
        mounted.ForEach(card => card.Dispose());
    }

    /// <summary>
    /// The ApplPi cards in card readers: the boot partition is labelled bootfs and made by our build. On Linux, a card's
    /// partition the desktop has mounted is used where it is; one that isn't mounted is mounted here when running as
    /// root (see <see cref="UnmountedCards"/>).
    /// </summary>
    public static IReadOnlyList<BootPartition> FindCards()
    {
        if (OperatingSystem.IsWindows())
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.DriveType == DriveType.Removable && drive.IsReady)
                .Where(drive => drive.VolumeLabel == Label && IsApplPi(drive.RootDirectory.FullName))
                .Select(drive => new BootPartition(drive.RootDirectory.FullName,
                    $"{drive.Name.TrimEnd('\\')} (boot partition, {drive.TotalSize / (1024 * 1024)} MB)"))
                .ToList();
        }
        var cards = new List<BootPartition>();
        foreach (var partition in RemovableDisk.FindAll().SelectMany(disk => disk.Partitions).Where(part => part.Label == Label))
        {
            if (partition.MountPoint is { } mounted)
            {
                if (IsApplPi(mounted))
                {
                    cards.Add(new BootPartition(mounted, $"{partition.Device} (boot partition, at {mounted})"));
                }
            }
            else if (Platform.IsAdministrator && Mount(partition.Device) is { } card)
            {
                cards.Add(card);
            }
        }
        return cards;
    }

    /// <summary>
    /// How many cards' boot partitions aren't mounted (Linux): run as a user, applpi can't mount them, so it runs itself
    /// again with sudo.
    /// </summary>
    public static int UnmountedCards() => OperatingSystem.IsWindows()
        ? 0
        : RemovableDisk.FindAll().SelectMany(disk => disk.Partitions).Count(part => part.Label == Label && part.MountPoint is null);

    /// <summary>
    /// The boot partition of a card just written, once the system has found it (and on Linux, mounted here), or null
    /// if it hasn't within 30 seconds.
    /// </summary>
    public static BootPartition? WaitFor(RemovableDisk disk)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var now = RemovableDisk.FindAll().FirstOrDefault(candidate => candidate.Id == disk.Id);
            if (OperatingSystem.IsWindows())
            {
                var root = (now?.Volumes ?? []).Select(letter => $@"{letter}\").FirstOrDefault(IsApplPi);
                if (root is not null)
                {
                    return new BootPartition(root, $"{root.TrimEnd('\\')} (boot partition)");
                }
            }
            else if (now?.Partitions.FirstOrDefault(part => part.Label == Label) is { } partition)
            {
                // A desktop may mount it as soon as it appears
                if (partition.MountPoint is { } mounted && IsApplPi(mounted))
                {
                    return new BootPartition(mounted, $"{partition.Device} (boot partition, at {mounted})");
                }
                if (partition.MountPoint is null && Mount(partition.Device) is { } card)
                {
                    return card;
                }
            }
            Thread.Sleep(1000);
        }
        return null;
    }

    /// <summary>Mounts a partition (Linux, as root) in a new temporary folder, if it's an ApplPi card's boot partition.</summary>
    static BootPartition? Mount(string device)
    {
        var folder = Directory.CreateTempSubdirectory("applpi-boot-").FullName;
        try
        {
            Platform.Run("mount", "-t", "vfat", device, folder);
        }
        catch (IOException)
        {
            Directory.Delete(folder);
            return null;
        }
        void Unmount()
        {
            Platform.Run("umount", folder);
            Directory.Delete(folder);
        }
        if (!IsApplPi(folder))
        {
            Unmount();
            return null;
        }
        var card = new BootPartition(folder, $"{device} (boot partition)", Unmount, device);
        lock (Mounted)
        {
            Mounted.Add(card);
        }
        return card;
    }

    /// <summary>
    /// Whether a directory is the boot partition of an ApplPi OS card. The image's issue.txt names the pi-gen stage
    /// that made it, which Raspberry Pi OS's images don't.
    /// </summary>
    public static bool IsApplPi(string root)
    {
        var issue = Path.Combine(root, "issue.txt");
        return File.Exists(Path.Combine(root, "config.txt")) && File.Exists(issue)
            && File.ReadAllText(issue).Contains("stage2-applpi");
    }
}
