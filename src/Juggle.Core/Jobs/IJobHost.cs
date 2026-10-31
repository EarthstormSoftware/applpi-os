namespace Juggle.Core.Jobs;

/// <summary>
/// Where a job reports what's happening: the host that runs it, or (for the job with administrator rights) the stream
/// back to it.
/// </summary>
public interface IJobReport
{
    /// <summary>Something that happened, or what comes next. An empty line separates parts of a long output.</summary>
    void Say(string text);

    /// <summary>Why the job stopped, or couldn't do something.</summary>
    void Problem(string text);

    /// <summary>How far the job has got, replacing the last progress; null when that stage is over.</summary>
    void Progress(JobProgress? progress);
}

/// <summary>
/// Whoever runs a job: juggle in a terminal, or Juggle Flash in its window. A job tells it what's happening and asks it
/// for each decision, with one method per decision, so a window can show a screen for each.
/// </summary>
public interface IJobHost : IJobReport
{
    /// <summary>Which of the cards in card readers to write to, or to set up (there are some).</summary>
    RemovableDisk ChooseDisk(IReadOnlyList<RemovableDisk> disks);

    /// <summary>Whether to erase this card and write the image to it.</summary>
    bool ConfirmErase(RemovableDisk disk);

    /// <summary>Which of the Juggle cards (more than one) to set up.</summary>
    BootPartition ChooseCard(IReadOnlyList<BootPartition> cards);

    /// <summary>A Pi's settings: its name, then the rest, starting from the form's defaults for that name.</summary>
    SettingsAnswers AskSettings(SettingsForm form);

    /// <summary>The name of the Pi to update.</summary>
    string AskPiName(string? suggested);

    /// <summary>Whether to go ahead with the update the job has described.</summary>
    bool ConfirmUpdate(UpdatePlan plan);

    /// <summary>Makes the user an SSH key, which they log in to their Pis with; false if none was made.</summary>
    bool CreateSshKey(string publicKeyFile);
}

/// <summary>What a job is doing, and how far it has got.</summary>
public enum JobStage
{
    /// <summary>Downloading a release's image (Item: the file).</summary>
    Download,

    /// <summary>Writing the image to a card.</summary>
    Write,

    /// <summary>Reading the card back to check it.</summary>
    Check,

    /// <summary>Sending the image to a Pi over SSH.</summary>
    Send,

    /// <summary>Waiting for a Pi to come back from a restart (Done: how many times it didn't answer).</summary>
    WaitForRestart,
}

/// <param name="Done">Bytes so far, or attempts for <see cref="JobStage.WaitForRestart"/>.</param>
/// <param name="Total">Bytes in all, if known.</param>
/// <param name="Item">What it's working on, such as the file being downloaded.</param>
public readonly record struct JobProgress(JobStage Stage, long Done, long? Total = null, string? Item = null)
{
    public double Fraction => Total is > 0 and var total ? Math.Min(1, (double)Done / total) : 0;
}

/// <summary>
/// The settings to ask for. Hostname is the name to offer; DefaultsFor gives the rest for a name: a Pi set up before
/// keeps its own, and a new one starts from the last Pi's.
/// </summary>
public sealed record SettingsForm(string? Hostname, Func<string, SettingsDefaults> DefaultsFor);

/// <param name="TimeZone">The time zone to offer (IANA, such as Europe/London).</param>
/// <param name="Wifi">The Wi-Fi saved for it, password included, or null (offer none).</param>
/// <param name="Country">The Wi-Fi country to offer, or null.</param>
public sealed record SettingsDefaults(string TimeZone, WifiSettings? Wifi, string? Country);

/// <summary>What was asked for: the name in lower case, and no Wi-Fi for Ethernet only. Each passes SettingsRules.</summary>
public sealed record SettingsAnswers(string Hostname, string TimeZone, WifiSettings? Wifi);

/// <summary>An update about to be written: the Pi, its slots now, the slot it goes into and the image.</summary>
public sealed record UpdatePlan(string Name, SlotStatus Before, string Target, string Image);

/// <summary>The files juggle keeps, which tests put in a folder of their own.</summary>
/// <param name="DeviceStore">The settings of each Pi.</param>
/// <param name="KnownHosts">ssh's known_hosts, where each Pi's host key is trusted.</param>
/// <param name="PublicKey">The SSH key a new Pi is given.</param>
public sealed record JugglePaths(string DeviceStore, string KnownHosts, string PublicKey)
{
    public static JugglePaths Default { get; } =
        new(Core.DeviceStore.DefaultPath, SshKeys.DefaultKnownHostsFile, SshKeys.DefaultPublicKeyFile);
}

/// <summary>The time, and waiting, which tests make instant.</summary>
public interface IClock
{
    DateTime UtcNow { get; }

    void Sleep(TimeSpan time);
}

public sealed class SystemClock : IClock
{
    public static SystemClock Instance { get; } = new();

    public DateTime UtcNow => DateTime.UtcNow;

    public void Sleep(TimeSpan time) => Thread.Sleep(time);
}
