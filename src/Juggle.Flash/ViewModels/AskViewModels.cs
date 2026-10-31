using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Juggle.Core;
using Juggle.Core.Jobs;

namespace Juggle.Flash.ViewModels;

/// <summary>
/// A page that asks a job's question. The job waits for Answer: given by the page, or cancelled, which stops the job
/// (nothing has been written while a question is open).
/// </summary>
public abstract partial class AskViewModel<T> : PageViewModel
{
    readonly TaskCompletionSource<T> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<T> Answer => answer.Task;

    protected void Give(T value) => answer.TrySetResult(value);

    [RelayCommand]
    protected virtual void Cancel() => answer.TrySetException(new OperationCanceledException());
}

/// <summary>Which card to write to, or to set up.</summary>
public sealed partial class ChooseDiskViewModel(IReadOnlyList<RemovableDisk> disks) : AskViewModel<RemovableDisk>
{
    public override string Title => "Which card?";

    public IReadOnlyList<RemovableDisk> Disks => disks;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    RemovableDisk? selected = disks.Count == 1 ? disks[0] : null;

    bool CanContinue => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanContinue))]
    void Continue() => Give(Selected!);
}

/// <summary>The last word before a card is erased, naming it.</summary>
public sealed partial class ConfirmEraseViewModel(RemovableDisk disk) : AskViewModel<bool>
{
    public override string Title => "Erase this card?";

    public string Card => disk.Description;

    [RelayCommand]
    void Erase() => Give(true);

    // Cancelling here is an answer: the job says nothing was written
    protected override void Cancel() => Give(false);
}

/// <summary>Which of the Juggle cards to set up.</summary>
public sealed partial class ChooseCardViewModel(IReadOnlyList<BootPartition> cards) : AskViewModel<BootPartition>
{
    public override string Title => "Which card?";

    public IReadOnlyList<string> Cards { get; } = cards.Select(card => card.Description).ToList();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    int selectedIndex = -1;

    bool CanContinue => SelectedIndex >= 0;

    [RelayCommand(CanExecute = nameof(CanContinue))]
    void Continue() => Give(cards[SelectedIndex]);
}

/// <summary>Whether to update a Pi, as the job described it.</summary>
public sealed partial class ConfirmUpdateViewModel(UpdatePlan plan) : AskViewModel<bool>
{
    public override string Title => $"Update {plan.Name}?";

    public string Running => $"It runs slot {plan.Before.Running}: Juggle OS {plan.Before.Holds(plan.Before.Running)}.";

    public string Target => $"Slot {plan.Target} ({plan.Before.Holds(plan.Target)}) gets {Path.GetFileName(plan.Image)}.";

    [RelayCommand]
    void Update() => Give(true);

    protected override void Cancel() => Give(false);
}

/// <summary>The name of the Pi to update (when the job wasn't given one).</summary>
public sealed partial class PiNameViewModel(string? suggested) : AskViewModel<string>
{
    public override string Title => "Which Pi?";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    string name = suggested ?? "";

    public string? Error => Name.Length == 0 ? null : SettingsRules.CheckHostname(Name.Trim().ToLowerInvariant());

    bool CanContinue => Name.Trim().Length > 0 && Error is null;

    [RelayCommand(CanExecute = nameof(CanContinue))]
    void Continue() => Give(Name.Trim().ToLowerInvariant());
}

/// <summary>
/// The user has no SSH key yet. ssh-keygen asks for the key's passphrase in a terminal, so this page shows the command
/// and checks again once it has been run.
/// </summary>
public sealed partial class SshKeyViewModel(string publicKeyFile) : AskViewModel<bool>
{
    public override string Title => "Make an SSH key";

    public string KeyFile => publicKeyFile;

    public string Command => $"ssh-keygen -t ed25519 -f \"{publicKeyFile[..^".pub".Length]}\"";

    [ObservableProperty]
    string? problem;

    [RelayCommand]
    void Done()
    {
        if (File.Exists(publicKeyFile))
        {
            Give(true);
            return;
        }
        Problem = $"There's no key at {publicKeyFile} yet.";
    }

    protected override void Cancel() => Give(false);
}
