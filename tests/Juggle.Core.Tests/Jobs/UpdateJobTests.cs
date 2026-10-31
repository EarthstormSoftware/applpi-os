using Juggle.Core;
using Juggle.Core.Jobs;
using static Juggle.Core.Tests.Jobs.ScriptedPi;

namespace Juggle.Core.Tests.Jobs;

public sealed class UpdateJobTests : IDisposable
{
    const string NewOs = "v0.8.0 (build 20261031-1200)";

    static readonly string Before = Status("boot1", "A", "A");
    static readonly string Trial = Status("boot2", "B", "A", tryboot: true, check: "checking", slotB: NewOs);
    static readonly string Kept = Status("boot2", "B", "B", tryboot: true, check: "kept", slotB: NewOs);

    readonly string dir = Directory.CreateTempSubdirectory("juggle-test").FullName;
    readonly string image;
    readonly FakeHost host = new();
    readonly FakeClock clock = new();

    public UpdateJobTests()
    {
        image = Path.Combine(dir, "juggle-os-v0.8.0.img.xz");
        File.WriteAllBytes(image, new byte[3 << 20]);
    }

    public void Dispose() => Directory.Delete(dir, recursive: true);

    bool Update(ScriptedPi pi, bool yes = false) => new UpdateJob(host,
        new ImageSource(host, Path.Combine(dir, "deploy")),
        new JugglePaths(Path.Combine(dir, "devices.json"), Path.Combine(dir, "known_hosts"), Path.Combine(dir, "id.pub")),
        _ => pi, clock).Run("juggle-test", image, yes);

    [Fact]
    public void KeepsTheNewSlotOnceThePiHasCheckedItself()
    {
        // The old boot answers until the Pi has gone down, then nothing until it's back
        var pi = new ScriptedPi(Before, Before, null, Trial, Trial, Kept);

        Assert.True(Update(pi));

        Assert.Empty(host.Problems);
        Assert.Contains($"Done: juggle-test runs Juggle OS {NewOs} from slot B.", host.Said);
        Assert.Contains("Writing slot B", host.Said);
        Assert.Contains(host.Said, line => line.StartsWith("Sent all 3 MiB"));
        Assert.DoesNotContain(pi.Commands, command => command.EndsWith(" commit"));
        Assert.Contains(new JobProgress(JobStage.WaitForRestart, 2), host.Progresses);
        Assert.Null(host.Progresses[^1]);
    }

    [Fact]
    public void CommitsAnOsFromBeforeThePiCheckedItself()
    {
        var pi = new ScriptedPi(Before, Status("boot2", "B", "A", tryboot: true, check: null, slotB: NewOs));

        Assert.True(Update(pi));

        Assert.Contains(pi.Commands, command => command.EndsWith(" commit"));
    }

    [Fact]
    public void AsksFirstUnlessToldYes()
    {
        host.Update = false;

        Assert.False(Update(new ScriptedPi(Before)));
        Assert.Contains("Stopped: nothing was changed.", host.Said);

        Assert.True(Update(new ScriptedPi(Before, Trial, Kept), yes: true));
    }

    [Fact]
    public void RefusesAPiOnATrialBoot()
    {
        var pi = new ScriptedPi(Trial);

        Assert.False(Update(pi));

        Assert.Contains("on trial", Assert.Single(host.Problems));
        Assert.Single(pi.Commands);
    }

    [Fact]
    public void RefusesAnOsFromBeforeUpdates()
    {
        Assert.False(Update(new ScriptedPi("!")));
        Assert.Contains("flash it once with juggle flash", Assert.Single(host.Problems));
    }

    [Fact]
    public void AnImageThatIsntWrittenChangesNothing()
    {
        var pi = new ScriptedPi(Before) { WriteFails = "Not enough space." };

        Assert.False(Update(pi));

        Assert.Equal("The update wasn't written: Not enough space. juggle-test still runs slot A, unchanged.",
            Assert.Single(host.Problems));
        Assert.DoesNotContain(pi.Commands, command => command.EndsWith(" try"));
        Assert.Null(host.Progresses[^1]);
    }

    [Fact]
    public void GivesUpOnAPiThatDoesntComeBack()
    {
        Assert.False(Update(new ScriptedPi(Before, null)));

        Assert.StartsWith("juggle-test didn't answer within 10 minutes.", Assert.Single(host.Problems));
        Assert.Null(host.Progresses[^1]);
    }

    [Fact]
    public void SaysWhyAPiCameBackOnTheOldSlot()
    {
        Assert.False(Update(new ScriptedPi(Before, Status("boot2", "A", "A", last: "B failed: the app didn't start"))));

        Assert.Equal([
            "juggle-test came back on slot A: slot B started, but failed its check. the app didn't start.",
            "It still runs Juggle OS v0.7.0 (build 20261030-1200), as before.",
        ], host.Problems);
    }

    [Fact]
    public void SaysWhyThePiDidntKeepTheNewSlot()
    {
        var failed = Status("boot2", "B", "A", tryboot: true, check: "failed: no network", slotB: NewOs);

        Assert.False(Update(new ScriptedPi(Before, Trial, failed)));

        Assert.Equal("juggle-test didn't keep slot B: it goes back to slot A, as it was. no network.",
            Assert.Single(host.Problems));
    }

    [Fact]
    public void NoticesAPiThatRestartedDuringItsCheck()
    {
        Assert.False(Update(new ScriptedPi(Before, Trial, Status("boot3", "A", "A", slotB: NewOs))));

        Assert.StartsWith("juggle-test came back on slot A: slot B started, but didn't keep running. It restarted before",
            host.Problems[0]);
    }

    [Fact]
    public void GivesUpWaitingForTheCheckAfter20Minutes()
    {
        Assert.False(Update(new ScriptedPi(Before, Trial)));

        Assert.StartsWith("juggle-test didn't keep slot B within 20 minutes", Assert.Single(host.Problems));
    }

    [Theory]
    [InlineData("map", true)]
    [InlineData("", false)]
    public void SendsTheBlockMapToAPiThatTakesIt(string features, bool sent)
    {
        File.WriteAllText(Path.Combine(dir, "juggle-os-v0.8.0.bmap"), """
            <bmap version="2.0">
              <ImageSize>4194304</ImageSize>
              <BlockSize>4096</BlockSize>
              <BlockMap>
                <Range chksum="aa">0</Range>
              </BlockMap>
            </bmap>
            """);
        var pi = new ScriptedPi(Status("boot1", "A", "A", features: features), Trial, Kept);

        Assert.True(Update(pi));

        var write = Assert.Single(pi.Commands, command => command.Contains(" write"));
        Assert.Equal(sent, write.EndsWith(" write --map=0-4096"));
        Assert.Equal(!sent, write.EndsWith(" write"));
    }
}
