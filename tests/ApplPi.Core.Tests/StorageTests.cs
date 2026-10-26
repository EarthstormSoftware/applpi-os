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
    public void BootPartitionReadsTheImageRelease()
    {
        var card = new BootPartition(dir, "test");
        Assert.Null(card.ReadRelease());
        File.WriteAllText(Path.Combine(dir, "applpi-release.txt"), "VERSION=v0.1.0\nCOMMIT=5e8e826\nBUILD_DATE=2026-10-16\n");
        Assert.Equal(("v0.1.0", "2026-10-16"), card.ReadRelease());
        File.WriteAllText(Path.Combine(dir, "applpi-release.txt"),
            "VERSION=v0.1.0-3-g1ad84dc\nCOMMIT=1ad84dc\nBUILD_STAMP=20261016-2249\n");
        Assert.Equal(("v0.1.0-3-g1ad84dc", "20261016-2249 UTC"), card.ReadRelease());
    }

    [Fact]
    public void StoreForgetsAPi()
    {
        var store = new DeviceStore();
        store.Set(new DeviceSettings("oldpi", "Etc/UTC", null, []), "key.pub");
        store.Set(new DeviceSettings("newpi", "Etc/UTC", null, []), "key.pub");
        Assert.True(store.Remove("newpi"));
        Assert.Null(store.Get("newpi"));
        Assert.Equal("oldpi", store.LastDevice);
        Assert.False(store.Remove("newpi"));
        Assert.True(store.Remove("oldpi"));
        Assert.Null(store.LastDevice);
    }

    [Fact]
    public void SshForgetsAHost()
    {
        var knownHosts = Path.Combine(dir, "known_hosts");
        File.WriteAllLines(knownHosts, ["oldpi.local ssh-ed25519 AAAA1", "other ssh-ed25519 AAAA2", "x,OldPi.local ssh-rsa AAAA3"]);
        Assert.True(SshKeys.ForgetHost(knownHosts, "oldpi.local"));
        Assert.Equal(["other ssh-ed25519 AAAA2"], File.ReadAllLines(knownHosts));
        Assert.False(SshKeys.ForgetHost(knownHosts, "oldpi.local"));
        Assert.False(SshKeys.ForgetHost(Path.Combine(dir, "none"), "oldpi.local"));
    }

    [Fact]
    public void BootPartitionRefusesSettingsThePiWouldReject() => Assert.Throws<ArgumentException>(
        () => new BootPartition(dir, "test").WriteSettings(new DeviceSettings("Bad_Name", null, null, [Key])));

    [Fact]
    public void RecognisesApplPiCardsOnly()
    {
        File.WriteAllText(Path.Combine(dir, "config.txt"), "os_prefix=slotA/");
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
    public void StoreKeepsTheHostKeyEncrypted()
    {
        var path = Path.Combine(dir, "devices.json");
        var store = new DeviceStore();
        store.Set(new DeviceSettings("applpi-test", null, null, [Key], HostKey), "key.pub", "ssh-ed25519 AAAAhost");
        store.Save(path);

        Assert.DoesNotContain("BEGIN OPENSSH", File.ReadAllText(path));
        var loaded = DeviceStore.Load(path);
        Assert.Equal(HostKey, loaded.Get("applpi-test")!.Value.Settings.HostKey);
        Assert.Equal("ssh-ed25519 AAAAhost", loaded.GetHostPublicKey("applpi-test"));
    }

    [Fact]
    public void TrustHostKeyReplacesWhatSshKnewForThatNameOnly()
    {
        var path = Path.Combine(dir, "known_hosts");
        File.WriteAllLines(path, [
            "other.local ssh-ed25519 AAAAother",
            "applpi-test.local ssh-ed25519 AAAAold",
            "applpi-test.local,192.0.2.1 ecdsa-sha2-nistp256 AAAAold2",
        ]);

        SshKeys.TrustHostKey(path, "applpi-test.local", "ssh-ed25519 AAAAnew");

        Assert.Equal(["other.local ssh-ed25519 AAAAother", "applpi-test.local ssh-ed25519 AAAAnew"], File.ReadAllLines(path));
    }

    [Fact]
    public void CreatesAHostKeyThePiAccepts()
    {
        var (privateKey, publicKey) = SshKeys.CreateHostKey();
        Assert.Null(SettingsRules.CheckHostKey(privateKey));
        Assert.DoesNotContain('\r', privateKey);
        Assert.StartsWith("ssh-ed25519 AAAA", publicKey);
        Assert.Equal(2, publicKey.Split(' ').Length);
    }

    const string HostKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----\n";

    [Fact]
    public void ImageSizeIsWhereTheLastPartitionEnds()
    {
        // As our images: boot from 8 MiB (256 MiB), root A after it (1.25 GiB)
        var sector = new byte[512];
        sector[510] = 0x55;
        sector[511] = 0xAA;
        void Partition(int entry, uint start, uint sectors)
        {
            BitConverter.TryWriteBytes(sector.AsSpan(446 + entry * 16 + 8), start);
            BitConverter.TryWriteBytes(sector.AsSpan(446 + entry * 16 + 12), sectors);
        }
        Partition(0, 16384, 524288);
        Partition(1, 540672, 2621440);
        Assert.Equal((540672L + 2621440) * 512, ImageWriter.ImageSize(sector));
        Assert.Equal(0, ImageWriter.ImageSize(new byte[512]));
    }

    [Fact]
    public void BlockMapGivesTheMappedPartsOfAChunk()
    {
        // bmaptool's format: whole blocks, inclusive ranges, single blocks without a dash
        var map = BlockMap.Parse("""
            <?xml version="1.0" ?>
            <bmap version="2.0">
                <ImageSize> 1610612736 </ImageSize>
                <BlockSize> 4096 </BlockSize>
                <BlocksCount> 393216 </BlocksCount>
                <MappedBlocksCount> 5 </MappedBlocksCount>
                <ChecksumType> sha256 </ChecksumType>
                <BlockMap>
                    <Range chksum="aa"> 0-1 </Range>
                    <Range chksum="bb"> 1023 </Range>
                    <Range chksum="cc"> 1025-1026 </Range>
                </BlockMap>
            </bmap>
            """);
        const int chunk = 4 << 20;
        Assert.Equal(1610612736, map.ImageSize);
        Assert.Equal(5 * 4096, map.MappedBytes);
        Assert.Equal([(0, 8192), (1023 * 4096, 4096)], map.Mapped(0, chunk));
        Assert.Equal([(4096, 8192)], map.Mapped(chunk, chunk));
        Assert.Empty(map.Mapped(2L * chunk, chunk));
    }

    [Fact]
    public void BlockMapIsFoundBesideTheImage()
    {
        File.WriteAllText(Path.Combine(dir, "applpi-os-v0.1.0.bmap"),
            "<bmap><BlockSize>4096</BlockSize><BlockMap><Range>0</Range></BlockMap></bmap>");
        Assert.NotNull(BlockMap.ForImage(Path.Combine(dir, "applpi-os-v0.1.0.img.xz")));
        Assert.Null(BlockMap.ForImage(Path.Combine(dir, "applpi-os-v0.2.0.img.xz")));
    }

    [Fact]
    public void ReadsKeysFromAPublicKeyFile()
    {
        var path = Path.Combine(dir, "id_ed25519.pub");
        File.WriteAllText(path, Key + "\r\n\r\n");
        Assert.Equal([Key], SshKeys.Read(path));
    }
}
