using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Juggle.Core;
using Juggle.Core.Jobs;

namespace Juggle.Flash.ViewModels;

/// <summary>
/// What writes a card: this app, if it has administrator rights already, or else juggle beside it, started with them
/// (UAC on Windows, pkexec on Linux).
/// </summary>
static class CardJobs
{
    public static ICardJob For(IJobReport report)
    {
        if (Platform.IsAdministrator)
        {
            return new CardJob(report);
        }
        var juggle = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "juggle.exe" : "juggle");
        return new ElevatedCardJob(report, juggle, [], linuxElevator: "pkexec");
    }
}

/// <summary>Where the image comes from.</summary>
public enum ImageChoice
{
    Latest,
    Release,
    File,
}

/// <summary>Flash a card: the image, and whether to eject the card after. The card and settings are asked by the job.</summary>
public sealed partial class FlashViewModel(MainWindowViewModel window) : PageViewModel
{
    public override string Title => "Flash a card";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLatest), nameof(IsRelease), nameof(IsFile), nameof(ImageError))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    ImageChoice image = ImageChoice.Latest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageError))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    string release = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImageError))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    string file = "";

    [ObservableProperty]
    bool eject = true;

    // Radio buttons, one per choice
    public bool IsLatest { get => Image == ImageChoice.Latest; set { if (value) Image = ImageChoice.Latest; } }
    public bool IsRelease { get => Image == ImageChoice.Release; set { if (value) Image = ImageChoice.Release; } }
    public bool IsFile { get => Image == ImageChoice.File; set { if (value) Image = ImageChoice.File; } }

    public string? ImageError => Image switch
    {
        ImageChoice.Release when Release.Trim().Length > 0 && !ReleaseImages.IsRelease(Release.Trim()) =>
            "A release is a version such as v0.7.0.",
        ImageChoice.File when File.Length > 0 && !System.IO.File.Exists(File) => "There's no such file.",
        _ => null,
    };

    /// <summary>The image as juggle takes it: latest, a release, or a file.</summary>
    internal string? ImageArgument => Image switch
    {
        ImageChoice.Latest => "latest",
        ImageChoice.Release when ReleaseImages.IsRelease(Release.Trim()) => Release.Trim(),
        ImageChoice.File when System.IO.File.Exists(File) => File,
        _ => null,
    };

    bool CanStart => ImageArgument is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    Task Start()
    {
        var image = ImageArgument!;
        var eject = Eject;
        return window.RunJob(Title, host =>
        {
            var found = new ImageSource(host, deployDirectory: "").Find(image);
            return found is not null && new FlashJob(host, new SetupJob(host), CardJobs.For(host)).Run(found, eject);
        });
    }
}


/// <summary>A Juggle card found: its boot partition this user can write, or (Linux) a card only root can mount.</summary>
public sealed record CardChoice(string Description, BootPartition? Card, RemovableDisk? Disk);

/// <summary>
/// Set up a card that already has Juggle OS: the Juggle cards in this computer's card readers, which one, and whether
/// to eject it after. The Pi's settings are asked by the job.
/// </summary>
public sealed partial class SetUpViewModel : PageViewModel
{
    readonly MainWindowViewModel window;
    readonly Func<IReadOnlyList<CardChoice>> findCards;

    public SetUpViewModel(MainWindowViewModel window, Func<IReadOnlyList<CardChoice>>? findCards = null)
    {
        this.window = window;
        this.findCards = findCards ?? FindCards;
        _ = Refresh();
    }

    public override string Title => "Set up a card";

    public ObservableCollection<CardChoice> Cards { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    CardChoice? selected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoCards))]
    bool looking;

    [ObservableProperty]
    bool eject = true;

    public bool NoCards => !Looking && Cards.Count == 0;

    /// <summary>Looks for the cards again (off the window's thread: it asks the system).</summary>
    [RelayCommand]
    async Task Refresh()
    {
        Looking = true;
        foreach (var old in Cards)
        {
            old.Card?.Dispose();
        }
        Cards.Clear();
        foreach (var card in await Task.Run(findCards))
        {
            Cards.Add(card);
        }
        Selected = Cards.Count == 1 ? Cards[0] : null;
        Looking = false;
    }

    static IReadOnlyList<CardChoice> FindCards()
    {
        var cards = BootPartition.FindCards();
        var unmounted = cards.Count == 0 && !Platform.IsAdministrator ? BootPartition.UnmountedCards() : [];
        return [.. cards.Select(card => new CardChoice(card.Description, card, null)),
            .. unmounted.Select(disk => new CardChoice(disk.Description, null, disk))];
    }

    bool CanStart => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    Task Start()
    {
        var chosen = Selected!;
        var eject = Eject;
        return window.RunJob(Title, host => chosen.Card is { } card
            ? new SetupJob(host).Run(null, [card], [], CardJobs.For(host), eject)
            : new SetupJob(host).Run(null, [], [chosen.Disk!], CardJobs.For(host), eject));
    }
}

/// <summary>
/// The Pis set up on this computer, each to update (to the latest release), forget, or use as the default: the Pi a
/// new one starts from.
/// </summary>
public sealed partial class PisViewModel : PageViewModel
{
    readonly MainWindowViewModel window;
    readonly string path;

    public PisViewModel(MainWindowViewModel window, string? deviceStore = null)
    {
        this.window = window;
        path = deviceStore ?? JugglePaths.Default.DeviceStore;
        Load();
    }

    public override string Title => "My Pis";

    public ObservableCollection<PiItem> Pis { get; } = [];

    public bool IsEmpty => Pis.Count == 0;

    void Load()
    {
        var store = DeviceStore.Load(path);
        Pis.Clear();
        foreach (var name in store.Devices.Keys.Order(StringComparer.Ordinal))
        {
            Pis.Add(new PiItem(window, this, name, name == store.StartingDevice));
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    internal void UseAsDefault(string name)
    {
        var store = DeviceStore.Load(path);
        store.DefaultDevice = name;
        store.Save(path);
        Load();
    }
}

/// <summary>A Pi in the list. Forget asks again before it forgets.</summary>
public sealed partial class PiItem(MainWindowViewModel window, PisViewModel list, string name, bool isDefault) : ObservableObject
{
    public string Name => name;

    public string Address => PiShell.HostFor(name);

    /// <summary>The Pi a new one starts from: the one chosen, or else the last one set up.</summary>
    public bool IsDefault => isDefault;

    [ObservableProperty]
    bool confirmingForget;

    [RelayCommand]
    void Update() => window.Show(new UpdateViewModel(window, name));

    [RelayCommand]
    void UseAsDefault() => list.UseAsDefault(name);

    [RelayCommand]
    void Forget() => ConfirmingForget = true;

    [RelayCommand]
    void Keep() => ConfirmingForget = false;

    [RelayCommand]
    Task ForgetNow() => window.RunJob($"Forget {name}", host => new ForgetJob(host).Run(name));
}

/// <summary>Before updating a Pi: a reminder that it has to be on and reachable, since the update goes over SSH.</summary>
public sealed partial class UpdateViewModel(MainWindowViewModel window, string name) : PageViewModel
{
    public override string Title => $"Update {name}";

    public string Reminder => $"Make sure {name} is turned on and connected to the network: the update goes to it over " +
        $"SSH, at {PiShell.HostFor(name)}.";

    [RelayCommand]
    Task Start() => window.RunJob(Title,
        host => new UpdateJob(host, new ImageSource(host, deployDirectory: "")).Run(name, null, yes: false));
}
