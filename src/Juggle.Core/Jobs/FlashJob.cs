namespace Juggle.Core.Jobs;

/// <summary>
/// Flashes a card in a card reader: asks which card and the Pi's settings, confirms, then has the card job (with
/// administrator rights) write the image, check it, put the settings on it and eject it. Only cards in card readers are
/// offered, and the host confirms, naming the card, before anything is written.
/// </summary>
public sealed class FlashJob(IJobHost host, SetupJob setup, ICardJob cardJob, ICards? cards = null)
{
    readonly ICards cards = cards ?? SystemCards.Instance;

    /// <summary>
    /// Writes the image (a full path, see <see cref="ImageSource"/>) and sets the card up, ejecting it after if eject.
    /// Returns whether it did.
    /// </summary>
    public bool Run(string image, bool eject)
    {
        host.Say($"Image: {Path.GetFileName(image)}");
        var disks = cards.FindAll();
        if (disks.Count == 0)
        {
            host.Problem("No card found: put the card in the card reader and try again.");
            return false;
        }
        var disk = host.ChooseDisk(disks);
        if (setup.Prepare(null) is not { } plan)
        {
            return false;
        }
        if (!host.ConfirmErase(disk))
        {
            host.Say("Stopped: nothing was written.");
            return false;
        }

        var outcome = cardJob.Run(new CardRequest(disk, image, plan.Settings, eject));
        if (outcome == CardOutcome.Failed)
        {
            return false;
        }
        setup.Finish(plan, outcome);
        return true;
    }
}

/// <summary>Reports progress on the calling thread (Progress&lt;T&gt; would post it to the thread pool, out of order).</summary>
sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
