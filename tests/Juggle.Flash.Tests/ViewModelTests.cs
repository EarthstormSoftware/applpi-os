using Juggle.Core.Jobs;
using Juggle.Flash.ViewModels;

namespace Juggle.Flash.Tests;

public class ViewModelTests
{
    [Fact]
    public void StartsOnTheStartPageAndGoesBackToIt()
    {
        var window = new MainWindowViewModel();
        Assert.IsType<StartViewModel>(window.Page);
        Assert.False(window.GoHomeCommand.CanExecute(null));

        ((StartViewModel)window.Page).FlashCommand.Execute(null);

        Assert.Equal("Flash a card", window.Page.Title);
        Assert.True(window.CanGoHome);
        window.GoHomeCommand.Execute(null);
        Assert.IsType<StartViewModel>(window.Page);
    }

    [Fact]
    public void EachChoiceOpensItsPage()
    {
        var window = new MainWindowViewModel();
        var start = (StartViewModel)window.Page;

        start.SetUpCommand.Execute(null);
        Assert.Equal("Set up a card", window.Page.Title);
        start.PisCommand.Execute(null);
        Assert.Equal("My Pis", window.Page.Title);
    }

    [Fact]
    public void ShowsTheVersion() => Assert.StartsWith("v", new MainWindowViewModel().Version);

    [Fact]
    public async Task ShowsWhatAJobReports()
    {
        var job = new JobViewModel("Flash a card", action => action());

        await job.RunAsync(() =>
        {
            job.Say("Image: juggle-os-v0.8.0.img.xz");
            job.Say("");
            job.Progress(new JobProgress(JobStage.Write, 360L << 20, 795L << 20));
            return true;
        });

        Assert.Equal([new JobLine("Image: juggle-os-v0.8.0.img.xz", false)], job.Lines);
        Assert.Equal("Writing the card: 360 of 795 MiB", job.ProgressText);
        Assert.True(job.ProgressKnown);
        Assert.Equal(360.0 / 795 * 100, job.ProgressValue, precision: 6);
        Assert.True(job.Succeeded);
        Assert.False(job.IsRunning);
    }

    [Fact]
    public async Task ShowsWhyAJobStopped()
    {
        var job = new JobViewModel("Update", action => action());

        await job.RunAsync(() =>
        {
            job.Progress(new JobProgress(JobStage.WaitForRestart, 3));
            throw new IOException("ssh: Could not resolve hostname");
        });

        Assert.Equal([new JobLine("ssh: Could not resolve hostname", true)], job.Lines);
        Assert.False(job.ShowsProgress);
        Assert.False(job.Succeeded);
    }

    [Fact]
    public void ProgressWithoutATotalIsShownAsGoing()
    {
        var job = new JobViewModel("Download", action => action());

        job.Progress(new JobProgress(JobStage.Download, 5L << 20, null, "juggle-os-v0.8.0.img.xz"));

        Assert.Equal("Downloading juggle-os-v0.8.0.img.xz: 5 MiB", job.ProgressText);
        Assert.False(job.ProgressKnown);
    }
}
