using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ApplPi.Core;

/// <summary>
/// The settings of each Pi, kept on this PC in %APPDATA%\ApplPi\devices.json (never in the repository) so they needn't
/// be typed again. Wi-Fi passwords are encrypted for the current Windows user (DPAPI).
/// </summary>
public sealed class DeviceStore
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ApplPi", "devices.json");

    /// <summary>The Pi set up most recently, whose settings are the defaults for the next one.</summary>
    public string? LastDevice { get; set; }

    public Dictionary<string, StoredDevice> Devices { get; set; } = [];

    public static DeviceStore Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<DeviceStore>(File.ReadAllText(path), JsonOptions) ?? new DeviceStore()
        : new DeviceStore();

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>The stored settings of a Pi (with its Wi-Fi password decrypted), or null if it isn't known.</summary>
    public (DeviceSettings Settings, string SshKeyFile)? Get(string hostname)
    {
        if (!Devices.TryGetValue(hostname, out var device))
        {
            return null;
        }
        var wifi = device.WifiSsid is null
            ? null
            : new WifiSettings(device.WifiCountry ?? "", device.WifiSsid, Unprotect(device.WifiPassword));
        var hostKey = device.HostKey is null ? null : Unprotect(device.HostKey);
        return (new DeviceSettings(hostname, device.TimeZone, wifi, [], hostKey), device.SshKeyFile);
    }

    /// <summary>The public half of a Pi's stored host key, or null.</summary>
    public string? GetHostPublicKey(string hostname) => Devices.GetValueOrDefault(hostname)?.HostPublicKey;

    /// <summary>
    /// Stores a Pi's settings (its SSH keys by the file they came from, its host key encrypted) and makes it the last
    /// one set up.
    /// </summary>
    public void Set(DeviceSettings settings, string sshKeyFile, string? hostPublicKey = null)
    {
        Devices[settings.Hostname] = new StoredDevice
        {
            TimeZone = settings.TimeZone,
            WifiCountry = settings.Wifi?.Country,
            WifiSsid = settings.Wifi?.Ssid,
            WifiPassword = settings.Wifi is null ? null : Protect(settings.Wifi.Password),
            SshKeyFile = sshKeyFile,
            HostKey = settings.HostKey is null ? null : Protect(settings.HostKey),
            HostPublicKey = hostPublicKey,
        };
        LastDevice = settings.Hostname;
    }

    /// <summary>
    /// Forgets a Pi's settings. If it was the last one set up, the next one's defaults come from another (or none).
    /// Returns whether it was known.
    /// </summary>
    public bool Remove(string hostname)
    {
        if (!Devices.Remove(hostname))
        {
            return false;
        }
        if (LastDevice == hostname)
        {
            LastDevice = Devices.Keys.LastOrDefault();
        }
        return true;
    }

    static string Protect(string secret) => Convert.ToBase64String(
        ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));

    static string Unprotect(string? secret) => secret is null
        ? ""
        : Encoding.UTF8.GetString(
            ProtectedData.Unprotect(Convert.FromBase64String(secret), null, DataProtectionScope.CurrentUser));
}

public sealed class StoredDevice
{
    public string? TimeZone { get; set; }
    public string? WifiCountry { get; set; }
    public string? WifiSsid { get; set; }

    /// <summary>Encrypted for the current Windows user (DPAPI), base64.</summary>
    public string? WifiPassword { get; set; }

    /// <summary>The Pi's SSH host key (private), encrypted like <see cref="WifiPassword"/>.</summary>
    public string? HostKey { get; set; }

    /// <summary>The public half of <see cref="HostKey"/>, for known_hosts.</summary>
    public string? HostPublicKey { get; set; }

    public string SshKeyFile { get; set; } = "";
}
