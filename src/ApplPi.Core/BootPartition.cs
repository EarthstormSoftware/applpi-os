namespace ApplPi.Core;

/// <summary>The boot partition of a card written with the ApplPi OS image, as Windows sees it (FAT, with a drive letter).</summary>
public sealed class BootPartition(string root, string description)
{
    const string SettingsFile = "applpi.json";
    const string ErrorFile = "applpi-error.txt";

    /// <summary>The partition's root directory, such as F:\.</summary>
    public string Root { get; } = root;

    /// <summary>What to show the user: drive letter and card size.</summary>
    public string Description { get; } = description;

    /// <summary>Whether settings are waiting on the card for the Pi to read at its next boot.</summary>
    public bool HasPendingSettings => File.Exists(Path.Combine(Root, SettingsFile));

    /// <summary>
    /// The image's version and build date, from applpi-release.txt (KEY=value lines, as /etc/applpi-release on the
    /// Pi), or null for images made before it was added.
    /// </summary>
    public (string Version, string BuildDate)? ReadRelease()
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
        return (values.GetValueOrDefault("VERSION", "unknown"), values.GetValueOrDefault("BUILD_DATE", "unknown"));
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

    /// <summary>The ApplPi cards in removable drives: the boot partition is labelled bootfs and made by our build.</summary>
    public static IReadOnlyList<BootPartition> FindCards() => DriveInfo.GetDrives()
        .Where(drive => drive.DriveType == DriveType.Removable && drive.IsReady)
        .Where(drive => drive.VolumeLabel == "bootfs" && IsApplPi(drive.RootDirectory.FullName))
        .Select(drive => new BootPartition(drive.RootDirectory.FullName,
            $"{drive.Name.TrimEnd('\\')} (boot partition, {drive.TotalSize / (1024 * 1024)} MB)"))
        .ToList();

    /// <summary>
    /// Whether a directory is the boot partition of an ApplPi OS card. The image's issue.txt names the pi-gen stage
    /// that made it, which Raspberry Pi OS's images don't.
    /// </summary>
    public static bool IsApplPi(string root)
    {
        var issue = Path.Combine(root, "issue.txt");
        return File.Exists(Path.Combine(root, "cmdline.txt")) && File.Exists(issue)
            && File.ReadAllText(issue).Contains("stage2-applpi");
    }
}
