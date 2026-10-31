using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Threading;
using Juggle.Flash.ViewModels;

namespace Juggle.Flash.Views;

public partial class JobView : UserControl
{
    JobViewModel? shown;

    public JobView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (shown is not null)
            {
                shown.Lines.CollectionChanged -= FollowLog;
            }
            shown = DataContext as JobViewModel;
            if (shown is not null)
            {
                shown.Lines.CollectionChanged += FollowLog;
            }
        };
    }

    // The newest line stays in view, once it has been laid out
    void FollowLog(object? sender, NotifyCollectionChangedEventArgs e) =>
        Dispatcher.UIThread.Post(() => Log.ScrollToEnd(), DispatcherPriority.Background);
}
