using System.Text.Json;
using ApplPi.Core;

namespace ApplPi.Core.Tests;

public class SettingsTests
{
    const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleExampleExampleExampleExampleExample example";

    static DeviceSettings Valid() =>
        new("applpi-test", "Europe/London", new WifiSettings("GB", "Example network", "example-password"), [Key]);

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
        Assert.Equal("applpi-test", root.GetProperty("hostname").GetString());
        Assert.Equal("Europe/London", root.GetProperty("timezone").GetString());
        Assert.Equal("GB", root.GetProperty("wifi").GetProperty("country").GetString());
        Assert.Equal("Example network", root.GetProperty("wifi").GetProperty("ssid").GetString());
        Assert.Equal("example-password", root.GetProperty("wifi").GetProperty("password").GetString());
        Assert.Equal(Key, root.GetProperty("ssh_keys")[0].GetString());
    }

    [Fact]
    public void LeavesOutWifiForEthernetOnly()
    {
        using var json = JsonDocument.Parse((Valid() with { Wifi = null }).ToJson());
        Assert.False(json.RootElement.TryGetProperty("wifi", out _));
    }
}
