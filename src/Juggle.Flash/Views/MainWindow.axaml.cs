using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Styling;

namespace Juggle.Flash.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ShowIcon();
        ActualThemeVariantChanged += (_, _) => ShowIcon();
    }

    // The icon in the title bar and taskbar: its line dark on a light theme, light on a dark one (as the logo)
    void ShowIcon()
    {
        var file = ActualThemeVariant == ThemeVariant.Dark ? "juggle-flash-dark.png" : "juggle-flash.png";
        using var icon = AssetLoader.Open(new Uri($"avares://juggle-flash/Assets/{file}"));
        Icon = new WindowIcon(icon);
    }
}
