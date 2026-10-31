using Juggle.Core;
using Juggle.Core.Jobs;

namespace Juggle.Core.Tests.Jobs;

public sealed class FlashJobTests : IDisposable
{
    const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleExampleExampleExampleExampleExample example";
    const string HostKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n";

    readonly string dir = Directory.CreateTempSubdirectory("juggle-test").FullName;
    readonly JugglePaths paths;
    readonly string image;
    readonly FakeHost host = new();
    readonly FakeCards cards = new();
    readonly SetupJob setup;

    public FlashJobTests()
    {
        paths = new JugglePaths(Path.Combine(dir, "devices.json"), Path.Combine(dir, "known_hosts"), Path.Combine(dir, "id.pub"));
        File.WriteAllText(paths.PublicKey, Key + "\n");
        setup = new SetupJob(host, paths, () => (HostKey, "ssh-ed25519 AAAAhost"), cards);
        image = Path.Combine(dir, "juggle-os-v0.8.0.img.xz");
        File.WriteAllBytes(image, new byte[1 << 20]);
    }

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void HasTheCardWrittenWithTheSettingsThenRemembersThem()
    {
        var cardJob = new FakeCardJob();

        Assert.True(new FlashJob(host, setup, cardJob, cards).Run(image, eject: true));

        var request = Assert.Single(cardJob.Requests);
        Assert.Equal(FakeCards.Disk, request.Disk);
        Assert.Equal(image, request.Image);
        Assert.Equal("juggle-test", request.Settings.Hostname);
        Assert.Equal(HostKey, request.Settings.HostKey);
        Assert.True(request.Eject);
        Assert.Equal("juggle-test", DeviceStore.Load(paths.DeviceStore).LastDevice);
        Assert.Contains(host.Said, line => line.StartsWith("The card is ejected: take it out"));
    }

    [Fact]
    public void SaysToEjectACardThatWasnt()
    {
        Assert.True(new FlashJob(host, setup, new FakeCardJob(CardOutcome.Done), cards).Run(image, eject: false));
        Assert.Contains(host.Said, line => line.StartsWith("Eject the card, put it in the Pi"));
    }

    [Fact]
    public void RemembersNothingIfTheCardWasntWritten()
    {
        Assert.False(new FlashJob(host, setup, new FakeCardJob(CardOutcome.Failed), cards).Run(image, eject: true));

        Assert.False(File.Exists(paths.DeviceStore));
        Assert.False(File.Exists(paths.KnownHosts));
    }

    [Fact]
    public void WithoutACardNothingIsAsked()
    {
        cards.Disks = [];
        var cardJob = new FakeCardJob();

        Assert.False(new FlashJob(host, setup, cardJob, cards).Run(image, eject: true));

        Assert.Empty(cardJob.Requests);
        Assert.Null(host.Form);
        Assert.Equal("No card found: put the card in the card reader and try again.", Assert.Single(host.Problems));
    }

    [Fact]
    public void AsksForTheSettingsBeforeConfirming()
    {
        host.Erase = false;
        var cardJob = new FakeCardJob();

        Assert.False(new FlashJob(host, setup, cardJob, cards).Run(image, eject: true));

        Assert.NotNull(host.Form);
        Assert.Empty(cardJob.Requests);
        Assert.Contains("Stopped: nothing was written.", host.Said);
        Assert.False(File.Exists(paths.DeviceStore));
    }
}
