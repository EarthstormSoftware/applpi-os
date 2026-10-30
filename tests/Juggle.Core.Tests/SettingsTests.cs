using System.Text.Json;
using Juggle.Core;

namespace Juggle.Core.Tests;

public class SettingsTests
{
    const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleExampleExampleExampleExampleExample example";

    static DeviceSettings Valid() =>
        new("juggle-test", "Europe/London", new WifiSettings("GB", "Example network", "example-password"), [Key]);

    [Fact]
    public void AcceptsValidSettings() => Assert.Empty(SettingsRules.Check(Valid()));

    [Fact]
    public void AcceptsEthernetOnlyWithoutTimeZone() =>
        Assert.Empty(SettingsRules.Check(Valid() with { Wifi = null, TimeZone = null }));

    [Theory]
    [InlineData("Bad_Name")]
    [InlineData("-x")]
    [InlineData("x-")]
    [InlineData("")]
    public void RejectsBadHostnames(string hostname) => Assert.NotNull(SettingsRules.CheckHostname(hostname));

    [Theory]
    [InlineData("Nowhere/City")]
    [InlineData("../../etc/passwd")]
    [InlineData("GMT Standard Time")]
    public void RejectsUnknownTimeZones(string timeZone) => Assert.NotNull(SettingsRules.CheckTimeZone(timeZone));

    [Theory]
    [InlineData("Europe/London")]
    [InlineData("America/New_York")]
    [InlineData("Etc/UTC")]
    public void AcceptsIanaTimeZones(string timeZone) => Assert.Null(SettingsRules.CheckTimeZone(timeZone));

    [Theory]
    [InlineData("short")]
    [InlineData("pässwörd-with-umlauts")]
    public void RejectsBadPasswords(string password) => Assert.NotNull(SettingsRules.CheckPassword(password));

    [Fact]
    public void RejectsLongSsid() => Assert.NotNull(SettingsRules.CheckSsid(new string('x', 33)));

    [Theory]
    [InlineData("gb")]
    [InlineData("GBR")]
    public void RejectsBadCountries(string country) => Assert.NotNull(SettingsRules.CheckCountry(country));

    [Fact]
    public void NeedsAnSshKey() => Assert.NotEmpty(SettingsRules.Check(Valid() with { SshKeys = [] }));

    [Fact]
    public void RejectsTwoKeysOnOneLine() =>
        Assert.NotNull(SettingsRules.CheckSshKey("ssh-ed25519 AAAA x\nssh-rsa BBBB"));

    [Fact]
    public void WritesTheJsonThePiReads()
    {
        using var json = JsonDocument.Parse(Valid().ToJson());
        var root = json.RootElement;
        Assert.Equal("juggle-test", root.GetProperty("hostname").GetString());
        Assert.Equal("Europe/London", root.GetProperty("timezone").GetString());
        Assert.Equal("GB", root.GetProperty("wifi").GetProperty("country").GetString());
        Assert.Equal("Example network", root.GetProperty("wifi").GetProperty("ssid").GetString());
        Assert.Equal("example-password", root.GetProperty("wifi").GetProperty("password").GetString());
        Assert.Equal(Key, root.GetProperty("ssh_keys")[0].GetString());
    }

    [Fact]
    public void RejectsAHostKeyThatIsntAPrivateKey()
    {
        Assert.NotNull(SettingsRules.CheckHostKey(Key));
        Assert.NotEmpty(SettingsRules.Check(Valid() with { HostKey = "not a key" }));
    }

    [Fact]
    public void WritesTheHostKeyOnlyWhenThereIsOne()
    {
        const string hostKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n";
        using var with = JsonDocument.Parse((Valid() with { HostKey = hostKey }).ToJson());
        Assert.Equal(hostKey, with.RootElement.GetProperty("host_key").GetString());
        using var without = JsonDocument.Parse(Valid().ToJson());
        Assert.False(without.RootElement.TryGetProperty("host_key", out _));
    }

    [Fact]
    public void LeavesOutWifiForEthernetOnly()
    {
        using var json = JsonDocument.Parse((Valid() with { Wifi = null }).ToJson());
        Assert.False(json.RootElement.TryGetProperty("wifi", out _));
    }
}
