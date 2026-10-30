using Juggle.Core;

namespace Juggle.Core.Tests;

/// <summary>Which disks in lsblk's JSON juggle offers to write on Linux (see RemovableDisk.FromLsblk).</summary>
public sealed class LinuxDiskTests
{
    // A Pi running Juggle OS, with a card reader in a USB port and a USB SSD: as lsblk --json --bytes shows them
    const string PiWithReaders = """
        {"blockdevices": [
          {"name": "sda", "type": "disk", "size": 31914983424, "vendor": "Generic ", "model": "STORAGE DEVICE  ",
           "tran": "usb", "rm": true, "label": null, "mountpoints": [null],
           "children": [
             {"name": "sda1", "type": "part", "size": 402653184, "label": "bootfs", "mountpoints": ["/media/someone/bootfs"]},
             {"name": "sda2", "type": "part", "size": 2147483648, "label": "rootfs", "mountpoints": [null]}
           ]},
          {"name": "sdb", "type": "disk", "size": 500107862016, "vendor": "Samsung", "model": "Portable SSD",
           "tran": "usb", "rm": false, "label": null, "mountpoints": [null]},
          {"name": "mmcblk0", "type": "disk", "size": 15931539456, "vendor": null, "model": null,
           "tran": "mmc", "rm": false, "label": null, "mountpoints": [null],
           "children": [
             {"name": "mmcblk0p1", "type": "part", "size": 402653184, "label": "bootfs", "mountpoints": ["/boot/firmware"]},
             {"name": "mmcblk0p2", "type": "part", "size": 2147483648, "label": "rootfs", "mountpoints": ["/media/root-ro"]},
             {"name": "mmcblk0p4", "type": "part", "size": 8589934592, "label": "data", "mountpoints": ["/data"]}
           ]}
        ]}
        """;

    static string? AllSd(string name) => "SD";

    [Fact]
    public void OffersTheCardReaderOnly()
    {
        var disk = Assert.Single(RemovableDisk.FromLsblk(PiWithReaders, AllSd));
        Assert.Equal("sda", disk.Id);
        Assert.Equal("Generic STORAGE DEVICE", disk.Name);
        Assert.Equal(31914983424, disk.Size);
        Assert.Equal(["/media/someone/bootfs"], disk.Volumes);
        Assert.Equal([new DiskPartition("/dev/sda1", "bootfs", "/media/someone/bootfs"), new DiskPartition("/dev/sda2", "rootfs", null)],
            disk.Partitions);
    }

    [Fact]
    public void NeverOffersTheCardThePiRunsFrom()
    {
        // It's an SD card on the MMC bus, as a laptop's card slot is, but its partitions are the system's
        Assert.DoesNotContain(RemovableDisk.FromLsblk(PiWithReaders, AllSd), disk => disk.Id == "mmcblk0");
    }

    [Theory]
    [InlineData("/", false)]
    [InlineData("/boot/firmware", false)]
    [InlineData("/media/root-ro", false)]
    [InlineData("/home", false)]
    [InlineData("[SWAP]", false)]
    [InlineData("/media/someone/bootfs", true)]
    [InlineData("/run/media/someone/bootfs", true)]
    [InlineData("/mnt/card", true)]
    public void OffersACardOnlyIfMountedWhereRemovableMediaGo(string mountPoint, bool offered)
    {
        var json = $$"""
            {"blockdevices": [{"name": "sdc", "type": "disk", "size": 8000000000, "tran": "usb", "rm": true,
              "mountpoints": [null], "children": [{"name": "sdc1", "type": "part", "mountpoints": ["{{mountPoint}}"]}]}]}
            """;
        Assert.Equal(offered, RemovableDisk.FromLsblk(json, AllSd).Count == 1);
    }

    [Fact]
    public void OffersALaptopsCardSlotButNotItsBuiltInStorage()
    {
        const string json = """
            {"blockdevices": [
              {"name": "mmcblk0", "type": "disk", "size": 64000000000, "tran": "mmc", "rm": false, "mountpoints": [null]},
              {"name": "mmcblk1", "type": "disk", "size": 32000000000, "tran": "mmc", "rm": false, "mountpoints": [null]}
            ]}
            """;
        var disk = Assert.Single(RemovableDisk.FromLsblk(json, name => name == "mmcblk1" ? "SD" : "MMC"));
        Assert.Equal("mmcblk1", disk.Id);
        Assert.Equal("Card reader", disk.Name);
    }

    [Fact]
    public void LeavesOutEmptyReadersVirtualDisksAndPartitions()
    {
        // WSL's own disks have no transport; an empty card reader has no size
        const string json = """
            {"blockdevices": [
              {"name": "sdd", "type": "disk", "size": 1099511627776, "tran": null, "rm": false, "mountpoints": ["/"]},
              {"name": "sde", "type": "disk", "size": 0, "tran": "usb", "rm": true, "mountpoints": [null]},
              {"name": "loop0", "type": "loop", "size": 1000000, "tran": null, "rm": false, "mountpoints": [null]}
            ]}
            """;
        Assert.Empty(RemovableDisk.FromLsblk(json, AllSd));
    }

    [Fact]
    public void ReadsOlderLsblks()
    {
        // util-linux before 2.33 gives numbers and flags as strings, and before 2.37 one mount point
        const string json = """
            {"blockdevices": [
              {"name": "sdb", "type": "disk", "size": "8000000000", "tran": "usb", "rm": "1", "model": "Reader",
               "mountpoint": null, "children": [{"name": "sdb1", "type": "part", "label": "bootfs", "mountpoint": "/media/someone/bootfs"}]},
              {"name": "sdc", "type": "disk", "size": "8000000000", "tran": "usb", "rm": "1",
               "mountpoint": null, "children": [{"name": "sdc1", "type": "part", "mountpoint": "/"}]}
            ]}
            """;
        var disk = Assert.Single(RemovableDisk.FromLsblk(json, AllSd));
        Assert.Equal(8000000000, disk.Size);
        Assert.Equal(["/media/someone/bootfs"], disk.Volumes);
    }
}
