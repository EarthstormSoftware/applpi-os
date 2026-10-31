using Juggle.Core;
using Juggle.Core.Jobs;

namespace Juggle.Core.Tests.Jobs;

public sealed class FlashJobTests : IDisposable
{
    const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleExampleExampleExampleExampleExample example";
    const string HostKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n";

    static readonly RemovableDisk Disk = new("2", "Card Reader", 16_000_000_000, [], []);

    readonly string dir = Directory.CreateTempSubdirectory("juggle-test").FullName;
    readonly string image;
    readonly BootPartition card;
    readonly FakeHost host = new();
    readonly SetupJob setup;
    readonly List<RemovableDisk> written = [];

    public FlashJobTests()
    {
        var paths = new JugglePaths(Path.Combine(dir, "devices.json"), Path.Combine(dir, "known_hosts"), Path.Combine(dir, "id.pub"));
        File.WriteAllText(paths.PublicKey, Key + "\n");
        setup = new SetupJob(host, paths, () => (HostKey, "ssh-ed25519 AAAAhost"));
        image = Path.Combine(dir, "juggle-os-v0.8.0.img.xz");
        File.WriteAllBytes(image, new byte[1 << 20]);
        card = new BootPartition(Directory.CreateDirectory(Path.Combine(dir, "card")).FullName, "test card");
    }

    public void Dispose() => Directory.Delete(dir, recursive: true);

    FlashJob Job(IReadOnlyList<RemovableDisk> disks, BootPartition? bootPartition) => new(host, setup, () => disks,
        (disk, _, progress) =>
        {
            written.Add(disk);
            progress.Report(new WriteProgress(false, 1 << 19, 1 << 20));
            progress.Report(new WriteProgress(true, 1 << 20, 1 << 20));
        },
        _ => bootPartition);

    [Fact]
    public void WritesTheCardThenSetsItUp()
    {
        Assert.True(Job([Disk], card).Run(image));

        Assert.Equal([Disk], written);
        Assert.Equal([JobStage.Write, JobStage.Check], host.Progresses.OfType<JobProgress>().Select(p => p.Stage));
        Assert.Null(host.Progresses[^1]);
        Assert.Contains("Written and checked.", host.Said);
        Assert.True(card.HasPendingSettings);
    }

    [Fact]
    public void SaysHowMuchItWritesWithABlockMap()
    {
        File.WriteAllText(Path.Combine(dir, "juggle-os-v0.8.0.bmap"), """
            <bmap version="2.0">
              <ImageSize>4194304</ImageSize>
              <BlockSize>4096</BlockSize>
              <BlockMap>
                <Range chksum="aa">0-255</Range>
              </BlockMap>
            </bmap>
            """);

        Assert.True(Job([Disk], card).Run(image));

        Assert.Contains(host.Said, line => line.StartsWith("Writing 1 MiB of the 4 MiB image"));
    }

    [Fact]
    public void WithoutACardNothingIsWritten()
    {
        Assert.False(Job([], card).Run(image));

        Assert.Empty(written);
        Assert.Equal("No card found: put the card in the card reader and try again.", Assert.Single(host.Problems));
    }

    [Fact]
    public void UnconfirmedNothingIsWritten()
    {
        host.Erase = false;

        Assert.False(Job([Disk], card).Run(image));

        Assert.Empty(written);
        Assert.Contains("Stopped: nothing was written.", host.Said);
    }

    [Fact]
    public void WithoutTheBootPartitionItSaysToSetItUpLater()
    {
        Assert.False(Job([Disk], null).Run(image));

        Assert.Equal([Disk], written);
        Assert.Contains("juggle setup", Assert.Single(host.Problems));
    }
}
