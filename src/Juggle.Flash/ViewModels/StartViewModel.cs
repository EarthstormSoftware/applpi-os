using CommunityToolkit.Mvvm.Input;

namespace Juggle.Flash.ViewModels;

/// <summary>The first page: flash a card, set up a card that's written already, or the Pis this computer knows.</summary>
public sealed partial class StartViewModel(MainWindowViewModel window) : PageViewModel
{
    public override string Title => "What would you like to do?";

    [RelayCommand]
    void Flash() => window.Show(new PlaceholderViewModel("Flash a card",
        "Choose the image, the card and the Pi's settings, then write the card. Coming in the next step."));

    [RelayCommand]
    void SetUp() => window.Show(new PlaceholderViewModel("Set up a card",
        "Put a Pi's settings on a card that already has Juggle OS. Coming in the next step."));

    [RelayCommand]
    void Pis() => window.Show(new PlaceholderViewModel("My Pis",
        "The Pis set up on this computer, each to update or forget. Coming in the next step."));
}

/// <summary>A page still to come.</summary>
public sealed class PlaceholderViewModel(string title, string text) : PageViewModel
{
    public override string Title => title;

    public string Text => text;
}
