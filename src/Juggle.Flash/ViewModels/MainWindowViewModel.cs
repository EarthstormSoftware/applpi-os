using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Juggle.Flash.ViewModels;

/// <summary>A page of the window: the start page, a screen of a job, or the job's progress.</summary>
public abstract class PageViewModel : ObservableObject
{
    public abstract string Title { get; }
}

/// <summary>The window: the page it shows, and the way back to the start.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoHome))]
    [NotifyCanExecuteChangedFor(nameof(GoHomeCommand))]
    PageViewModel page;

    public MainWindowViewModel() => page = new StartViewModel(this);

    /// <summary>This app's version, from the release tag it was built from.</summary>
    public string Version { get; } = typeof(MainWindowViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public bool CanGoHome => Page is not StartViewModel;

    [RelayCommand(CanExecute = nameof(CanGoHome))]
    void GoHome() => Page = new StartViewModel(this);

    internal void Show(PageViewModel next) => Page = next;
}
