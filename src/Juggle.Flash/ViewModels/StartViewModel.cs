using CommunityToolkit.Mvvm.Input;

namespace Juggle.Flash.ViewModels;

/// <summary>The first page: flash a card, set up a card that's written already, or the Pis this computer knows.</summary>
public sealed partial class StartViewModel(MainWindowViewModel window) : PageViewModel
{
    public override string Title => "What would you like to do?";

    [RelayCommand]
    void Flash() => window.Show(new FlashViewModel(window));

    [RelayCommand]
    void SetUp() => window.Show(new SetUpViewModel(window));

    [RelayCommand]
    void Pis() => window.Show(new PisViewModel(window));
}
