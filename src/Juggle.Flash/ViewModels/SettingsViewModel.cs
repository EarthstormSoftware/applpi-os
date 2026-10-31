using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Juggle.Core;
using Juggle.Core.Jobs;

namespace Juggle.Flash.ViewModels;

/// <summary>
/// A Pi's settings: its name, then its time zone and Wi-Fi, filled in from that name's defaults (a Pi set up before
/// keeps its own, a new one starts from the last Pi's) each time the name changes. Each is checked as it's typed, by
/// the same rules as juggle's.
/// </summary>
public sealed partial class SettingsViewModel : AskViewModel<SettingsAnswers>
{
    readonly SettingsForm form;

    // The Wi-Fi saved for the name, whose password is kept if it's left empty
    WifiSettings? saved;

    public SettingsViewModel(SettingsForm form)
    {
        this.form = form;
        hostname = form.Hostname ?? "";
        Fill(hostname);
    }

    public override string Title => "The Pi's settings";

    /// <summary>The time zones to choose from, as IANA names the Pi takes (Windows' own list has "UTC", say).</summary>
    public static IReadOnlyList<string> TimeZones { get; } = TimeZoneInfo.GetSystemTimeZones()
        .Select(zone => zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : null)
        .OfType<string>()
        .Where(zone => SettingsRules.CheckTimeZone(zone) is null)
        .Distinct()
        .Order(StringComparer.Ordinal)
        .ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostnameError), nameof(Address))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    string hostname;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeZoneError))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    string timeZone = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SsidError), nameof(PasswordError), nameof(CountryError), nameof(KeepsPassword))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    bool useWifi;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SsidError), nameof(PasswordError), nameof(KeepsPassword))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    string ssid = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordError))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    string password = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountryError))]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    string country = "";

    string Name => Hostname.Trim().ToLowerInvariant();

    /// <summary>How the Pi is reached once it's up.</summary>
    public string Address => Name.Length > 0 && HostnameError is null ? $"ssh {PiShell.User}@{PiShell.HostFor(Name)}" : "";

    /// <summary>Whether an empty password keeps the one saved for this network.</summary>
    public bool KeepsPassword => UseWifi && saved is { Password.Length: > 0 } && saved.Ssid == Ssid.Trim();

    public string? HostnameError => Name.Length == 0 ? null : SettingsRules.CheckHostname(Name);

    public string? TimeZoneError => TimeZone.Trim().Length == 0 ? null : SettingsRules.CheckTimeZone(TimeZone.Trim());

    public string? SsidError => !UseWifi || Ssid.Length == 0 ? null : SettingsRules.CheckSsid(Ssid.Trim());

    public string? PasswordError => !UseWifi || Password.Length == 0 ? null : SettingsRules.CheckPassword(Password);

    public string? CountryError => !UseWifi || Country.Length == 0 ? null : SettingsRules.CheckCountry(Country.Trim().ToUpperInvariant());

    bool CanContinue => Name.Length > 0 && HostnameError is null
        && TimeZone.Trim().Length > 0 && TimeZoneError is null
        && (!UseWifi || Ssid.Trim().Length > 0 && SsidError is null
            && (Password.Length > 0 ? PasswordError is null : KeepsPassword)
            && Country.Trim().Length > 0 && CountryError is null);

    [RelayCommand(CanExecute = nameof(CanContinue))]
    void Continue() => Give(new SettingsAnswers(Name, TimeZone.Trim(),
        UseWifi
            ? new WifiSettings(Country.Trim().ToUpperInvariant(), Ssid.Trim(), Password.Length > 0 ? Password : saved!.Password)
            : null));

    partial void OnHostnameChanged(string value)
    {
        if (SettingsRules.CheckHostname(value.Trim().ToLowerInvariant()) is null)
        {
            Fill(value);
        }
    }

    // The defaults for a name (with none yet, the last Pi's): the time zone, and the Wi-Fi without its password (which
    // stays saved, not shown)
    void Fill(string name)
    {
        var defaults = form.DefaultsFor(name.Trim().ToLowerInvariant());
        saved = defaults.Wifi;
        TimeZone = defaults.TimeZone;
        UseWifi = saved is not null;
        Ssid = saved?.Ssid ?? "";
        Password = "";
        Country = defaults.Country ?? "";
        OnPropertyChanged(nameof(KeepsPassword));
        ContinueCommand.NotifyCanExecuteChanged();
    }
}
