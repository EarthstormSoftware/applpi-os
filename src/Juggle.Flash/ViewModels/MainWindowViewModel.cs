using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Juggle.Flash.ViewModels;

/// <summary>A page of the window: the start page, a screen of a job, or the job's progress.</summary>
public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }

    /// <summary>Back to the start page (not while a job runs), for the page's Back button.</summary>
    public IRelayCommand? HomeCommand { get; internal set; }
}

/// <summary>The window: the page it shows, and the job running.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoHomeCommand))]
    PageViewModel page;

    /// <summary>Whether a job is running: the window stays with it until it ends.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoHomeCommand))]
    bool busy;

    public MainWindowViewModel()
    {
        page = new StartViewModel(this);
        page.HomeCommand = GoHomeCommand;
    }

    /// <summary>This app's version, from the release tag it was built from.</summary>
    public string Version { get; } = typeof(MainWindowViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    bool CanGoHome => Page is not StartViewModel && !Busy;

    [RelayCommand(CanExecute = nameof(CanGoHome))]
    void GoHome() => Show(new StartViewModel(this));

    internal void Show(PageViewModel next)
    {
        next.HomeCommand = GoHomeCommand;
        Page = next;
    }

    /// <summary>
    /// Runs a job on a thread of its own, showing its progress, and the page for each question it asks. work returns
    /// whether it did what it set out to.
    /// </summary>
    internal async Task RunJob(string title, Func<WindowJobHost, bool> work)
    {
        var job = new JobViewModel(title);
        var host = new WindowJobHost(this, job);
        Show(job);
        Busy = true;
        try
        {
            await job.RunAsync(() => work(host));
        }
        finally
        {
            Busy = false;
        }
    }
}
