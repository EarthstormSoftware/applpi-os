using Juggle.Core;
using Juggle.Core.Jobs;

namespace Juggle.Core.Tests.Jobs;

/// <summary>A host that answers as the test says and keeps everything it was told.</summary>
sealed class FakeHost : IJobHost
{
    public List<string> Said { get; } = [];
    public List<string> Problems { get; } = [];
    public List<JobProgress?> Progresses { get; } = [];

    public SettingsAnswers Settings { get; set; } = new("juggle-test", "Europe/London", null);
    public SettingsForm? Form { get; private set; }
    public bool Erase { get; set; } = true;
    public bool Update { get; set; } = true;
    public string PiName { get; set; } = "juggle-test";
    public Func<string, bool> CreateKey { get; set; } = _ => false;

    public void Say(string text) => Said.Add(text);
    public void Problem(string text) => Problems.Add(text);
    public void Progress(JobProgress? progress) => Progresses.Add(progress);

    public RemovableDisk ChooseDisk(IReadOnlyList<RemovableDisk> disks) => disks[0];
    public bool ConfirmErase(RemovableDisk disk) => Erase;
    public BootPartition ChooseCard(IReadOnlyList<BootPartition> cards) => cards[^1];

    public SettingsAnswers AskSettings(SettingsForm form)
    {
        Form = form;
        return Settings;
    }

    public string AskPiName(string? suggested) => PiName;
    public bool ConfirmUpdate(UpdatePlan plan) => Update;
    public bool CreateSshKey(string publicKeyFile) => CreateKey(publicKeyFile);
}

/// <summary>Card readers with the cards the test gives, writing nothing.</summary>
sealed class FakeCards : ICards
{
    public static readonly RemovableDisk Disk = new("2", "Card Reader", 16_000_000_000, [], []);

    public List<RemovableDisk> Disks { get; set; } = [Disk];
    public BootPartition? BootPartition { get; set; }
    public List<(RemovableDisk Disk, string Image)> Written { get; } = [];
    public List<RemovableDisk> Ejected { get; } = [];

    /// <summary>Why ejecting fails, or null if it works.</summary>
    public string? EjectFails { get; set; }

    public IReadOnlyList<RemovableDisk> FindAll() => Disks;

    public void Write(RemovableDisk disk, string image, IProgress<WriteProgress> progress)
    {
        Written.Add((disk, image));
        progress.Report(new WriteProgress(false, 1 << 19, 1 << 20));
        progress.Report(new WriteProgress(true, 1 << 20, 1 << 20));
    }

    public BootPartition? FindBootPartition(RemovableDisk disk, bool wait) => BootPartition;

    public void Eject(RemovableDisk disk)
    {
        if (EjectFails is { } why)
        {
            throw new IOException(why);
        }
        Ejected.Add(disk);
    }
}

/// <summary>A card job that keeps the request and ends as the test says.</summary>
sealed class FakeCardJob(CardOutcome outcome = CardOutcome.Ejected) : ICardJob
{
    public List<CardRequest> Requests { get; } = [];

    public CardOutcome Run(CardRequest request)
    {
        Requests.Add(request);
        return outcome;
    }
}

/// <summary>Time that passes only when the job sleeps.</summary>
sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; private set; } = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

    public void Sleep(TimeSpan time) => UtcNow += time;
}

/// <summary>
/// A Pi that answers os-update's commands from a script: each status is the next answer in Statuses (null: it doesn't
/// answer; "!": os-update refuses, as on an OS from before updates), or the last one once they run out.
/// </summary>
sealed class ScriptedPi(params string?[] statuses) : IPi
{
    readonly Queue<string?> statuses = new(statuses);
    string? last;

    public string Host => "juggle-test.local";

    public List<string> Commands { get; } = [];

    /// <summary>Why write fails, or null if it works.</summary>
    public string? WriteFails { get; set; }

    public string Run(string command, Action<string>? onMessage = null, int connectTimeoutSeconds = 15)
    {
        Commands.Add(command);
        var verb = command.Split(' ')[^1];
        switch (verb)
        {
            case "status":
                if (statuses.Count > 0)
                {
                    last = statuses.Dequeue();
                }
                return last switch
                {
                    null => throw new PiCommandException(255, "ssh: connect to host juggle-test.local: timed out"),
                    "!" => throw new PiCommandException(1, "sudo: a password is required"),
                    _ => last,
                };
            case "try":
                onMessage?.Invoke("Restarting into the other slot");
                throw new PiCommandException(255, "Connection closed");
            default:
                return "";
        }
    }

    public string Run(string command, string inputFile, IProgress<long> progress, Action<string>? onMessage = null)
    {
        Commands.Add(command);
        var size = new FileInfo(inputFile).Length;
        progress.Report(size / 2);
        onMessage?.Invoke("Writing slot B");
        if (WriteFails is { } why)
        {
            throw new PiCommandException(1, why);
        }
        progress.Report(size);
        return "";
    }

    /// <summary>What os-update status says.</summary>
    public static string Status(string boot, string running, string permanent, bool tryboot = false, string? check = "",
        string slotA = "v0.7.0 (build 20261030-1200)", string slotB = "", string features = "map", string? last = null) =>
        $"""
        boot={boot}
        running={running}
        tryboot={(tryboot ? 1 : 0)}
        permanent={permanent}
        waiting=
        {(check is null ? "" : $"check={check}")}
        features={features}
        slotA={slotA}
        slotB={slotB}
        {(last is null ? "" : $"last={last}")}
        """;
}
