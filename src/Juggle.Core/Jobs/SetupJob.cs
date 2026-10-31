using System.Globalization;

namespace Juggle.Core.Jobs;

/// <summary>
/// Puts a Pi's settings (name, Wi-Fi, time zone, SSH key, host key) on a Juggle card, remembers them for next time, and
/// has ssh trust the Pi's host key. The Pi reads them at its next boot, then deletes them from the card.
/// </summary>
public sealed class SetupJob(IJobHost host, JugglePaths? paths = null,
    Func<(string PrivateKey, string PublicKey)>? createHostKey = null)
{
    readonly JugglePaths paths = paths ?? JugglePaths.Default;
    readonly Func<(string PrivateKey, string PublicKey)> createHostKey = createHostKey ?? SshKeys.CreateHostKey;

    /// <summary>Sets up one of the Juggle cards found (asking which, if there are several). Returns whether it did.</summary>
    public bool Run(string? name, IReadOnlyList<BootPartition> cards)
    {
        switch (cards.Count)
        {
            case 0:
                host.Problem("No Juggle card found. Put a card written with the Juggle OS image in the card reader and try again.");
                return false;
            case 1:
                host.Say($"Card: {cards[0].Description}");
                return Run(name, cards[0]);
            default:
                return Run(name, host.ChooseCard(cards));
        }
    }

    /// <summary>Sets up this card. Returns whether it did.</summary>
    public bool Run(string? name, BootPartition card)
    {
        if (card.ReadRelease() is var (version, built))
        {
            host.Say($"Image: Juggle OS {version}, build {built}");
        }
        if (card.ReadError() is { } error)
        {
            host.Say($"The Pi left a message on this card about the last settings: {error}");
        }
        if (card.HasPendingSettings)
        {
            host.Say("The card already has settings the Pi hasn't read yet: these will replace them.");
        }

        var store = DeviceStore.Load(paths.DeviceStore);
        var answers = host.AskSettings(new SettingsForm(name?.ToLowerInvariant() ?? store.LastDevice,
            hostname => DefaultsFor(store, hostname)));
        var hostname = answers.Hostname.ToLowerInvariant();

        var known = Known(store, hostname);
        var keyFile = known?.SshKeyFile is { Length: > 0 } stored && File.Exists(stored) ? stored : paths.PublicKey;
        if (!File.Exists(keyFile) && !host.CreateSshKey(keyFile))
        {
            return false;
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
            return false;
        }

        card.WriteSettings(settings);
        store.Set(settings, keyFile, hostPublicKey);
        store.Save(paths.DeviceStore);
        SshKeys.TrustHostKey(paths.KnownHosts, PiShell.HostFor(hostname), hostPublicKey);

        host.Say("");
        host.Say($"Settings for {hostname} are on the card ({card.Name}), and ssh on this computer knows its host key.");
        host.Say("Eject the card, put it in the Pi and power it on. After a few minutes (the first boot also sets up the card):");
        host.Say($"  ssh {PiShell.User}@{PiShell.HostFor(hostname)}");
        if (answers.Wifi is null)
        {
            host.Say("No Wi-Fi: connect the Pi with an Ethernet cable.");
        }
        return true;
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
