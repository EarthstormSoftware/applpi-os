namespace Juggle.Core.Jobs;

/// <summary>
/// Writes the Juggle OS image to a card in a card reader, checks it, then sets it up (<see cref="SetupJob"/>). Needs
/// administrator rights (root on Linux). Only cards in card readers are offered, and the host confirms before writing.
/// </summary>
public sealed class FlashJob(IJobHost host, SetupJob setup,
    Func<IReadOnlyList<RemovableDisk>>? findDisks = null,
    Action<RemovableDisk, string, IProgress<WriteProgress>>? write = null,
    Func<RemovableDisk, BootPartition?>? waitForBootPartition = null)
{
    readonly Func<IReadOnlyList<RemovableDisk>> findDisks = findDisks ?? RemovableDisk.FindAll;
    readonly Action<RemovableDisk, string, IProgress<WriteProgress>> write = write ?? ImageWriter.Write;
    readonly Func<RemovableDisk, BootPartition?> waitForBootPartition = waitForBootPartition ?? BootPartition.WaitFor;

    /// <summary>Writes the image (a full path, see <see cref="ImageSource"/>) and sets the card up. Returns whether it did.</summary>
    public bool Run(string image)
    {
        host.Say($"Image: {Path.GetFileName(image)}");
        var disks = findDisks();
        if (disks.Count == 0)
        {
            host.Problem("No card found: put the card in the card reader and try again.");
            return false;
        }
        var disk = host.ChooseDisk(disks);
        if (!host.ConfirmErase(disk))
        {
            host.Say("Stopped: nothing was written.");
            return false;
        }

        if (BlockMap.ForImage(image) is { } map)
        {
            host.Say($"Writing {map.MappedBytes / (1 << 20)} MiB of the {map.ImageSize / (1 << 20)} MiB image " +
                "(the rest is empty space), then reading it back to check it.");
        }
        try
        {
            write(disk, image, new DirectProgress<WriteProgress>(progress => host.Progress(
                new JobProgress(progress.Verifying ? JobStage.Check : JobStage.Write, progress.Bytes, progress.Total))));
        }
        finally
        {
            host.Progress(null);
        }
        host.Say("Written and checked.");

        using var card = waitForBootPartition(disk);
        if (card is null)
        {
            host.Problem("The system didn't show the card's boot partition: take the card out, put it back in and run " +
                "juggle setup.");
            return false;
        }
        return setup.Run(null, card);
    }
}

/// <summary>Reports progress on the calling thread (Progress&lt;T&gt; would post it to the thread pool, out of order).</summary>
sealed class DirectProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
