using Juggle.Core;
using Juggle.Core.Jobs;
using Juggle.Flash.ViewModels;

namespace Juggle.Flash.Tests;

public sealed class ScreenTests : IDisposable
{
    const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleExampleExampleExampleExampleExample example";

    static readonly RemovableDisk Disk = new("2", "Card Reader", 16_000_000_000, [], []);

    // kitchen was set up before, with Wi-Fi; any other name is new
    static readonly SettingsForm Form = new("kitchen", name => name == "kitchen"
        ? new SettingsDefaults("Europe/Berlin", new WifiSettings("DE", "Home", "secret1234"), "DE")
        : new SettingsDefaults("Europe/London", null, "GB"));

    readonly string dir = Directory.CreateTempSubdirectory("juggle-test").FullName;

    public void Dispose() => Directory.Delete(dir, recursive: true);

    /// <summary>The page the window shows once it's a T, as a job's question shows it from the job's thread.</summary>
    static async Task<T> PageOf<T>(MainWindowViewModel window) where T : PageViewModel
    {
        for (var wait = 0; wait < 200; wait++)
        {
            if (window.Page is T page)
            {
                return page;
            }
            await Task.Delay(25, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException($"The window didn't show a {typeof(T).Name}.");
    }

    [Fact]
    public async Task SettingsStartFromThePisOwn()
    {
        var settings = new SettingsViewModel(Form);

        Assert.Equal("kitchen", settings.Hostname);
        Assert.Equal("Europe/Berlin", settings.TimeZone);
        Assert.True(settings.UseWifi);
        Assert.Equal("Home", settings.Ssid);
        Assert.Equal("", settings.Password);
        Assert.True(settings.KeepsPassword);
        Assert.Equal("ssh juggle@kitchen.local", settings.Address);

        settings.ContinueCommand.Execute(null);

        Assert.Equal(new SettingsAnswers("kitchen", "Europe/Berlin", new WifiSettings("DE", "Home", "secret1234")),
            await settings.Answer);
    }

    [Fact]
    public async Task ANewNameStartsFromItsDefaults()
    {
        var settings = new SettingsViewModel(Form) { Hostname = "Garage" };

        Assert.Equal("Europe/London", settings.TimeZone);
        Assert.False(settings.UseWifi);
        settings.ContinueCommand.Execute(null);
        Assert.Equal(new SettingsAnswers("garage", "Europe/London", null), await settings.Answer);
    }

    [Fact]
    public void SettingsThatWontWorkCantBeGiven()
    {
        var settings = new SettingsViewModel(Form) { Hostname = "Bad_Name" };
        Assert.NotNull(settings.HostnameError);
        Assert.False(settings.ContinueCommand.CanExecute(null));

        settings.Hostname = "kitchen";
        settings.Ssid = "Other";
        // Another network has no saved password to keep
        Assert.False(settings.KeepsPassword);
        Assert.False(settings.ContinueCommand.CanExecute(null));
        settings.Password = "short";
        Assert.NotNull(settings.PasswordError);
        settings.Password = "long enough";
        Assert.True(settings.ContinueCommand.CanExecute(null));

        settings.TimeZone = "Nowhere/Here";
        Assert.NotNull(settings.TimeZoneError);
        Assert.False(settings.ContinueCommand.CanExecute(null));
    }

    [Fact]
    public void TimeZonesAreIanaNames()
    {
        Assert.Contains("Europe/London", SettingsViewModel.TimeZones);
        Assert.All(SettingsViewModel.TimeZones, zone => Assert.Null(SettingsRules.CheckTimeZone(zone)));
    }

    [Fact]
    public async Task AQuestionShowsItsPageThenTheJobAgain()
    {
        var window = new MainWindowViewModel();
        var job = new JobViewModel("Flash a card", action => action());
        var host = new WindowJobHost(window, job, action => action());

        var asking = Task.Run(() => host.ConfirmErase(Disk), TestContext.Current.CancellationToken);
        var page = await PageOf<ConfirmEraseViewModel>(window);
        Assert.Contains("Card Reader", page.Card);
        page.EraseCommand.Execute(null);

        Assert.True(await asking);
        Assert.Same(job, window.Page);
    }

    [Fact]
    public async Task CancellingAQuestionStopsTheJob()
    {
        var window = new MainWindowViewModel();
        var job = new JobViewModel("Flash a card", action => action());
        var host = new WindowJobHost(window, job, action => action());

        var running = job.RunAsync(() => host.ChooseDisk([Disk]) is not null);
        var page = await PageOf<ChooseDiskViewModel>(window);
        // One card is chosen already
        Assert.Equal(Disk, page.Selected);
        page.CancelCommand.Execute(null);
        await running;

        Assert.False(job.Succeeded);
        Assert.Equal("Stopped.", job.Status);
        Assert.Contains(new JobLine("Stopped: nothing was changed.", false), job.Lines);
    }

    [Fact]
    public async Task CancellingTheEraseIsAnAnswer()
    {
        var page = new ConfirmEraseViewModel(Disk);
        page.CancelCommand.Execute(null);
        Assert.False(await page.Answer);
    }

    [Theory]
    [InlineData("v0.7", false)]
    [InlineData("v0.7.0", true)]
    [InlineData("latest", true)]
    public void AReleaseMustBeAVersion(string release, bool valid)
    {
        var flash = new FlashViewModel(new MainWindowViewModel()) { IsRelease = true, Release = release };

        Assert.Equal(valid, flash.ImageError is null);
        Assert.Equal(valid, flash.StartCommand.CanExecute(null));
        Assert.Equal(valid ? release : null, flash.ImageArgument);
    }

    [Fact]
    public void AFileMustBeThere()
    {
        var flash = new FlashViewModel(new MainWindowViewModel()) { IsFile = true, File = Path.Combine(dir, "none.img.xz") };
        Assert.NotNull(flash.ImageError);
        Assert.False(flash.StartCommand.CanExecute(null));

        flash.File = Path.Combine(dir, "juggle-os-v0.8.0.img.xz");
        File.WriteAllText(flash.File, "");
        flash.File = flash.File;
        Assert.Equal(flash.File, flash.ImageArgument);
    }

    [Fact]
    public void FlashingTakesTheLatestReleaseAndEjectsByDefault()
    {
        var flash = new FlashViewModel(new MainWindowViewModel());
        Assert.Equal("latest", flash.ImageArgument);
        Assert.True(flash.Eject);
    }

    [Fact]
    public void ListsThePisSetUpHere()
    {
        var path = Path.Combine(dir, "devices.json");
        var store = new DeviceStore();
        store.Set(new DeviceSettings("porch", null, null, [Key]), "id.pub");
        store.Set(new DeviceSettings("attic", null, null, [Key]), "id.pub");
        store.LastDevice = "porch";
        store.Save(path);

        var pis = new PisViewModel(new MainWindowViewModel(), path);

        Assert.Equal(["attic", "porch"], pis.Pis.Select(pi => pi.Name));
        Assert.Equal([false, true], pis.Pis.Select(pi => pi.IsDefault));
        Assert.Equal("porch.local", pis.Pis[1].Address);

        pis.Pis[0].ForgetCommand.Execute(null);
        Assert.True(pis.Pis[0].ConfirmingForget);
        pis.Pis[0].KeepCommand.Execute(null);
        Assert.False(pis.Pis[0].ConfirmingForget);
    }

    [Fact]
    public void UsingAPiAsTheDefaultIsRemembered()
    {
        var path = Path.Combine(dir, "devices.json");
        var store = new DeviceStore();
        store.Set(new DeviceSettings("attic", null, null, [Key]), "id.pub");
        store.Set(new DeviceSettings("porch", null, null, [Key]), "id.pub");
        store.Save(path);
        var pis = new PisViewModel(new MainWindowViewModel(), path);

        pis.Pis[0].UseAsDefaultCommand.Execute(null);

        Assert.Equal("attic", DeviceStore.Load(path).DefaultDevice);
        Assert.Equal([true, false], pis.Pis.Select(pi => pi.IsDefault));
    }

    [Fact]
    public async Task SettingUpListsTheCardsFound()
    {
        var found = new List<CardChoice>();
        var setUp = new SetUpViewModel(new MainWindowViewModel(), () => found);
        await setUp.RefreshCommand.ExecuteAsync(null);
        Assert.True(setUp.NoCards);
        Assert.False(setUp.StartCommand.CanExecute(null));

        found.Add(new CardChoice("F: (boot partition, 384 MB)", new BootPartition(dir, "F: (boot partition, 384 MB)"), null));
        await setUp.RefreshCommand.ExecuteAsync(null);

        Assert.False(setUp.NoCards);
        // The only card is chosen already
        Assert.Equal(found[0], setUp.Selected);
        Assert.True(setUp.StartCommand.CanExecute(null));
    }

    [Fact]
    public void UpdatingAPiFirstSaysToHaveItOn()
    {
        var path = Path.Combine(dir, "devices.json");
        var store = new DeviceStore();
        store.Set(new DeviceSettings("porch", null, null, [Key]), "id.pub");
        store.Save(path);
        var window = new MainWindowViewModel();

        new PisViewModel(window, path).Pis[0].UpdateCommand.Execute(null);

        var update = Assert.IsType<UpdateViewModel>(window.Page);
        Assert.Equal("Update porch", update.Title);
        Assert.Contains("porch is turned on and connected to the network", update.Reminder);
        Assert.Contains("porch.local", update.Reminder);
    }

    [Fact]
    public void NoPisYet() => Assert.True(new PisViewModel(new MainWindowViewModel(), Path.Combine(dir, "none.json")).IsEmpty);
}
