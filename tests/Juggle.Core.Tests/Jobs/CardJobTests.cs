using System.IO.Pipelines;
using System.Text.Json;
using Juggle.Core;
using Juggle.Core.Jobs;

namespace Juggle.Core.Tests.Jobs;

public sealed class CardJobTests : IDisposable
{
    const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleExampleExampleExampleExampleExample example";

    static readonly DeviceSettings Settings = new("juggle-test", "Europe/London", new WifiSettings("GB", "Home", "password1"), [Key]);

    readonly string dir = Directory.CreateTempSubdirectory("juggle-test").FullName;
    readonly string image;
    readonly FakeHost host = new();
    readonly FakeCards cards = new();

    public CardJobTests()
    {
        image = Path.Combine(dir, "juggle-os-v0.8.0.img.xz");
        File.WriteAllBytes(image, new byte[1 << 20]);
        cards.BootPartition = new BootPartition(Directory.CreateDirectory(Path.Combine(dir, "card")).FullName, "test card");
    }

    public void Dispose() => Directory.Delete(dir, recursive: true);

    JsonElement CardSettings() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(cards.BootPartition!.Root, "juggle.json"))).RootElement;

    [Fact]
    public void WritesTheImageAndTheSettingsThenEjectsTheCard()
    {
        Assert.Equal(CardOutcome.Ejected, new CardJob(host, cards).Run(new CardRequest(FakeCards.Disk, image, Settings, true)));

        Assert.Equal([(FakeCards.Disk, image)], cards.Written);
        Assert.Equal([JobStage.Write, JobStage.Check], host.Progresses.OfType<JobProgress>().Select(p => p.Stage));
        Assert.Null(host.Progresses[^1]);
        Assert.Contains("Written and checked.", host.Said);
        Assert.Equal("juggle-test", CardSettings().GetProperty("hostname").GetString());
        Assert.Equal([FakeCards.Disk], cards.Ejected);
    }

    [Fact]
    public void OnlyPutsTheSettingsOnACardToSetUp()
    {
        Assert.Equal(CardOutcome.Done, new CardJob(host, cards).Run(new CardRequest(FakeCards.Disk, null, Settings, false)));

        Assert.Empty(cards.Written);
        Assert.Empty(cards.Ejected);
        Assert.True(cards.BootPartition!.HasPendingSettings);
    }

    [Theory]
    [InlineData("3", "Card Reader", 16_000_000_000)]
    [InlineData("2", "Other Reader", 16_000_000_000)]
    [InlineData("2", "Card Reader", 32_000_000_000)]
    public void WritesNothingToADifferentCard(string id, string name, long size)
    {
        cards.Disks = [new RemovableDisk(id, name, size, [], [])];

        Assert.Equal(CardOutcome.Failed, new CardJob(host, cards).Run(new CardRequest(FakeCards.Disk, image, Settings, true)));

        Assert.Empty(cards.Written);
        Assert.False(cards.BootPartition!.HasPendingSettings);
        Assert.EndsWith("isn't in the card reader any more: nothing was written.", Assert.Single(host.Problems));
    }

    [Fact]
    public void SaysWhenTheBootPartitionDoesntShow()
    {
        cards.BootPartition = null;

        Assert.Equal(CardOutcome.Failed, new CardJob(host, cards).Run(new CardRequest(FakeCards.Disk, image, Settings, true)));

        Assert.Contains("juggle setup", Assert.Single(host.Problems));
        Assert.Empty(cards.Ejected);
    }

    [Fact]
    public void SaysWhenTheCardWasntEjected()
    {
        cards.EjectFails = "The volume is in use.";

        Assert.Equal(CardOutcome.Done, new CardJob(host, cards).Run(new CardRequest(FakeCards.Disk, image, Settings, true)));

        Assert.Equal("The card wasn't ejected (The volume is in use): eject it before taking it out.", Assert.Single(host.Problems));
    }

    [Fact]
    public void ReportsBackOverAStream()
    {
        var toJob = new Pipe();
        var fromJob = new Pipe();
        var job = Task.Run(() =>
        {
            using var output = fromJob.Writer.AsStream();
            return CardJobStream.Serve(toJob.Reader.AsStream(), output, cards);
        });

        var outcome = CardJobStream.Relay(new CardRequest(FakeCards.Disk, image, Settings, true),
            toJob.Writer.AsStream(), fromJob.Reader.AsStream(), host);

        Assert.Equal(CardOutcome.Ejected, outcome);
        Assert.Equal(CardOutcome.Ejected, job.Result);
        Assert.Contains("Written and checked.", host.Said);
        Assert.Contains(new JobProgress(JobStage.Check, 1 << 20, 1 << 20), host.Progresses);
        // The settings arrived whole, secrets included
        Assert.Equal("password1", CardSettings().GetProperty("wifi").GetProperty("password").GetString());
        Assert.Equal([FakeCards.Disk], cards.Ejected);
    }

    [Fact]
    public void ReportsAJobThatEndsWithoutSaying()
    {
        using var nothing = new MemoryStream();

        Assert.Null(CardJobStream.Relay(new CardRequest(FakeCards.Disk, image, Settings, true), Stream.Null, nothing, host));
    }
}
