using System.Text;
using System.Text.RegularExpressions;

namespace ApplPi.Core;

/// <summary>
/// The rules the Pi checks applpi.json against (image\stage2-applpi\02-system\files\apply-settings). The Pi deletes a
/// file that breaks them, so they are checked here first. Each check returns null when the value is fine, or what's
/// wrong with it.
/// </summary>
public static partial class SettingsRules
{
    [GeneratedRegex("^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex HostnamePattern();

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex CountryPattern();

    [GeneratedRegex(@"^(ssh-ed25519|ssh-rsa|ecdsa-sha2-nistp(256|384|521)|sk-ssh-ed25519@openssh\.com" +
                    @"|sk-ecdsa-sha2-nistp256@openssh\.com) [A-Za-z0-9+/]+=*( [^\r\n]*)?$")]
    private static partial Regex SshKeyPattern();

    [GeneratedRegex(@"^-----BEGIN OPENSSH PRIVATE KEY-----\n[A-Za-z0-9+/=\n]+-----END OPENSSH PRIVATE KEY-----\n?$")]
    private static partial Regex HostKeyPattern();

    public static string? CheckHostKey(string hostKey) => HostKeyPattern().IsMatch(hostKey.Replace("\r\n", "\n").Trim() + "\n")
        ? null
        : "The host key must be an OpenSSH private key.";

    public static string? CheckHostname(string hostname) => HostnamePattern().IsMatch(hostname)
        ? null
        : "The name must be 1-63 lower-case letters, digits and hyphens, not starting or ending with a hyphen.";

    public static string? CheckTimeZone(string timeZone) =>
        timeZone.Contains('/') && TimeZoneInfo.TryConvertIanaIdToWindowsId(timeZone, out _)
            ? null
            : $"Unknown time zone: {timeZone}. Use a name such as Europe/London.";

    public static string? CheckCountry(string country) => CountryPattern().IsMatch(country)
        ? null
        : "The country must be a two-letter code, such as GB.";

    public static string? CheckSsid(string ssid) => Encoding.UTF8.GetByteCount(ssid) is >= 1 and <= 32
        ? null
        : "The network name must be 1-32 bytes.";

    public static string? CheckPassword(string password) =>
        password.Length is >= 8 and <= 63 && password.All(c => c is >= ' ' and <= '~')
            ? null
            : "The Wi-Fi password must be 8-63 printable ASCII characters.";

    public static string? CheckSshKey(string key) => SshKeyPattern().IsMatch(key.Trim())
        ? null
        : "That isn't an SSH public key (a line such as \"ssh-ed25519 AAAA... name\").";

    /// <summary>Everything wrong with the settings: empty when the Pi will accept them.</summary>
    public static IReadOnlyList<string> Check(DeviceSettings settings)
    {
        var errors = new List<string?> { CheckHostname(settings.Hostname) };
        if (settings.TimeZone is not null)
        {
            errors.Add(CheckTimeZone(settings.TimeZone));
        }
        if (settings.Wifi is not null)
        {
            errors.Add(CheckCountry(settings.Wifi.Country));
            errors.Add(CheckSsid(settings.Wifi.Ssid));
            errors.Add(CheckPassword(settings.Wifi.Password));
        }
        errors.Add(settings.SshKeys.Count == 0 ? "At least one SSH key is needed: it's the only way in." : null);
        errors.AddRange(settings.SshKeys.Select(CheckSshKey));
        if (settings.HostKey is not null)
        {
            errors.Add(CheckHostKey(settings.HostKey));
        }
        return errors.OfType<string>().Distinct().ToList();
    }
}
