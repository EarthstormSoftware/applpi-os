using System.Text.Json;
using System.Text.Json.Serialization;

namespace ApplPi.Core;

/// <summary>A Pi's settings, as written to applpi.json on its card's boot partition.</summary>
/// <param name="Hostname">The Pi's name on the network (<c>name.local</c>).</param>
/// <param name="TimeZone">An IANA time zone, such as Europe/London, or null to keep UTC.</param>
/// <param name="Wifi">The Wi-Fi network, or null for Ethernet only.</param>
/// <param name="SshKeys">The SSH public keys that can log in as the applpi user.</param>
public sealed record DeviceSettings(string Hostname, string? TimeZone, WifiSettings? Wifi, IReadOnlyList<string> SshKeys)
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The applpi.json the Pi reads (see image\applpi.example.json).</summary>
    public string ToJson() => JsonSerializer.Serialize(
        new CardFile(Hostname, TimeZone, Wifi is null ? null : new CardWifi(Wifi.Country, Wifi.Ssid, Wifi.Password), SshKeys),
        JsonOptions);

    sealed record CardFile(
        [property: JsonPropertyName("hostname")] string Hostname,
        [property: JsonPropertyName("timezone")] string? TimeZone,
        [property: JsonPropertyName("wifi")] CardWifi? Wifi,
        [property: JsonPropertyName("ssh_keys")] IReadOnlyList<string> SshKeys);

    sealed record CardWifi(
        [property: JsonPropertyName("country")] string Country,
        [property: JsonPropertyName("ssid")] string Ssid,
        [property: JsonPropertyName("password")] string Password);
}

/// <param name="Country">The two-letter country code, which sets the channels the Pi may use.</param>
/// <param name="Ssid">The network name.</param>
/// <param name="Password">The WPA2 password.</param>
public sealed record WifiSettings(string Country, string Ssid, string Password);
