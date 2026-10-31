using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Juggle.Flash.ViewModels;
using Juggle.Flash.Views;

namespace Juggle.Flash.Tests;

/// <summary>The window itself, drawn without a screen.</summary>
public class WindowTests
{
    static MainWindow Show(MainWindowViewModel model)
    {
        var window = new MainWindow { DataContext = model };
        window.Show();
        return window;
    }

    [AvaloniaFact]
    public void ShowsTheStartPageWithItsChoices()
    {
        var window = Show(new MainWindowViewModel());

        var start = window.GetVisualDescendants().OfType<StartView>().Single();
        Assert.NotNull(start.FindControl<Button>("FlashButton"));
        Assert.NotNull(start.FindControl<Button>("SetUpButton"));
        Assert.NotNull(start.FindControl<Button>("PisButton"));
    }

    [AvaloniaFact]
    public void ClickingAChoiceOpensItsPage()
    {
        var model = new MainWindowViewModel();
        var window = Show(model);
        var flash = window.GetVisualDescendants().OfType<StartView>().Single().FindControl<Button>("FlashButton")!;

        var centre = flash.TranslatePoint(new(flash.Bounds.Width / 2, flash.Bounds.Height / 2), window)!.Value;
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal("Flash a card", model.Page.Title);
        Assert.Single(window.GetVisualDescendants().OfType<FlashView>());
    }

    [AvaloniaFact]
    public void ProblemsAreShownInTheirOwnColour()
    {
        var job = new JobViewModel("Update kitchen", action => action());
        var model = new MainWindowViewModel();
        model.Show(job);
        var window = Show(model);

        job.Say("Connecting to kitchen.local");
        job.Problem("Couldn't connect to kitchen (kitchen.local).");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var lines = window.GetVisualDescendants().OfType<SelectableTextBlock>().ToDictionary(line => line.Text ?? "");
        var problem = lines["Couldn't connect to kitchen (kitchen.local)."].Foreground;
        // A colour the theme has (a missing one leaves the text without any: invisible)
        Assert.NotNull(problem);
        Assert.NotEqual(lines["Connecting to kitchen.local"].Foreground, problem);
    }

    [AvaloniaFact]
    public void ShowsAJobsLinesAndProgress()
    {
        var job = new JobViewModel("Flash a card", action => action());
        var model = new MainWindowViewModel();
        model.Show(job);
        var window = Show(model);

        job.Say("Written and checked.");
        job.Progress(new Core.Jobs.JobProgress(Core.Jobs.JobStage.Check, 1 << 20, 2 << 20));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var view = window.GetVisualDescendants().OfType<JobView>().Single();
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        Assert.Contains("Written and checked.", texts);
        Assert.Contains("Checking the card: 1 of 2 MiB", texts);
        Assert.Equal(50, view.GetVisualDescendants().OfType<ProgressBar>().Single().Value);
    }
}
