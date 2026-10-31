using Avalonia.Threading;
using Juggle.Core;
using Juggle.Core.Jobs;

namespace Juggle.Flash.ViewModels;

/// <summary>
/// A job's host in the window. What the job reports goes to its progress page; each question shows the page for it
/// until it's answered (the job's thread waits meanwhile), then the progress page again.
/// </summary>
public sealed class WindowJobHost(MainWindowViewModel window, JobViewModel job, Action<Action>? dispatch = null) : IJobHost
{
    readonly Action<Action> dispatch = dispatch ?? (action => Dispatcher.UIThread.Post(action));

    public void Say(string text) => job.Say(text);

    public void Problem(string text) => job.Problem(text);

    public void Progress(JobProgress? progress) => job.Progress(progress);

    public RemovableDisk ChooseDisk(IReadOnlyList<RemovableDisk> disks) => Ask(() => new ChooseDiskViewModel(disks));

    public bool ConfirmErase(RemovableDisk disk) => Ask(() => new ConfirmEraseViewModel(disk));

    public BootPartition ChooseCard(IReadOnlyList<BootPartition> cards) => Ask(() => new ChooseCardViewModel(cards));

    public SettingsAnswers AskSettings(SettingsForm form) => Ask(() => new SettingsViewModel(form));

    public string AskPiName(string? suggested) => Ask(() => new PiNameViewModel(suggested));

    public bool ConfirmUpdate(UpdatePlan plan) => Ask(() => new ConfirmUpdateViewModel(plan));

    public bool CreateSshKey(string publicKeyFile) => Ask(() => new SshKeyViewModel(publicKeyFile));

    /// <summary>Shows the page (on the window's thread) and waits for its answer; cancelled, the job stops.</summary>
    T Ask<T>(Func<AskViewModel<T>> page)
    {
        var shown = new TaskCompletionSource<AskViewModel<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
        dispatch(() =>
        {
            var asking = page();
            window.Show(asking);
            shown.SetResult(asking);
        });
        try
        {
            return shown.Task.GetAwaiter().GetResult().Answer.GetAwaiter().GetResult();
        }
        finally
        {
            dispatch(() => window.Show(job));
        }
    }
}
