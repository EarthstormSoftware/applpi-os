using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Juggle.Flash.ViewModels;

namespace Juggle.Flash.Views;

public partial class FlashView : UserControl
{
    public FlashView() => InitializeComponent();

    async void Browse(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is not { } top || DataContext is not FlashViewModel model)
        {
            return;
        }
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "A Juggle OS image",
            FileTypeFilter = [new FilePickerFileType("Juggle OS images") { Patterns = ["*.img.xz"] }],
        });
        if (files.FirstOrDefault()?.TryGetLocalPath() is { } path)
        {
            model.File = path;
        }
    }
}
