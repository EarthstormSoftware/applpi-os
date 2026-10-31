using System.Globalization;

namespace Juggle.Core.Jobs;

/// <summary>A Pi's settings, asked for and ready for its card, with what's remembered once they're on it.</summary>
/// <param name="KeyFile">The SSH public key file its keys came from.</param>
/// <param name="HostPublicKey">Its host key's public half, for ssh's known_hosts.</param>
public sealed record SetupPlan(DeviceSettings Settings, string KeyFile, string HostPublicKey);

/// <summary>
/// Puts a Pi's settings (name, Wi-Fi, time zone, SSH key, host key) on a Juggle card, remembers them for next time, and
/// has ssh trust the Pi's host key. The Pi reads them at its next boot, then deletes them from the card. Asking for
/// them and remembering them is done as the user; only writing a card may need administrator rights (see
/// <see cref="FlashJob"/>, and on Linux a boot partition the desktop hasn't mounted).
/// </summary>
public sealed class SetupJob(IJobHost host, JugglePaths? paths = null,
    Func<(string PrivateKey, string PublicKey)>? createHostKey = null, ICards? cards = null)
{
    readonly JugglePaths paths = paths ?? JugglePaths.Default;
    readonly Func<(string PrivateKey, string PublicKey)> createHostKey = createHostKey ?? SshKeys.CreateHostKey;
    readonly ICards cards = cards ?? SystemCards.Instance;

    /// <summary>
    /// juggle setup: sets up one of the Juggle cards found (asking which, if there are several), ejecting it after if
    /// eject. Cards are the boot partitions this user can write; unmounted are cards (Linux) whose boot partition only
    /// root can mount, set up by cardJob. Returns whether it did.
    /// </summary>
    public bool Run(string? name, IReadOnlyList<BootPartition> cards, IReadOnlyList<RemovableDisk> unmounted,
        ICardJob cardJob, bool eject)
    {
        if (cards.Count > 0)
        {
            var card = cards.Count == 1 ? cards[0] : host.ChooseCard(cards);
            if (cards.Count == 1)
            {
                host.Say($"Card: {card.Description}");
            }
            Describe(card, host);
            if (Prepare(name) is not { } plan)
            {
                return false;
            }
            card.WriteSettings(plan.Settings);
            // Unmounted first, if juggle mounted it
            card.Dispose();
            var outcome = eject && card.Disk is { } disk ? CardJob.Eject(this.cards, disk, host) : CardOutcome.Done;
            Finish(plan, outcome);
            return true;
        }
        if (unmounted.Count > 0)
        {
            var disk = unmounted.Count == 1 ? unmounted[0] : host.ChooseDisk(unmounted);
            if (unmounted.Count == 1)
            {
                host.Say($"Card: {disk.Description}");
            }
            if (Prepare(name) is not { } plan)
            {
                return false;
            }
            var outcome = cardJob.Run(new CardRequest(disk, null, plan.Settings, eject));
            if (outcome == CardOutcome.Failed)
            {
                return false;
            }
            Finish(plan, outcome);
            return true;
        }
        host.Problem("No Juggle card found. Put a card written with the Juggle OS image in the card reader and try again.");
        return false;
    }

    /// <summary>What's on a card already: its image, and the Pi's message about the last settings, if it left one.</summary>
    public static void Describe(BootPartition card, IJobReport report)
    {
        if (card.ReadRelease() is var (version, built))
        {
            report.Say($"Image: Juggle OS {version}, build {built}");
        }
        if (card.ReadError() is { } error)
        {
            report.Say($"The Pi left a message on this card about the last settings: {error}");
        }
        if (card.HasPendingSettings)
        {
            report.Say("The card already has settings the Pi hasn't read yet: these will replace them.");
        }
    }

    /// <summary>
    /// Asks for a Pi's settings (offering name), making the user an SSH key if they have none, and the Pi a host key the
    /// first time. Null (having said why) if they won't work.
    /// </summary>
    public SetupPlan? Prepare(string? name)
    {
        var store = DeviceStore.Load(paths.DeviceStore);
        var answers = host.AskSettings(new SettingsForm(name?.ToLowerInvariant() ?? store.LastDevice,
            hostname => DefaultsFor(store, hostname)));
        var hostname = answers.Hostname.ToLowerInvariant();

        var known = Known(store, hostname);
        var keyFile = known?.SshKeyFile is { Length: > 0 } stored && File.Exists(stored) ? stored : paths.PublicKey;
        if (!File.Exists(keyFile) && !host.CreateSshKey(keyFile))
        {
            return null;
        }
        var keys = SshKeys.Read(keyFile);

        // Each Pi keeps one host key, made here the first time, so reflashing it doesn't change its identity
        var hostKey = store.Get(hostname)?.Settings.HostKey;
        var hostPublicKey = store.GetHostPublicKey(hostname);
        if (hostKey is null || hostPublicKey is null)
        {
            (hostKey, hostPublicKey) = createHostKey();
        }
        var settings = new DeviceSettings(hostname, answers.TimeZone, answers.Wifi, keys, hostKey);
        var errors = SettingsRules.Check(settings);
        if (errors.Count > 0)
        {
            host.Problem($"These settings won't work ({keyFile}): {string.Join(" ", errors)}");
            return null;
        }
        return new SetupPlan(settings, keyFile, hostPublicKey);
    }

    /// <summary>Once the settings are on the card: remembers them, has ssh trust the Pi, and says what's next.</summary>
    public void Finish(SetupPlan plan, CardOutcome outcome)
    {
        var hostname = plan.Settings.Hostname;
        var store = DeviceStore.Load(paths.DeviceStore);
        store.Set(plan.Settings, plan.KeyFile, plan.HostPublicKey);
        store.Save(paths.DeviceStore);
        SshKeys.TrustHostKey(paths.KnownHosts, PiShell.HostFor(hostname), plan.HostPublicKey);

        host.Say("");
        host.Say($"Settings for {hostname} are on the card, and ssh on this computer knows its host key.");
        host.Say((outcome == CardOutcome.Ejected
            ? "The card is ejected: take it out, put it in the Pi and power it on."
            : "Eject the card, put it in the Pi and power it on.") +
            " After a few minutes (the first boot also sets up the card):");
        host.Say($"  ssh {PiShell.User}@{PiShell.HostFor(hostname)}");
        if (plan.Settings.Wifi is null)
        {
            host.Say("No Wi-Fi: connect the Pi with an Ethernet cable.");
        }
    }

    // A Pi set up before keeps its settings; a new one starts from the last Pi's
    static (DeviceSettings Settings, string SshKeyFile)? Known(DeviceStore store, string hostname) =>
        store.Get(hostname) ?? (store.LastDevice is null ? null : store.Get(store.LastDevice));

    static SettingsDefaults DefaultsFor(DeviceStore store, string hostname)
    {
        var known = Known(store, hostname.ToLowerInvariant())?.Settings;
        return new SettingsDefaults(known?.TimeZone ?? LocalTimeZone(), known?.Wifi,
            known?.Wifi?.Country is { Length: > 0 } country ? country : LocalCountry());
    }

    /// <summary>This computer's time zone as an IANA name (Linux's already is), the default for a new Pi.</summary>
    internal static string LocalTimeZone() => TimeZoneInfo.Local.HasIanaId
        ? TimeZoneInfo.Local.Id
        : TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : "Etc/UTC";

    /// <summary>
    /// This computer's country, the default for a new Pi's Wi-Fi: Windows' region, or on Linux (where juggle runs without
    /// the system's region data) the country of LANG, such as GB in en_GB.UTF-8. Null if neither gives one.
    /// </summary>
    internal static string? LocalCountry()
    {
        var country = OperatingSystem.IsWindows()
            ? RegionInfo.CurrentRegion.TwoLetterISORegionName
            : Environment.GetEnvironmentVariable("LANG")?.Split('.', '@')[0].Split('_') is [_, var region] ? region : "";
        return SettingsRules.CheckCountry(country) is null ? country : null;
    }
}
