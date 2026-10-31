using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Juggle.Core;

/// <summary>
/// The settings of each Pi, kept on this PC in %APPDATA%\Juggle\devices.json, or ~/.config/juggle/devices.json on Linux
/// (never in the repository), so they needn't be typed again. Wi-Fi passwords and host keys are encrypted for the
/// current Windows user (DPAPI). Linux has nothing like it without a desktop's keyring, so there the file is one only
/// its user can read (as ~/.ssh's keys are), and they're marked as not encrypted.
/// </summary>
public sealed class DeviceStore
{
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Before a secret that isn't encrypted (Linux): base64 after it
    const string Plain = "plain:";

    public static string DefaultPath { get; } = Path.Combine(Platform.ConfigDirectory, "devices.json");

    /// <summary>The Pi set up most recently, whose settings are the defaults for the next one.</summary>
    public string? LastDevice { get; set; }

    /// <summary>The Pi chosen as the default: new Pis start from its settings, whichever was set up last.</summary>
    public string? DefaultDevice { get; set; }

    /// <summary>The Pi a new one starts from: the default, or else the last one set up.</summary>
    [JsonIgnore]
    public string? StartingDevice => DefaultDevice is { } chosen && Devices.ContainsKey(chosen) ? chosen : LastDevice;

    public Dictionary<string, StoredDevice> Devices { get; set; } = [];

    public static DeviceStore Load(string path) => File.Exists(path)
        ? JsonSerializer.Deserialize<DeviceStore>(File.ReadAllText(path), JsonOptions) ?? new DeviceStore()
        : new DeviceStore();

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        var json = JsonSerializer.Serialize(this, JsonOptions);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, json);
            return;
        }
        // Only its user may read it, from the start
        const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        Platform.CreateUserDirectory(directory, Private | UnixFileMode.UserExecute);
        using (var file = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            UnixCreateMode = Private,
        }))
        using (var writer = new StreamWriter(file))
        {
            writer.Write(json);
        }
        File.SetUnixFileMode(path, Private);
        Platform.GiveToUser(path);
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
            : new WifiSettings(device.WifiCountry ?? "", device.WifiSsid, Unprotect(device.WifiPassword) ?? "");
        var hostKey = Unprotect(device.HostKey);
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
        if (DefaultDevice == hostname)
        {
            DefaultDevice = null;
        }
        return true;
    }

    static string Protect(string secret) => OperatingSystem.IsWindows()
        ? Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser))
        : Plain + Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));

    /// <summary>
    /// A stored secret, or null if there isn't one or it can't be read here (encrypted by Windows, in a file copied to
    /// Linux): then it's asked for, or made, again.
    /// </summary>
    static string? Unprotect(string? secret) => secret switch
    {
        null => null,
        _ when secret.StartsWith(Plain, StringComparison.Ordinal) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(secret[Plain.Length..])),
        _ when OperatingSystem.IsWindows() => Encoding.UTF8.GetString(
            ProtectedData.Unprotect(Convert.FromBase64String(secret), null, DataProtectionScope.CurrentUser)),
        _ => null,
    };
}

public sealed class StoredDevice
{
    public string? TimeZone { get; set; }
    public string? WifiCountry { get; set; }
    public string? WifiSsid { get; set; }

    /// <summary>Encrypted for the current Windows user (DPAPI), base64; on Linux "plain:" and base64 (see <see cref="DeviceStore"/>).</summary>
    public string? WifiPassword { get; set; }

    /// <summary>The Pi's SSH host key (private), encrypted like <see cref="WifiPassword"/>.</summary>
    public string? HostKey { get; set; }

    /// <summary>The public half of <see cref="HostKey"/>, for known_hosts.</summary>
    public string? HostPublicKey { get; set; }

    public string SshKeyFile { get; set; } = "";
}
