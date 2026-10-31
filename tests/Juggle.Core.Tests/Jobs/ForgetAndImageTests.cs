using Juggle.Core;
using Juggle.Core.Jobs;

namespace Juggle.Core.Tests.Jobs;

public sealed class ForgetAndImageTests : IDisposable
{
    const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleExampleExampleExampleExampleExample example";

    readonly string dir = Directory.CreateTempSubdirectory("juggle-test").FullName;
    readonly JugglePaths paths;
    readonly FakeHost host = new();

    public ForgetAndImageTests() =>
        paths = new JugglePaths(Path.Combine(dir, "devices.json"), Path.Combine(dir, "known_hosts"), Path.Combine(dir, "id.pub"));

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void ForgetsAKnownPi()
    {
        var store = new DeviceStore();
        store.Set(new DeviceSettings("old-pi", null, null, [Key]), paths.PublicKey, "ssh-ed25519 AAAAold");
        store.Set(new DeviceSettings("other-pi", null, null, [Key]), paths.PublicKey, "ssh-ed25519 AAAAother");
        store.LastDevice = "old-pi";
        store.Save(paths.DeviceStore);
        File.WriteAllLines(paths.KnownHosts, ["old-pi.local ssh-ed25519 AAAAold", "other-pi.local ssh-ed25519 AAAAother"]);

        Assert.True(new ForgetJob(host, paths).Run("Old-Pi"));

        Assert.Null(DeviceStore.Load(paths.DeviceStore).Get("old-pi"));
        Assert.Equal(["other-pi.local ssh-ed25519 AAAAother"], File.ReadAllLines(paths.KnownHosts));
        Assert.Equal([
            "Forgot old-pi: its settings and host key, and old-pi.local in ssh's known_hosts.",
            "New Pis now start from other-pi's settings.",
        ], host.Said);
    }

    [Fact]
    public void ChoosesTheDefaultPi()
    {
        var store = new DeviceStore();
        store.Set(new DeviceSettings("attic", null, null, [Key]), paths.PublicKey);
        store.Set(new DeviceSettings("porch", null, null, [Key]), paths.PublicKey);
        store.Save(paths.DeviceStore);

        Assert.True(new DefaultJob(host, paths).Run(null));
        Assert.True(new DefaultJob(host, paths).Run("Attic"));
        Assert.False(new DefaultJob(host, paths).Run("nobody"));

        Assert.Equal([
            "New Pis start from porch's settings: it was set up last. juggle default <name> chooses one.",
            "New Pis start from attic's settings: it's the default.",
        ], host.Said);
        Assert.Equal("nobody isn't known on this PC: set it up first (juggle flash or juggle setup).", Assert.Single(host.Problems));
        Assert.Equal("attic", DeviceStore.Load(paths.DeviceStore).DefaultDevice);
    }

    [Fact]
    public void ForgettingTheDefaultPiGoesBackToTheLastOne()
    {
        var store = new DeviceStore();
        store.Set(new DeviceSettings("attic", null, null, [Key]), paths.PublicKey);
        store.Set(new DeviceSettings("porch", null, null, [Key]), paths.PublicKey);
        store.DefaultDevice = "attic";
        store.Save(paths.DeviceStore);

        Assert.True(new ForgetJob(host, paths).Run("attic"));

        var after = DeviceStore.Load(paths.DeviceStore);
        Assert.Null(after.DefaultDevice);
        Assert.Equal("porch", after.StartingDevice);
        Assert.Equal("New Pis now start from porch's settings.", host.Said[^1]);
    }

    [Fact]
    public void HasNothingToForgetForAnUnknownPi()
    {
        Assert.False(new ForgetJob(host, paths).Run("nobody"));
        Assert.Equal("nobody isn't known on this PC: nothing to forget.", Assert.Single(host.Problems));
    }

    [Fact]
    public void FindsAnImageFile()
    {
        var image = Path.Combine(dir, "mine.img.xz");
        File.WriteAllText(image, "");

        Assert.Equal(image, new ImageSource(host, Path.Combine(dir, "deploy")).Find(image));
    }

    [Fact]
    public void SaysWhenTheresNoSuchImage()
    {
        var missing = Path.Combine(dir, "missing.img.xz");

        Assert.Null(new ImageSource(host, Path.Combine(dir, "deploy")).Find(missing));
        Assert.Equal($"No such image: {missing}", Assert.Single(host.Problems));
    }

    [Fact]
    public void TakesTheNewestBuildInDeploy()
    {
        var deploy = Directory.CreateDirectory(Path.Combine(dir, "deploy")).FullName;
        var older = Path.Combine(deploy, "juggle-os-v0.8.0.img.xz");
        var newer = Path.Combine(deploy, "juggle-os-v0.8.0-2-gabcdef0.img.xz");
        File.WriteAllText(older, "");
        File.WriteAllText(newer, "");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddHours(-1));
        File.WriteAllText(Path.Combine(deploy, "other.img.xz"), "");

        Assert.Equal(newer, new ImageSource(host, deploy).Find(null));
    }

    [Fact]
    public void DownloadsARelease()
    {
        var downloaded = Path.Combine(dir, "juggle-os-v0.7.0.img.xz");
        var source = new ImageSource(host, Path.Combine(dir, "deploy"), (release, progress) =>
        {
            Assert.Equal("v0.7.0", release);
            progress("juggle-os-v0.7.0.img.xz", 1 << 20, 2 << 20);
            return downloaded;
        });

        Assert.Equal(downloaded, source.Find("v0.7.0"));

        Assert.Equal([new JobProgress(JobStage.Download, 1 << 20, 2 << 20, "juggle-os-v0.7.0.img.xz"), null], host.Progresses);
        Assert.Equal("  juggle-os-v0.7.0.img.xz, checked against the release's SHA256SUMS", host.Said[^1]);
    }

    [Fact]
    public void SaysWhenADownloadFails()
    {
        var source = new ImageSource(host, Path.Combine(dir, "deploy"),
            (_, _) => throw new HttpRequestException("No such host is known."));

        Assert.Null(source.Find(null));

        Assert.Equal("Couldn't download it: No such host is known.", Assert.Single(host.Problems));
    }
}
