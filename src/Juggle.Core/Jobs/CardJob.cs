namespace Juggle.Core.Jobs;

/// <summary>What the job with administrator rights is given.</summary>
/// <param name="Disk">The card chosen, as it was listed: the job checks it's still the same card.</param>
/// <param name="Image">The image to write (a full path), or null to only put the settings on the card.</param>
/// <param name="Settings">The Pi's settings, for the card's boot partition.</param>
/// <param name="Eject">Whether to eject the card safely once it's done.</param>
public sealed record CardRequest(RemovableDisk Disk, string? Image, DeviceSettings Settings, bool Eject);

/// <summary>How a card job ended.</summary>
public enum CardOutcome
{
    /// <summary>It stopped, having said why.</summary>
    Failed,

    /// <summary>The card is done, and still in the system (not asked to eject it, or it couldn't).</summary>
    Done,

    /// <summary>The card is done and ejected: it can be taken out.</summary>
    Ejected,
}

/// <summary>Writes a card: here (<see cref="CardJob"/>), or in a process with administrator rights.</summary>
public interface ICardJob
{
    CardOutcome Run(CardRequest request);
}

/// <summary>The cards in card readers, and what's done to them; tests have their own.</summary>
public interface ICards
{
    IReadOnlyList<RemovableDisk> FindAll();

    void Write(RemovableDisk disk, string image, IProgress<WriteProgress> progress);

    /// <summary>The card's boot partition, waiting for the system to find it after a write if wait.</summary>
    BootPartition? FindBootPartition(RemovableDisk disk, bool wait);

    /// <summary>Ejects the card, throwing IOException (saying why) if it can't.</summary>
    void Eject(RemovableDisk disk);
}

public sealed class SystemCards : ICards
{
    public static SystemCards Instance { get; } = new();

    public IReadOnlyList<RemovableDisk> FindAll() => RemovableDisk.FindAll();

    public void Write(RemovableDisk disk, string image, IProgress<WriteProgress> progress) => ImageWriter.Write(disk, image, progress);

    public BootPartition? FindBootPartition(RemovableDisk disk, bool wait) =>
        wait ? BootPartition.WaitFor(disk) : BootPartition.Find(disk);

    public void Eject(RemovableDisk disk) => CardEject.Eject(disk);
}

/// <summary>
/// The part of flashing and setting up that needs administrator rights (root on Linux): checks the card is still the
/// one chosen, writes the image to it and checks it, puts the settings on its boot partition, and ejects it.
/// </summary>
public sealed class CardJob(IJobReport report, ICards? cards = null) : ICardJob
{
    readonly ICards cards = cards ?? SystemCards.Instance;

    public CardOutcome Run(CardRequest request)
    {
        // Still the card chosen: the same disk, reader and size, and still a card in a card reader
        var chosen = request.Disk;
        var disk = cards.FindAll().FirstOrDefault(candidate =>
            candidate.Id == chosen.Id && candidate.Name == chosen.Name && candidate.Size == chosen.Size);
        if (disk is null)
        {
            report.Problem($"The card chosen ({chosen.Description}) isn't in the card reader any more: nothing was written.");
            return CardOutcome.Failed;
        }

        if (request.Image is { } image)
        {
            if (BlockMap.ForImage(image) is { } map)
            {
                report.Say($"Writing {map.MappedBytes / (1 << 20)} MiB of the {map.ImageSize / (1 << 20)} MiB image " +
                    "(the rest is empty space), then reading it back to check it.");
            }
            try
            {
                cards.Write(disk, image, new DirectProgress<WriteProgress>(progress => report.Progress(
                    new JobProgress(progress.Verifying ? JobStage.Check : JobStage.Write, progress.Bytes, progress.Total))));
            }
            finally
            {
                report.Progress(null);
            }
            report.Say("Written and checked.");
        }

        using (var card = cards.FindBootPartition(disk, wait: request.Image is not null))
        {
            if (card is null)
            {
                report.Problem(request.Image is null
                    ? "The card has no Juggle OS boot partition: write the image to it with juggle flash."
                    : "The system didn't show the card's boot partition: take the card out, put it back in and run " +
                        "juggle setup.");
                return CardOutcome.Failed;
            }
            SetupJob.Describe(card, report);
            card.WriteSettings(request.Settings);
        }
        return request.Eject ? Eject(cards, disk, report) : CardOutcome.Done;
    }

    /// <summary>Ejects a card that's done, saying so if it can't.</summary>
    internal static CardOutcome Eject(ICards cards, RemovableDisk disk, IJobReport report)
    {
        try
        {
            cards.Eject(disk);
            return CardOutcome.Ejected;
        }
        catch (IOException error)
        {
            report.Problem($"The card wasn't ejected ({error.Message.TrimEnd('.')}): eject it before taking it out.");
            return CardOutcome.Done;
        }
    }
}
