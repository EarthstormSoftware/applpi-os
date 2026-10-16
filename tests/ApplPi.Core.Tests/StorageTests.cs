using ApplPi.Core;

namespace ApplPi.Core.Tests;

public sealed class StorageTests : IDisposable
{
    const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleExampleExampleExampleExampleExample example";

    readonly string dir = Directory.CreateTempSubdirectory("applpi-test").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void BootPartitionWritesSettingsAndClearsTheError()
    {
        File.WriteAllText(Path.Combine(dir, "applpi-error.txt"), "applpi.json was not used: bad\n");
        var card = new BootPartition(dir, "test");
        Assert.Equal("applpi.json was not used: bad", card.ReadError());

        card.WriteSettings(new DeviceSettings("applpi-test", null, null, [Key]));

        Assert.True(card.HasPendingSettings);
        Assert.Null(card.ReadError());
    }

    [Fact]
    public void BootPartitionRefusesSettingsThePiWouldReject() => Assert.Throws<ArgumentException>(
        () => new BootPartition(dir, "test").WriteSettings(new DeviceSettings("Bad_Name", null, null, [Key])));

    [Fact]
    public void RecognisesApplPiCardsOnly()
    {
        File.WriteAllText(Path.Combine(dir, "cmdline.txt"), "console=tty1");
        File.WriteAllText(Path.Combine(dir, "issue.txt"), "Raspberry Pi reference\nGenerated using pi-gen, stage2");
        Assert.False(BootPartition.IsApplPi(dir));
        File.WriteAllText(Path.Combine(dir, "issue.txt"), "Raspberry Pi reference\nGenerated using pi-gen, stage2-applpi");
        Assert.True(BootPartition.IsApplPi(dir));
    }

    [Fact]
    public void StoreKeepsSettingsWithTheWifiPasswordEncrypted()
    {
        var path = Path.Combine(dir, "devices.json");
        var store = new DeviceStore();
        var settings = new DeviceSettings("applpi-test", "Europe/London",
            new WifiSettings("GB", "Example network", "example-password"), [Key]);
        store.Set(settings, @"C:\keys\id_ed25519.pub");
        store.Save(path);

        Assert.DoesNotContain("example-password", File.ReadAllText(path));
        var loaded = DeviceStore.Load(path);
        Assert.Equal("applpi-test", loaded.LastDevice);
        var (stored, keyFile) = loaded.Get("applpi-test")!.Value;
        Assert.Equal(settings.Wifi, stored.Wifi);
        Assert.Equal("Europe/London", stored.TimeZone);
        Assert.Equal(@"C:\keys\id_ed25519.pub", keyFile);
        Assert.Null(loaded.Get("unknown"));
    }

    [Fact]
    public void ReadsKeysFromAPublicKeyFile()
    {
        var path = Path.Combine(dir, "id_ed25519.pub");
        File.WriteAllText(path, Key + "\r\n\r\n");
        Assert.Equal([Key], SshKeys.Read(path));
    }
}
