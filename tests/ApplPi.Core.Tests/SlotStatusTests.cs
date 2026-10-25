using ApplPi.Core;

namespace ApplPi.Core.Tests;

public class SlotStatusTests
{
    const string FreshCard = """
        boot=0b9c2f43-8f5d-4a71-9a0e-6f1f3c1d2e7a
        running=A
        tryboot=0
        permanent=A
        waiting=
        slotA=v0.1.2 (build 20261025-1012)
        slotB=
        """;

    [Fact]
    public void ReadsTheSlots()
    {
        var status = SlotStatus.Parse(FreshCard.Replace("\n", "\r\n"));
        Assert.Equal("0b9c2f43-8f5d-4a71-9a0e-6f1f3c1d2e7a", status.Boot);
        Assert.Equal("A", status.Running);
        Assert.False(status.Tryboot);
        Assert.Equal("A", status.Permanent);
        Assert.Equal("", status.Waiting);
        Assert.Equal("B", status.Other);
        Assert.Equal("v0.1.2 (build 20261025-1012)", status.Holds("A"));
        Assert.Equal("nothing yet", status.Holds("B"));
    }

    [Fact]
    public void ReadsATrialBoot()
    {
        var status = SlotStatus.Parse(FreshCard.Replace("running=A", "running=B").Replace("tryboot=0", "tryboot=1")
            .Replace("waiting=", "waiting=B"));
        Assert.True(status.Tryboot);
        Assert.Equal("B", status.Running);
        Assert.Equal("A", status.Other);
        Assert.Equal("B", status.Waiting);
    }

    [Fact]
    public void RefusesAnIncompleteStatus() =>
        Assert.Throws<InvalidOperationException>(() => SlotStatus.Parse("running=A\n"));
}
