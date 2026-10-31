using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Juggle.Core.Jobs;

namespace Juggle.Flash.ViewModels;

/// <summary>A line a job reported: what happened, or a problem.</summary>
public sealed record JobLine(string Text, bool IsProblem);

/// <summary>
/// A job running: what it reports, as lines and a progress bar. Jobs run on a thread of their own, so what they report
/// is passed to the window's thread (dispatch; tests pass it straight on).
/// </summary>
public sealed partial class JobViewModel(string title, Action<Action>? dispatch = null) : PageViewModel, IJobReport
{
    readonly Action<Action> dispatch = dispatch ?? (action => Dispatcher.UIThread.Post(action));

    public override string Title => title;

    public ObservableCollection<JobLine> Lines { get; } = [];

    /// <summary>The progress shown, or null when there's none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsProgress))]
    string? progressText;

    /// <summary>How far, from 0 to 100, when that's known.</summary>
    [ObservableProperty]
    double progressValue;

    [ObservableProperty]
    bool progressKnown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    bool isRunning;

    /// <summary>Whether the job did what it set out to, once it has ended.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    bool? succeeded;

    public bool ShowsProgress => ProgressText is not null;

    /// <summary>Where the job is: working, done, or stopped (having said why).</summary>
    public string Status => IsRunning ? "Working…" : Succeeded switch
    {
        true => "Done.",
        false => "Stopped.",
        null => "",
    };

    /// <summary>Runs a job (on a thread of its own) until it ends; it returns whether it did what it set out to.</summary>
    public async Task RunAsync(Func<bool> job)
    {
        IsRunning = true;
        Succeeded = null;
        try
        {
            Succeeded = await Task.Run(job);
        }
        catch (OperationCanceledException)
        {
            // Cancelled at a question, before anything was written
            Say("Stopped: nothing was changed.");
            Succeeded = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Problem(error.Message);
            Progress(null);
            Succeeded = false;
        }
        finally
        {
            IsRunning = false;
        }
    }

    public void Say(string text)
    {
        // Empty lines only space out a terminal's output
        if (text.Length > 0)
        {
            dispatch(() => Lines.Add(new JobLine(text, IsProblem: false)));
        }
    }

    public void Problem(string text) => dispatch(() => Lines.Add(new JobLine(text, IsProblem: true)));

    public void Progress(JobProgress? progress) => dispatch(() =>
    {
        ProgressText = progress is { } now ? Describe(now) : null;
        ProgressKnown = progress?.Total is > 0;
        ProgressValue = (progress?.Fraction ?? 0) * 100;
    });

    /// <summary>A stage's progress in words, such as "Writing: 360 of 795 MiB".</summary>
    internal static string Describe(JobProgress progress) => progress.Stage switch
    {
        JobStage.Download => $"Downloading {progress.Item}: {Size(progress)}",
        JobStage.Write => $"Writing the card: {Size(progress)}",
        JobStage.Check => $"Checking the card: {Size(progress)}",
        JobStage.Send => $"Sending the image to the Pi: {Size(progress)}",
        JobStage.WaitForRestart => "Waiting for the Pi to restart",
        _ => progress.Stage.ToString(),
    };

    static string Size(JobProgress progress) => progress.Total is { } total
        ? $"{progress.Done >> 20} of {total >> 20} MiB"
        : $"{progress.Done >> 20} MiB";
}
