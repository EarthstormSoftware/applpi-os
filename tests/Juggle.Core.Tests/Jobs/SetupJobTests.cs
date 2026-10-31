using System.Text.Json;
using Juggle.Core;
using Juggle.Core.Jobs;

namespace Juggle.Core.Tests.Jobs;

public sealed class SetupJobTests : IDisposable
{
    const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleExampleExampleExampleExampleExample example";
    const string HostKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n";

    readonly string dir = Directory.CreateTempSubdirectory("juggle-test").FullName;
    readonly JugglePaths paths;
    readonly BootPartition card;
    readonly FakeHost host = new();
    readonly FakeCards cards = new();
    readonly FakeCardJob cardJob = new();
    int hostKeysMade;

    public SetupJobTests()
    {
        paths = new JugglePaths(Path.Combine(dir, "devices.json"), Path.Combine(dir, "known_hosts"), Path.Combine(dir, "id.pub"));
        File.WriteAllText(paths.PublicKey, Key + "\n");
        card = new BootPartition(Directory.CreateDirectory(Path.Combine(dir, "card")).FullName, "test card");
    }

    public void Dispose() => Directory.Delete(dir, recursive: true);

    SetupJob Job() => new(host, paths, () =>
    {
        hostKeysMade++;
        return (HostKey, $"ssh-ed25519 AAAAhost{hostKeysMade}");
    }, cards);

    bool Run(string? name, params BootPartition[] found) => Job().Run(name, found, [], cardJob, eject: false);

    [Fact]
    public void EjectsTheCardWhenAsked()
    {
        var onDisk = new BootPartition(card.Root, "test card", disk: FakeCards.Disk);

        Assert.True(Job().Run(null, [onDisk], [], cardJob, eject: true));

        Assert.Equal([FakeCards.Disk], cards.Ejected);
        Assert.Contains(host.Said, line => line.StartsWith("The card is ejected: take it out"));
    }

    [Fact]
    public void ACardNotInACardReaderIsntEjected()
    {
        Assert.True(Job().Run(null, [card], [], cardJob, eject: true));

        Assert.Empty(cards.Ejected);
        Assert.Contains(host.Said, line => line.StartsWith("Eject the card, put it in the Pi"));
    }

    [Fact]
    public void ACardOnlyRootCanMountGoesToTheCardJob()
    {
        Assert.True(Job().Run(null, [], [FakeCards.Disk], cardJob, eject: true));

        var request = Assert.Single(cardJob.Requests);
        Assert.Null(request.Image);
        Assert.Equal(FakeCards.Disk, request.Disk);
        Assert.Equal("juggle-test", request.Settings.Hostname);
        Assert.Equal("juggle-test", DeviceStore.Load(paths.DeviceStore).LastDevice);
    }

    [Fact]
    public void RemembersNothingIfTheCardJobFailed()
    {
        Assert.False(Job().Run(null, [], [FakeCards.Disk], new FakeCardJob(CardOutcome.Failed), eject: true));
        Assert.False(File.Exists(paths.DeviceStore));
    }

    JsonElement CardSettings() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(card.Root, "juggle.json"))).RootElement;

    [Fact]
    public void PutsTheSettingsOnTheCardAndRemembersThem()
    {
        host.Settings = new SettingsAnswers("Juggle-Test", "Europe/London", new WifiSettings("GB", "Home", "password1"));

        Assert.True(Run(null, card));

        Assert.Equal("juggle-test", CardSettings().GetProperty("hostname").GetString());
        var stored = DeviceStore.Load(paths.DeviceStore);
        Assert.Equal("juggle-test", stored.LastDevice);
        Assert.Equal("password1", stored.Get("juggle-test")!.Value.Settings.Wifi!.Password);
        Assert.Equal(["juggle-test.local ssh-ed25519 AAAAhost1"], File.ReadAllLines(paths.KnownHosts));
        Assert.Contains("  ssh juggle@juggle-test.local", host.Said);
        Assert.DoesNotContain(host.Said, line => line.Contains("Ethernet"));
        Assert.Empty(host.Problems);
    }

    [Fact]
    public void APiSetUpAgainKeepsItsHostKey()
    {
        Assert.True(Run(null, card));
        Assert.True(Run(null, card));

        Assert.Equal(1, hostKeysMade);
        Assert.Equal(["juggle-test.local ssh-ed25519 AAAAhost1"], File.ReadAllLines(paths.KnownHosts));
    }

    [Fact]
    public void ANewPiStartsFromTheLastPisSettings()
    {
        host.Settings = new SettingsAnswers("first-pi", "America/New_York", new WifiSettings("US", "Home", "password1"));
        Assert.True(Run(null, card));

        Assert.True(Run(null, card));

        Assert.Equal("first-pi", host.Form!.Hostname);
        var defaults = host.Form.DefaultsFor("second-pi");
        Assert.Equal("America/New_York", defaults.TimeZone);
        Assert.Equal("Home", defaults.Wifi!.Ssid);
        Assert.Equal("US", defaults.Country);
    }

    [Fact]
    public void ANewPiStartsFromTheDefaultPiWhicheverWasSetUpLast()
    {
        host.Settings = new SettingsAnswers("chosen-pi", "America/New_York", new WifiSettings("US", "Home", "password1"));
        Assert.True(Run(null, card));
        host.Settings = new SettingsAnswers("other-pi", "Europe/Paris", null);
        Assert.True(Run(null, card));
        var store = DeviceStore.Load(paths.DeviceStore);
        store.DefaultDevice = "chosen-pi";
        store.Save(paths.DeviceStore);

        Assert.True(Run(null, card));

        Assert.Equal("chosen-pi", host.Form!.Hostname);
        Assert.Equal("America/New_York", host.Form.DefaultsFor("third-pi").TimeZone);
        // Setting up another Pi leaves the default as it was
        Assert.Equal("chosen-pi", DeviceStore.Load(paths.DeviceStore).DefaultDevice);
    }

    [Fact]
    public void ANameGivenIsOfferedInLowerCase()
    {
        Assert.True(Run("Kitchen", card));
        Assert.Equal("kitchen", host.Form!.Hostname);
    }

    [Fact]
    public void WithoutWifiItSaysToUseACable()
    {
        Assert.True(Run(null, card));
        Assert.Contains("No Wi-Fi: connect the Pi with an Ethernet cable.", host.Said);
        Assert.False(CardSettings().TryGetProperty("wifi", out _));
    }

    [Fact]
    public void SettingsThatWontWorkAreNotWritten()
    {
        File.WriteAllText(paths.PublicKey, "not a key\n");

        Assert.False(Run(null, card));

        Assert.Single(host.Problems);
        Assert.False(card.HasPendingSettings);
        Assert.False(File.Exists(paths.DeviceStore));
        Assert.False(File.Exists(paths.KnownHosts));
    }

    [Fact]
    public void WithoutAnSshKeyItStops()
    {
        File.Delete(paths.PublicKey);
        string? asked = null;
        host.CreateKey = file =>
        {
            asked = file;
            return false;
        };

        Assert.False(Run(null, card));

        Assert.Equal(paths.PublicKey, asked);
        Assert.False(card.HasPendingSettings);
    }

    [Fact]
    public void ItSaysWhatsOnTheCard()
    {
        File.WriteAllText(Path.Combine(card.Root, "juggle-release.txt"), "VERSION=v0.7.0\nBUILD_STAMP=20261030-1200\n");
        File.WriteAllText(Path.Combine(card.Root, "juggle-error.txt"), "bad time zone\n");

        Assert.True(Run(null, card));

        Assert.Contains("Image: Juggle OS v0.7.0, build 20261030-1200 UTC", host.Said);
        Assert.Contains("The Pi left a message on this card about the last settings: bad time zone", host.Said);
    }

    [Fact]
    public void WithoutACardItStops()
    {
        Assert.False(Run(null));
        Assert.StartsWith("No Juggle card found.", Assert.Single(host.Problems));
    }

    [Fact]
    public void WithSeveralCardsItAsksWhich()
    {
        var other = new BootPartition(Directory.CreateDirectory(Path.Combine(dir, "other")).FullName, "other card");

        Assert.True(Run(null, card, other));

        Assert.True(other.HasPendingSettings);
        Assert.False(card.HasPendingSettings);
    }
}
